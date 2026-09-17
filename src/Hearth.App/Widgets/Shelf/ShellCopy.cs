using System.IO;
using System.Runtime.InteropServices;

namespace Hearth.App.Widgets.Shelf;

/// <summary>Copying and moving through the shell, as a drop in Explorer does.</summary>
internal static class ShellCopy
{
    /// <summary>
    /// Copies (or moves) files and folders into <paramref name="folder"/>
    /// with Explorer's progress window, "Copy of ..." names on a clash, and
    /// undo. Blocks until done, so call it off the UI thread. Returns false
    /// if it failed or the user cancelled.
    /// </summary>
    public static bool Into(IEnumerable<string> paths, string folder, bool move, IntPtr owner)
    {
        var from = string.Join((char)0, paths.Where(p => File.Exists(p) || Directory.Exists(p)));
        if (from.Length == 0) return false;

        var op = new SHFILEOPSTRUCT
        {
            hwnd = owner,
            wFunc = move ? FO_MOVE : FO_COPY,
            pFrom = from + new string((char)0, 2), // double-null-terminated lists
            pTo = folder + new string((char)0, 2),
            fFlags = FOF_ALLOWUNDO | FOF_RENAMEONCOLLISION | FOF_NOCONFIRMMKDIR,
        };

        var result = SHFileOperation(ref op);
        return result == 0 && !op.fAnyOperationsAborted;
    }

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

    private const uint FO_MOVE = 0x0001;
    private const uint FO_COPY = 0x0002;
    private const ushort FOF_RENAMEONCOLLISION = 0x0008;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_NOCONFIRMMKDIR = 0x0200;

    [DllImport("shell32.dll", EntryPoint = "SHFileOperationW", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);
}
