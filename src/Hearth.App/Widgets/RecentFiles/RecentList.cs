using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Hearth.Core.Diagnostics;
using Hearth.Core.Interop;

namespace Hearth.App.Widgets.RecentFiles;

/// <summary>A file or folder Windows lists under Recent.</summary>
internal sealed record RecentFile(string Path, string LinkPath, bool IsFolder, DateTime UsedUtc)
{
    public string Name => System.IO.Path.GetFileName(Path.TrimEnd('\\')) is { Length: > 0 } name ? name : Path;
}

/// <summary>
/// Reads Windows' Recent items (the shortcuts in shell:recent that Explorer's
/// Quick access and jump lists are built from). Blocking; call it off the UI thread.
/// </summary>
internal static class RecentList
{
    private const uint StgmRead = 0;

    public static string Folder { get; } = Environment.GetFolderPath(Environment.SpecialFolder.Recent);

    public static IReadOnlyList<RecentFile> Read(int limit)
    {
        var result = new List<RecentFile>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var links = new DirectoryInfo(Folder).EnumerateFiles("*.lnk")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Take(limit * 4);

            foreach (var link in links)
            {
                if (Resolve(link.FullName) is not { } target || !seen.Add(target)) continue;

                // Network paths can take seconds to answer; trust the shortcut.
                bool isFolder;
                if (target.StartsWith(@"\\", StringComparison.Ordinal))
                {
                    isFolder = string.IsNullOrEmpty(System.IO.Path.GetExtension(target));
                }
                else if (Directory.Exists(target))
                {
                    isFolder = true;
                }
                else if (File.Exists(target))
                {
                    isFolder = false;
                }
                else
                {
                    continue;
                }

                result.Add(new RecentFile(target, link.FullName, isFolder, link.LastWriteTimeUtc));
                if (result.Count >= limit) break;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Write($"recent files: {ex.Message}");
        }
        return result;
    }

    private static string? Resolve(string linkPath)
    {
        IShellLinkW? link = null;
        try
        {
            link = (IShellLinkW)new ShellLinkClass();
            ((IPersistFileW)link).Load(linkPath, StgmRead);
            var buffer = new StringBuilder(1024);
            link.GetPath(buffer, buffer.Capacity, IntPtr.Zero, 0);
            return buffer.Length > 0 ? buffer.ToString() : null;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or ArgumentException)
        {
            return null;
        }
        finally
        {
            if (link is not null) Marshal.FinalReleaseComObject(link);
        }
    }
}
