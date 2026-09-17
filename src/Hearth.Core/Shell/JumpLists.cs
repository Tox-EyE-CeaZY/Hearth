using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using Hearth.Core.Interop;

namespace Hearth.Core.Shell;

public enum JumpListCategoryKind
{
    /// <summary>A category the app named itself ("Pinned", "Recent projects").</summary>
    Custom,
    Recent,
    Frequent,
    /// <summary>Actions such as "New private window".</summary>
    Tasks,
}

/// <summary>
/// One jump list row. <see cref="LinkData"/> is the serialised shell link the
/// app stored, which is what gets launched: it carries arguments, working
/// directory and the app's own identity exactly as the app wrote them.
/// </summary>
public sealed record JumpListEntry(
    string Title,
    string? ParsingName,
    string? Arguments,
    string? IconPath,
    int IconIndex,
    bool IsSeparator,
    byte[]? LinkData)
{
    /// <summary>
    /// Set for entries written by packaged apps: they are started by activating
    /// the app with <see cref="ActivationArguments"/>, not by running a path.
    /// </summary>
    public string? PackagedAppId { get; init; }

    public string? ActivationArguments { get; init; }
}

public sealed record JumpListCategory(string Title, JumpListCategoryKind Kind, IReadOnlyList<JumpListEntry> Entries);

/// <summary>
/// Reads another app's jump list — what the taskbar shows when you right-click
/// its button.
///
/// Tasks and custom categories live in
/// %APPDATA%\Microsoft\Windows\Recent\CustomDestinations\{hash}.customDestinations-ms.
/// There is no public API to read them, but the format has been stable since
/// Windows 7: categories of serialised shell links. Recent and frequent items
/// come from IApplicationDocumentLists, which is public.
///
/// The file name is a CRC-64 (Jones polynomial, reflected, initial value all
/// ones, no final xor) of the upper-cased AppUserModelID as UTF-16LE. Checked
/// on this machine: it names 19 of the jump list files present, from AppIDs
/// such as "Microsoft.Windows.Explorer" and "com.squirrel.AnthropicClaude.claude".
/// </summary>
public static class JumpLists
{
    private const uint CategoryFooter = 0xBABFFBAB;

    public static string HashAppId(string appId)
    {
        var crc = ulong.MaxValue;
        foreach (var b in Encoding.Unicode.GetBytes(appId.ToUpperInvariant()))
            crc = Crc64Table[(byte)(crc ^ b)] ^ (crc >> 8);
        return crc.ToString("x16");
    }

    private static readonly ulong[] Crc64Table = BuildTable(0x92C64265D32139A4);

    private static ulong[] BuildTable(ulong polynomial)
    {
        var table = new ulong[256];
        for (ulong i = 0; i < 256; i++)
        {
            var c = i;
            for (var bit = 0; bit < 8; bit++) c = (c & 1) != 0 ? (c >> 1) ^ polynomial : c >> 1;
            table[i] = c;
        }
        return table;
    }

