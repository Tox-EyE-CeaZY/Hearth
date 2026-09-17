using System.IO;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Hearth.Core.Interop;

namespace Hearth.Core.Shell;

/// <summary>Launches items and exposes the shell verbs the context menu needs.</summary>
public static class ShellLauncher
{
    /// <summary>
    /// Launches via ShellExecuteEx rather than Process.Start so that AUMIDs
    /// (shell:AppsFolder\...) and packaged apps go through the same path as
    /// plain files — and without spawning a throwaway explorer.exe to do it.
    /// </summary>
    public static bool Launch(LauncherItem item)
    {
        if (item.Target is null) return false;
        return Invoke(ResolveTarget(item), verb: null);
    }

    /// <summary>Opens a path, link or URI with its default handler.</summary>
    public static bool Open(string target) => Invoke(target, verb: null);

    public static bool OpenFileLocation(LauncherItem item)
    {
        var path = item.FileSystemPath;
        if (string.IsNullOrEmpty(path)) return false;

        // /select, has no ShellExecute verb equivalent, so this one really does
        // have to go through explorer.exe.
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"")
        {
            UseShellExecute = true,
        });
        return true;
    }

    public static bool ShowProperties(LauncherItem item)
    {
        var target = item.FileSystemPath ?? item.Target;
        if (string.IsNullOrEmpty(target)) return false;

        // The properties verb needs the full ID list to be resolved first.
        return Invoke(target, "properties", ShellNative.SEE_MASK_INVOKEIDLIST);
    }

    public static bool RunAsAdministrator(LauncherItem item)
    {
        if (item.Target is null) return false;
        return Invoke(ResolveTarget(item), "runas");
    }

    /// <summary>
    /// Sends a file or folder to the Recycle Bin through the shell, exactly as
    /// pressing Delete in Explorer does — including the user's own
    /// confirmation setting, and the Recycle Bin's undo.
    /// Returns false if it failed or the user cancelled.
    /// </summary>
    public static bool Recycle(string path, IntPtr owner)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return false;

        var op = new SHFILEOPSTRUCT
        {
            hwnd = owner,
            wFunc = FO_DELETE,
            pFrom = path + new string((char)0, 2), // the API takes a double-null-terminated list
            fFlags = FOF_ALLOWUNDO | FOF_WANTNUKEWARNING,
        };

        var result = SHFileOperation(ref op);
        return result == 0 && !op.fAnyOperationsAborted;
    }

    /// <summary>Opens Windows' own Installed apps page, where apps are uninstalled.</summary>
    public static void OpenInstalledApps() =>
        Process.Start(new ProcessStartInfo("ms-settings:appsfeatures") { UseShellExecute = true });

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    private const uint FO_DELETE = 0x0003;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_WANTNUKEWARNING = 0x4000;

    [DllImport("shell32.dll", EntryPoint = "SHFileOperationW", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);

    private static string ResolveTarget(LauncherItem item) =>
        item.Kind == LauncherItemKind.App
            ? $@"shell:AppsFolder\{item.Target}"
            : item.Target!;

    private static bool Invoke(string target, string? verb, uint extraMask = 0)
    {
        var info = new ShellNative.SHELLEXECUTEINFO
        {
            cbSize = Marshal.SizeOf<ShellNative.SHELLEXECUTEINFO>(),
            fMask = ShellNative.SEE_MASK_ASYNCOK | ShellNative.SEE_MASK_FLAG_NO_UI | extraMask,
            lpVerb = verb,
            lpFile = target,
            nShow = Win32.SW_SHOW,
        };

        if (ShellNative.ShellExecuteEx(ref info)) return true;

        Debug.WriteLine($"[Hearth] ShellExecuteEx failed for '{target}' (verb: {verb ?? "default"})");
        return false;
    }
}
