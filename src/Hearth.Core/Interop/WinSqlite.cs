using System.Runtime.InteropServices;

namespace Hearth.Core.Interop;

/// <summary>
/// The SQLite build that ships inside Windows (System32\winsqlite3.dll), used
/// read-only. Keeps Hearth free of a NuGet dependency for the one database it
/// reads: the shell's notification store.
/// </summary>
internal static partial class WinSqlite
{
    private const string Dll = "winsqlite3.dll";

    public const int SQLITE_OK = 0;
    public const int SQLITE_ROW = 100;
    public const int SQLITE_OPEN_READONLY = 0x00000001;
    public const int SQLITE_OPEN_URI = 0x00000040;

    [LibraryImport(Dll, EntryPoint = "sqlite3_open_v2", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int Open(string filename, out IntPtr db, int flags, IntPtr vfs);

    [LibraryImport(Dll, EntryPoint = "sqlite3_close_v2")]
    public static partial int Close(IntPtr db);

    [LibraryImport(Dll, EntryPoint = "sqlite3_busy_timeout")]
    public static partial int BusyTimeout(IntPtr db, int milliseconds);

    [LibraryImport(Dll, EntryPoint = "sqlite3_prepare16_v2", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int Prepare(IntPtr db, string sql, int bytes, out IntPtr statement, IntPtr tail);

    [LibraryImport(Dll, EntryPoint = "sqlite3_step")]
    public static partial int Step(IntPtr statement);

    [LibraryImport(Dll, EntryPoint = "sqlite3_finalize")]
    public static partial int Finalize(IntPtr statement);

    [LibraryImport(Dll, EntryPoint = "sqlite3_column_text16")]
    public static partial IntPtr ColumnText16(IntPtr statement, int column);

    [LibraryImport(Dll, EntryPoint = "sqlite3_column_blob")]
    public static partial IntPtr ColumnBlob(IntPtr statement, int column);

    [LibraryImport(Dll, EntryPoint = "sqlite3_column_bytes")]
    public static partial int ColumnBytes(IntPtr statement, int column);

    [LibraryImport(Dll, EntryPoint = "sqlite3_column_int64")]
    public static partial long ColumnInt64(IntPtr statement, int column);

    [LibraryImport(Dll, EntryPoint = "sqlite3_errmsg16")]
    public static partial IntPtr ErrorMessage16(IntPtr db);

    public static string? Text(IntPtr statement, int column)
    {
        var ptr = ColumnText16(statement, column);
        return ptr == IntPtr.Zero ? null : Marshal.PtrToStringUni(ptr);
    }

    public static byte[] Blob(IntPtr statement, int column)
    {
        var ptr = ColumnBlob(statement, column);
        var length = ColumnBytes(statement, column);
        if (ptr == IntPtr.Zero || length <= 0) return [];
        var bytes = new byte[length];
        Marshal.Copy(ptr, bytes, 0, length);
        return bytes;
    }

    public static string Error(IntPtr db) =>
        Marshal.PtrToStringUni(ErrorMessage16(db)) ?? "unknown SQLite error";
}