    public static string CustomDestinationsPath(string appId) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Microsoft", "Windows", "Recent", "CustomDestinations",
        HashAppId(appId) + ".customDestinations-ms");

    /// <summary>
    /// The jump list for an app, in taskbar order: the app's own categories and
    /// recent/frequent lists first, tasks last. Must run on an STA thread.
    /// </summary>
    public static IReadOnlyList<JumpListCategory> Read(string appId, int maxPerList = 10)
    {
        var result = new List<JumpListCategory>();
        var path = CustomDestinationsPath(appId);

        var declared = File.Exists(path) ? ReadCustomDestinations(path) : null;

        // An app with no custom list gets the default Recent category, as on
        // the taskbar. One with a list gets exactly the categories it declared.
        foreach (var category in declared ?? [])
        {
            switch (category.Kind)
            {
                case JumpListCategoryKind.Recent or JumpListCategoryKind.Frequent:
                    var documents = ReadDocuments(appId, category.Kind, maxPerList);
                    if (documents.Count > 0) result.Add(category with { Entries = documents });
                    break;
                case JumpListCategoryKind.Custom when category.Entries.Count > 0:
                    result.Add(category);
                    break;
            }
        }

        if (declared is null)
        {
            var recent = ReadDocuments(appId, JumpListCategoryKind.Recent, maxPerList);
            if (recent.Count > 0) result.Add(new JumpListCategory("Recent", JumpListCategoryKind.Recent, recent));
        }

        if (declared?.FirstOrDefault(c => c.Kind == JumpListCategoryKind.Tasks) is { Entries.Count: > 0 } tasks)
            result.Add(tasks);

        return result;
    }

    /// <summary>Parses one .customDestinations-ms file. Public for harness use.</summary>
    public static List<JumpListCategory> ReadCustomDestinations(string path)
    {
        var categories = new List<JumpListCategory>();
        byte[] data;
        try { data = File.ReadAllBytes(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return categories; }

        if (data.Length < 12) return categories;
        var offset = 0;
        var version = ReadUInt32(data, ref offset);
        var count = ReadUInt32(data, ref offset);
        offset += 4; // reserved
        if (version != 2) return categories;

        try
        {
            for (var i = 0; i < count && offset + 4 <= data.Length; i++)
            {
                var type = ReadUInt32(data, ref offset);
                switch (type)
                {
                    case 0:
                    {
                        var nameLength = BitConverter.ToUInt16(data, offset);
                        offset += 2;
                        var name = Encoding.Unicode.GetString(data, offset, nameLength * 2);
                        offset += nameLength * 2;
                        var entries = ReadEntries(data, ref offset);
                        categories.Add(new JumpListCategory(CategoryTitle(name), JumpListCategoryKind.Custom, entries));
                        break;
                    }
                    case 1:
                    {
                        var known = ReadUInt32(data, ref offset);
                        categories.Add(known == 1
                            ? new JumpListCategory("Frequent", JumpListCategoryKind.Frequent, [])
                            : new JumpListCategory("Recent", JumpListCategoryKind.Recent, []));
                        break;
                    }
                    case 2:
                        categories.Add(new JumpListCategory("Tasks", JumpListCategoryKind.Tasks, ReadEntries(data, ref offset)));
                        break;
                    default:
                        return categories; // unknown layout: keep what was read
                }

                if (offset + 4 <= data.Length && BitConverter.ToUInt32(data, offset) == CategoryFooter) offset += 4;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or COMException or InvalidCastException or IndexOutOfRangeException)
        {
            Debug.WriteLine($"[Hearth] jump list parse stopped in {Path.GetFileName(path)}: {ex.Message}");
        }

        return categories;
    }

    /// <summary>
    /// A category's display name. If a packaged app's resource string will not
    /// resolve, its key is still readable: ".../HistoryGroupTitle" is "History".
    /// </summary>
    private static string CategoryTitle(string raw)
    {
        var resolved = ShellLinkNative.ResolveIndirect(raw);
        if (!resolved.StartsWith("@{", StringComparison.Ordinal)) return resolved;

        var key = resolved.TrimEnd('}');
        key = key[(key.LastIndexOf('/') + 1)..];
        foreach (var suffix in new[] { "GroupTitle", "Title", "Group" })
        {
            if (key.Length > suffix.Length && key.EndsWith(suffix, StringComparison.Ordinal))
            {
                key = key[..^suffix.Length];
                break;
            }
        }
        return string.Concat(key.Select((c, i) => i > 0 && char.IsUpper(c) ? " " + char.ToLower(c) : c.ToString()));
    }

    private static List<JumpListEntry> ReadEntries(byte[] data, ref int offset)
    {
        var count = ReadUInt32(data, ref offset);
        var entries = new List<JumpListEntry>((int)Math.Min(count, 64));

        for (var i = 0; i < count; i++)
        {
            var clsid = new Guid(data.AsSpan(offset, 16));
            offset += 16;

            // Only shell links can be decoded: anything else has no length
            // prefix, so the rest of the category cannot be located.
            if (clsid != ShellLinkNative.CLSID_ShellLink)
                throw new ArgumentException($"unsupported destination type {clsid}");

            var (entry, consumed) = LoadLink(data, offset);
            offset += consumed;
            if (entry is not null) entries.Add(entry);
        }

        return entries;
    }

    /// <summary>
    /// Loads a shell link from a position in the file. The stream position after
    /// IPersistStream.Load is the only reliable way to find where it ends.
    /// </summary>
    private static (JumpListEntry? Entry, int Consumed) LoadLink(byte[] data, int offset)
    {
        var slice = data.AsSpan(offset).ToArray();
        var stream = ShellLinkNative.SHCreateMemStream(slice, (uint)slice.Length);
        var link = (IShellLinkW)new ShellLinkClass();
        try
        {
            ((IPersistStream)link).Load(stream);

            var position = Marshal.AllocHGlobal(8);
            int consumed;
            try
            {
                stream.Seek(0, 1 /* STREAM_SEEK_CUR */, position);
                consumed = (int)Marshal.ReadInt64(position);
            }
            finally
            {
                Marshal.FreeHGlobal(position);
            }

            return (Describe(link, slice.AsSpan(0, consumed).ToArray()), consumed);
        }
        finally
        {
            Marshal.FinalReleaseComObject(link);
            Marshal.FinalReleaseComObject(stream);
        }
    }

    private static JumpListEntry? Describe(IShellLinkW link, byte[]? linkData)
    {
        var store = (IPropertyStore)link;
        if (ShellLinkNative.GetBool(store, ShellLinkNative.PKEY_AppUserModel_IsDestListSeparator))
            return new JumpListEntry(string.Empty, null, null, null, 0, true, null);

        var path = new StringBuilder(1024);
        link.GetPath(path, path.Capacity, IntPtr.Zero, 0);
        var target = path.ToString();

        if (string.IsNullOrEmpty(target))
        {
            link.GetIDList(out var idList);
            try { target = ShellLinkNative.NameOfIdList(idList, ShellLinkNative.SIGDN_DESKTOPABSOLUTEPARSING) ?? string.Empty; }
            finally { if (idList != IntPtr.Zero) ShellLinkNative.CoTaskMemFree(idList); }
        }

        var args = new StringBuilder(2048);
        link.GetArguments(args, args.Capacity);

        var icon = new StringBuilder(1024);
        link.GetIconLocation(icon, icon.Capacity, out var iconIndex);

        var title = ShellLinkNative.GetString(store, ShellLinkNative.PKEY_Title);
        if (string.IsNullOrWhiteSpace(title))
            title = ShellLinkNative.GetString(store, ShellLinkNative.PKEY_AppUserModel_DestListProvidedTitle);

        // Packaged apps' entries point at the app itself and carry arguments
        // in the activation context; GetPath returns a meaningless path for them.
        var activation = ShellLinkNative.GetString(store, ShellLinkNative.PKEY_AppUserModel_ActivationContext);
        var linkAppId = ShellLinkNative.GetString(store, ShellLinkNative.PKEY_AppUserModel_ID);
        var packaged = !string.IsNullOrEmpty(linkAppId) && linkAppId.Contains('!') &&
                       target.Contains(linkAppId, StringComparison.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(title))
        {
            var description = new StringBuilder(1024);
            link.GetDescription(description, description.Capacity);
            title = description.ToString();
        }
        if (string.IsNullOrWhiteSpace(title))
        {
            // Nothing to call it: a packaged entry has no usable path to name it after.
            if (packaged) return null;
            title = string.IsNullOrEmpty(target) ? "Untitled" : Path.GetFileNameWithoutExtension(target);
        }

        return new JumpListEntry(
            ShellLinkNative.ResolveIndirect(title),
            string.IsNullOrEmpty(target) ? null : target,
            args.Length > 0 ? args.ToString() : null,
            icon.Length > 0 ? Environment.ExpandEnvironmentVariables(icon.ToString()) : null,
            iconIndex,
            false,
            linkData)
        {
            PackagedAppId = packaged ? linkAppId : null,
            ActivationArguments = packaged ? activation ?? string.Empty : null,
        };
    }

    private static List<JumpListEntry> ReadDocuments(string appId, JumpListCategoryKind kind, int max)
    {
        var entries = new List<JumpListEntry>();
        IApplicationDocumentLists? lists = null;
        try
        {
            lists = (IApplicationDocumentLists)new ApplicationDocumentListsClass();
            lists.SetAppID(appId);
            var iid = ShellLinkNative.IID_IObjectArray;
            lists.GetList(kind == JumpListCategoryKind.Frequent ? ShellLinkNative.ADLT_FREQUENT : ShellLinkNative.ADLT_RECENT,
                (uint)max, ref iid, out var raw);
            var array = (IObjectArray)raw;
            try
            {
                array.GetCount(out var count);
                for (uint i = 0; i < count; i++)
                {
                    var unknown = ShellLinkNative.IID_IUnknown;
                    array.GetAt(i, ref unknown, out var item);
                    try
                    {
                        var entry = item switch
                        {
                            IShellLinkW link => Describe(link, SaveLink(link)),
                            IShellItem shellItem => DescribeItem(shellItem),
                            _ => null,
                        };
                        if (entry is not null) entries.Add(entry);
                    }
                    finally
                    {
                        Marshal.FinalReleaseComObject(item);
                    }
                }
            }
            finally
            {
                Marshal.FinalReleaseComObject(array);
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or ArgumentException)
        {
            Debug.WriteLine($"[Hearth] recent list for {appId}: {ex.Message}");
        }
        finally
        {
            if (lists is not null) Marshal.FinalReleaseComObject(lists);
        }

        return entries;
    }

    /// <summary>
    /// Recent entries that are bare URIs (ms-settings:taskbar, app deep links)
    /// have no display name worth showing, so Hearth leaves them out rather than
    /// print the address. A drive path has its colon at index 1; a shell
    /// namespace path ("::{...}") at 0.
    /// </summary>
    private static bool LooksLikeRawUri(string title, string parsing) =>
        string.Equals(title, parsing, StringComparison.OrdinalIgnoreCase) && parsing.IndexOf(':') > 1;

    private static JumpListEntry? DescribeItem(IShellItem item)
    {
        item.GetDisplayName(SIGDN.DesktopAbsoluteParsing, out var parsingPtr);
        var parsing = ShellNative.TakeCoTaskMemString(parsingPtr);
        item.GetDisplayName(SIGDN.NormalDisplay, out var namePtr);
        var name = ShellNative.TakeCoTaskMemString(namePtr);
        if (string.IsNullOrEmpty(parsing)) return null;
        if (LooksLikeRawUri(string.IsNullOrEmpty(name) ? parsing : name, parsing)) return null;
        return new JumpListEntry(string.IsNullOrEmpty(name) ? Path.GetFileName(parsing) : name,
            parsing, null, null, 0, false, null);
    }

    private static byte[]? SaveLink(IShellLinkW link)
    {
        var stream = ShellLinkNative.SHCreateMemStream([], 0);
        try
        {
            ((IPersistStream)link).Save(stream, false);
            stream.Stat(out var stat, 1 /* STATFLAG_NONAME */);
            stream.Seek(0, 0, IntPtr.Zero);
            var bytes = new byte[stat.cbSize];
            stream.Read(bytes, bytes.Length, IntPtr.Zero);
            return bytes;
        }
        catch (COMException)
        {
            return null;
        }
        finally
        {
            Marshal.FinalReleaseComObject(stream);
        }
    }

    /// <summary>
    /// Writes an entry's link to a file Hearth can hand to ShellExecute, so the
    /// app is started exactly as its jump list describes. Returns null for
    /// entries that are a plain path (recent documents).
    /// </summary>
    public static string? MaterialiseLink(JumpListEntry entry, string directory)
    {
        if (entry.LinkData is not { Length: > 0 } data) return null;

        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(data))[..16] + ".lnk");
        if (File.Exists(file)) return file;

        var stream = ShellLinkNative.SHCreateMemStream(data, (uint)data.Length);
        var link = new ShellLinkClass();
        try
        {
            ((IPersistStream)link).Load(stream);
            ((IPersistFileW)link).Save(file, false);
            return file;
        }
        finally
        {
            Marshal.FinalReleaseComObject(link);
            Marshal.FinalReleaseComObject(stream);
        }
    }

    /// <summary>Starts a packaged app's jump list entry. Returns false if Windows refused.</summary>
    public static bool ActivatePackaged(JumpListEntry entry)
    {
        if (entry.PackagedAppId is not { } appId) return false;
        var manager = (IApplicationActivationManager)new ApplicationActivationManagerClass();
        try
        {
            return manager.ActivateApplication(appId, entry.ActivationArguments, 0, out _) >= 0;
        }
        finally
        {
            Marshal.FinalReleaseComObject(manager);
        }
    }

    private static uint ReadUInt32(byte[] data, ref int offset)
    {
        var value = BitConverter.ToUInt32(data, offset);
        offset += 4;
        return value;
    }
}

