using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace Hearth.Core.Interop;

[ComImport]
[Guid("000214F9-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellLinkW
{
    void GetPath([MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int maxPath, IntPtr findData, uint flags);
    void GetIDList(out IntPtr idList);
    void SetIDList(IntPtr idList);
    void GetDescription([MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int maxName);
    void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
    void GetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] StringBuilder dir, int maxPath);
    void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
    void GetArguments([MarshalAs(UnmanagedType.LPWStr)] StringBuilder args, int maxPath);
    void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
    void GetHotkey(out short hotkey);
    void SetHotkey(short hotkey);
    void GetShowCmd(out int showCmd);
    void SetShowCmd(int showCmd);
    void GetIconLocation([MarshalAs(UnmanagedType.LPWStr)] StringBuilder iconPath, int maxPath, out int iconIndex);
    void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int iconIndex);
    void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string relPath, uint reserved);
    void Resolve(IntPtr hwnd, uint flags);
    void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
}

[ComImport]
[Guid("00000109-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPersistStream
{
    void GetClassID(out Guid clsid);
    [PreserveSig] int IsDirty();
    void Load(IStream stream);
    void Save(IStream stream, [MarshalAs(UnmanagedType.Bool)] bool clearDirty);
    void GetSizeMax(out long size);
}

[ComImport]
[Guid("0000010b-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPersistFileW
{
    void GetClassID(out Guid clsid);
    [PreserveSig] int IsDirty();
    void Load([MarshalAs(UnmanagedType.LPWStr)] string fileName, uint mode);
    void Save([MarshalAs(UnmanagedType.LPWStr)] string fileName, [MarshalAs(UnmanagedType.Bool)] bool remember);
    void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string fileName);
    void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string fileName);
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct PROPERTYKEY
{
    public Guid fmtid;
    public uint pid;

    public PROPERTYKEY(string fmtid, uint pid)
    {
        this.fmtid = new Guid(fmtid);
        this.pid = pid;
    }
}

/// <summary>Just enough of PROPVARIANT to read strings and booleans.</summary>
[StructLayout(LayoutKind.Explicit, Size = 24)]
internal struct PROPVARIANT
{
    [FieldOffset(0)] public ushort vt;
    [FieldOffset(8)] public IntPtr pointer;
    [FieldOffset(8)] public short boolValue;

    public const ushort VT_BSTR = 8;
    public const ushort VT_BOOL = 11;
    public const ushort VT_LPWSTR = 31;

    public readonly string? AsString() => pointer == IntPtr.Zero ? null : vt switch
    {
        VT_LPWSTR => Marshal.PtrToStringUni(pointer),
        VT_BSTR => Marshal.PtrToStringBSTR(pointer),
        _ => null,
    };
    public readonly bool AsBool() => vt == VT_BOOL && boolValue != 0;
}

[ComImport]
[Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPropertyStore
{
    void GetCount(out uint count);
    void GetAt(uint index, out PROPERTYKEY key);
    void GetValue(ref PROPERTYKEY key, out PROPVARIANT value);
    void SetValue(ref PROPERTYKEY key, ref PROPVARIANT value);
    void Commit();
}

[ComImport]
[Guid("92CA9DCD-5622-4BBA-A805-5E9F541BD8C9")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IObjectArray
{
    void GetCount(out uint count);
    void GetAt(uint index, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out object item);
}

[ComImport]
[Guid("3C594F9F-9F30-47A1-979A-C9E83D3D0A06")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IApplicationDocumentLists
{
    void SetAppID([MarshalAs(UnmanagedType.LPWStr)] string appId);
    void GetList(int listType, uint itemsDesired, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out object list);
}

[ComImport]
[Guid("2e941141-7f97-4756-ba1d-9decde894a3d")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IApplicationActivationManager
{
    [PreserveSig]
    int ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
        [MarshalAs(UnmanagedType.LPWStr)] string? arguments, int options, out uint processId);
}

[ComImport]
[Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")]
internal class ApplicationActivationManagerClass { }

[ComImport]
[Guid("00021401-0000-0000-C000-000000000046")]
internal class ShellLinkClass { }

[ComImport]
[Guid("86BEC222-30F2-47E0-9F25-60D11CD75C28")]
internal class ApplicationDocumentListsClass { }

internal static partial class ShellLinkNative
{
    public static readonly Guid CLSID_ShellLink = new("00021401-0000-0000-C000-000000000046");
    public static readonly Guid IID_IPropertyStore = new("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99");
    public static readonly Guid IID_IObjectArray = new("92CA9DCD-5622-4BBA-A805-5E9F541BD8C9");
    public static readonly Guid IID_IUnknown = new("00000000-0000-0000-C000-000000000046");

    public static PROPERTYKEY PKEY_Title = new("F29F85E0-4FF9-1068-AB91-08002B27B3D9", 2);
    public static PROPERTYKEY PKEY_AppUserModel_ID = new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3", 5);
    public static PROPERTYKEY PKEY_AppUserModel_IsDestListSeparator = new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3", 6);

    // Written by the packaged-app JumpList API instead of a title and arguments.
    public static PROPERTYKEY PKEY_AppUserModel_ActivationContext = new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3", 20);
    public static PROPERTYKEY PKEY_AppUserModel_DestListProvidedTitle = new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3", 27);

    public const int ADLT_RECENT = 0;
    public const int ADLT_FREQUENT = 1;
    public const int GPS_DEFAULT = 0;
    public const int SIGDN_DESKTOPABSOLUTEPARSING = unchecked((int)0x80028000);
    public const int SIGDN_NORMALDISPLAY = 0;

    [DllImport("shlwapi.dll", EntryPoint = "#12", CharSet = CharSet.Unicode)]
    public static extern IStream SHCreateMemStream(byte[] data, uint size);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    public static extern void SHGetPropertyStoreFromParsingName(
        string path, IntPtr bindContext, int flags, ref Guid iid,
        [MarshalAs(UnmanagedType.Interface)] out IPropertyStore store);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern int SHGetNameFromIDList(IntPtr idList, int sigdn, out IntPtr name);

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    public static extern int SHLoadIndirectString(string source, StringBuilder output, int outputSize, IntPtr reserved);

    [DllImport("ole32.dll")]
    public static extern int PropVariantClear(ref PROPVARIANT value);

    [DllImport("ole32.dll")]
    public static extern void CoTaskMemFree(IntPtr ptr);

    public static string? GetString(IPropertyStore store, PROPERTYKEY key)
    {
        store.GetValue(ref key, out var value);
        try { return value.AsString(); }
        finally { PropVariantClear(ref value); }
    }

    public static bool GetBool(IPropertyStore store, PROPERTYKEY key)
    {
        store.GetValue(ref key, out var value);
        try { return value.AsBool(); }
        finally { PropVariantClear(ref value); }
    }

    /// <summary>Resolves "@{Package?ms-resource://...}" and "@dll,-123" strings.</summary>
    public static string ResolveIndirect(string text)
    {
        if (!text.StartsWith('@')) return text;
        if (TryLoadIndirect(text) is { } resolved) return resolved;

        // Packaged apps write "ms-resource:///Resources/..." with the package
        // left implied, which SHLoadIndirectString will not resolve. Name the
        // package (the part of the full name before the first underscore).
        var open = text.IndexOf('{');
        var query = text.IndexOf('?');
        if (open == 1 && query > open && text.Contains("ms-resource:///", StringComparison.Ordinal))
        {
            var packageName = text[(open + 1)..query].Split('_')[0];
            var named = text.Replace("ms-resource:///", $"ms-resource://{packageName}/", StringComparison.Ordinal);
            if (TryLoadIndirect(named) is { } fromNamed) return fromNamed;
        }

        return text;
    }

    private static string? TryLoadIndirect(string text)
    {
        var buffer = new StringBuilder(512);
        return SHLoadIndirectString(text, buffer, buffer.Capacity, IntPtr.Zero) == 0 ? buffer.ToString() : null;
    }

    public static string? NameOfIdList(IntPtr idList, int sigdn)
    {
        if (idList == IntPtr.Zero) return null;
        if (SHGetNameFromIDList(idList, sigdn, out var ptr) != 0 || ptr == IntPtr.Zero) return null;
        try { return Marshal.PtrToStringUni(ptr); }
        finally { CoTaskMemFree(ptr); }
    }
}
