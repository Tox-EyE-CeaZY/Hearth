using System.Diagnostics;
using System.Runtime.InteropServices;
using Hearth.Core.Interop;

namespace Hearth.Core.Shell;

/// <summary>
/// Enumerates the shell's virtual "Applications" folder.
///
/// This is the single source worth using: it is what Win+R "shell:AppsFolder"
/// and the Win11 all-apps list both read, and it covers classic Win32 entries
/// (backed by Start-menu .lnk files) and packaged MSIX/UWP apps in one pass,
/// each with a launchable AppUserModelID.
/// </summary>
public sealed class AppsFolderCatalog
{
    private const string AppsFolderPath = "shell:AppsFolder";

    /// <summary>
    /// Entries whose AUMID matches one of these are shell plumbing rather than
    /// things a person launches. Kept narrow on purpose — over-filtering hides
    /// real apps, and the user can always hide an item by hand.
    /// </summary>
    private static readonly string[] NoiseAumidFragments =
    [
        "Microsoft.Windows.Search",
        "Microsoft.Windows.StartMenuExperienceHost",
        "Microsoft.Windows.ShellExperienceHost",
        "Microsoft.Windows.SecHealthUI",
        "Microsoft.AsyncTextService",
        "Windows.PrintDialog",
        "Microsoft.Windows.CapturePicker",
        "Microsoft.Windows.ContentDeliveryManager",
        "Microsoft.Windows.ParentalControls",
        "Microsoft.Windows.PeopleExperienceHost",
        "Microsoft.Windows.XGpuEjectDialog",
        "Microsoft.Windows.AssignedAccessLockApp",
        "Microsoft.Windows.NarratorQuickStart",
        "Microsoft.Windows.OOBENetworkCaptivePortal",
        "Microsoft.Windows.OOBENetworkConnectionFlow",
        "Microsoft.ECApp",
        "Microsoft.LockApp",
        "Microsoft.CredDialogHost",
        "Microsoft.AAD.BrokerPlugin",
        "Microsoft.AccountsControl",
    ];

    /// <summary>
    /// Reads every installed application. Blocking and COM-heavy — call it off
    /// the UI thread. Typical cost is 100-400 ms for a few hundred apps.
    /// </summary>
    public IReadOnlyList<LauncherItem> Enumerate(bool filterNoise = true)
    {
        var results = new List<LauncherItem>(256);

        ShellNative.SHCreateItemFromParsingName(AppsFolderPath, IntPtr.Zero, ShellNative.IID_IShellItem, out var raw);
        if (raw is not IShellItem appsFolder)
            return results;

        IEnumShellItems? enumerator = null;
        try
        {
            appsFolder.BindToHandler(IntPtr.Zero, ShellNative.BHID_EnumItems,
                ShellNative.IID_IEnumShellItems, out var enumPtr);
            if (enumPtr == IntPtr.Zero) return results;

            enumerator = (IEnumShellItems)Marshal.GetObjectForIUnknown(enumPtr);
            Marshal.Release(enumPtr);

            var buffer = new IShellItem[1];
            while (enumerator.Next(1, buffer, out var fetched) == 0 && fetched == 1)
            {
                var item = buffer[0];
                try
                {
                    var entry = Describe(item, filterNoise);
                    if (entry is not null) results.Add(entry);
                }
                catch (Exception ex) when (ex is COMException or InvalidCastException or ArgumentException)
                {
                    // A single malformed entry must not abort the whole catalog.
                    // ArgumentException belongs here too: PreserveSig=false
                    // turns a shell E_INVALIDARG into one of those, and one bad
                    // entry out of several hundred should cost that entry only.
                    Debug.WriteLine($"[Hearth] skipped AppsFolder entry: {ex.Message}");
                }
                finally
                {
                    if (item is not null) Marshal.FinalReleaseComObject(item);
                    buffer[0] = null!;
                }
            }
        }
        finally
        {
            if (enumerator is not null) Marshal.FinalReleaseComObject(enumerator);
            Marshal.FinalReleaseComObject(appsFolder);
        }

        results.Sort(static (a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.CurrentCultureIgnoreCase));
        return results;
    }

    private static LauncherItem? Describe(IShellItem item, bool filterNoise)
    {
        item.GetDisplayName(SIGDN.ParentRelativeParsing, out var aumidPtr);
        var aumid = ShellNative.TakeCoTaskMemString(aumidPtr);
        if (string.IsNullOrWhiteSpace(aumid)) return null;

        if (filterNoise && IsNoise(aumid)) return null;

        item.GetDisplayName(SIGDN.NormalDisplay, out var namePtr);
        var name = ShellNative.TakeCoTaskMemString(namePtr);
        if (string.IsNullOrWhiteSpace(name)) name = aumid;

        // Classic Win32 entries resolve to a Start-menu .lnk on disk; packaged
        // apps do not, and that absence is exactly how we tell them apart.
        string? fsPath = null;
        try
        {
            item.GetDisplayName(SIGDN.FileSysPath, out var pathPtr);
            fsPath = ShellNative.TakeCoTaskMemString(pathPtr);
            if (string.IsNullOrWhiteSpace(fsPath)) fsPath = null;
        }
        catch (Exception ex) when (ex is COMException or ArgumentException)
        {
            // Expected, and common: packaged apps have no filesystem identity.
            // ArgumentException matters as much as COMException here — the
            // interface is declared PreserveSig=false, so the E_INVALIDARG that
            // a packaged app returns is translated into ArgumentException, not
            // a COMException. Catching only the latter aborts the entire
            // catalog on the first Store app it meets.
        }

        return new LauncherItem
        {
            Id = aumid,
            DisplayName = name,
            Kind = LauncherItemKind.App,
            Target = aumid,
            FileSystemPath = fsPath,
        };
    }

    private static bool IsNoise(string aumid)
    {
        foreach (var fragment in NoiseAumidFragments)
        {
            if (aumid.Contains(fragment, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