/// <summary>Works out which AppUserModelID a launcher item belongs to.</summary>
public static class AppIdentity
{
    /// <summary>
    /// The explicit AppUserModelID stored in a shortcut, if it has one. Must run
    /// on an STA thread.
    /// </summary>
    public static string? FromShortcut(string path)
    {
        try
        {
            var iid = ShellLinkNative.IID_IPropertyStore;
            ShellLinkNative.SHGetPropertyStoreFromParsingName(path, IntPtr.Zero, ShellLinkNative.GPS_DEFAULT, ref iid, out var store);
            try
            {
                var id = ShellLinkNative.GetString(store, ShellLinkNative.PKEY_AppUserModel_ID);
                return string.IsNullOrWhiteSpace(id) ? null : id;
            }
            finally
            {
                Marshal.FinalReleaseComObject(store);
            }
        }
        catch (Exception ex) when (ex is COMException or ArgumentException or InvalidCastException)
        {
            return null;
        }
    }

    /// <summary>
    /// Resolves every item's AppUserModelID in one pass: apps are their own ID;
    /// shortcuts use the ID stored in them, or else the installed app with the
    /// same name (which is how most desktop shortcuts relate to Start entries).
    /// Must run on an STA thread.
    /// </summary>
    public static Dictionary<string, string> Resolve(IEnumerable<LauncherItem> items, IEnumerable<LauncherItem> installedApps)
    {
        var byName = new Dictionary<string, string>(StringComparer.CurrentCultureIgnoreCase);
        foreach (var app in installedApps) byName.TryAdd(app.DisplayName, app.Id);

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            switch (item.Kind)
            {
                case LauncherItemKind.App:
                    result[item.Id] = item.Id;
                    break;
                case LauncherItemKind.Shortcut when item.FileSystemPath is { } path:
                    var id = path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ? FromShortcut(path) : null;
                    if (id is null) byName.TryGetValue(item.DisplayName, out id);
                    if (id is not null) result[item.Id] = id;
                    break;
            }
        }
        return result;
    }
}
