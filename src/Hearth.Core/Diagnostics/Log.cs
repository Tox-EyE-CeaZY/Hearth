using System.IO;
using System.Diagnostics;
using System.Text;

namespace Hearth.Core.Diagnostics;

/// <summary>
/// Dirt-simple file log.
///
/// Hearth has no console and no window chrome, and its failure modes are things
/// like "the desktop is blank" — which tells you nothing. Debug.WriteLine only
/// helps under a debugger, and attaching one to a window that owns the desktop
/// layer is its own adventure. So: a file, written eagerly, that survives a
/// crash and can be read while the app is still running.
/// </summary>
public static class Log
{
    private static readonly object Gate = new();
    private static readonly string Path = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Hearth", "hearth.log");

    private static bool _started;

    public static string FilePath => Path;

    /// <summary>Truncates the log and stamps it. Call once at startup.</summary>
    public static void Start(string header)
    {
        lock (Gate)
        {
            try
            {
                var directory = System.IO.Path.GetDirectoryName(Path);
                if (!string.IsNullOrEmpty(directory)) System.IO.Directory.CreateDirectory(directory);

                // One run per file. A rolling log would bury the run that
                // actually matters, which is always the most recent one — but
                // the run before is kept too, because after a crash the next
                // start would otherwise wipe the evidence.
                if (System.IO.File.Exists(Path))
                    System.IO.File.Copy(Path, System.IO.Path.ChangeExtension(Path, ".previous.log"), overwrite: true);
                System.IO.File.WriteAllText(Path,
                    $"=== Hearth {header} === {DateTime.Now:yyyy-MM-dd HH:mm:ss}{Environment.NewLine}",
                    Encoding.UTF8);
                _started = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Debug.WriteLine($"[Hearth] could not open log: {ex.Message}");
            }
        }
    }

    public static void Write(string message)
    {
        Debug.WriteLine($"[Hearth] {message}");
        if (!_started) return;

        lock (Gate)
        {
            try
            {
                System.IO.File.AppendAllText(Path,
                    $"{DateTime.Now:HH:mm:ss.fff}  {message}{Environment.NewLine}", Encoding.UTF8);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Losing a log line must never take the app down with it.
                Debug.WriteLine($"[Hearth] log write failed: {ex.Message}");
            }
        }
    }

    public static void Error(string context, Exception ex) =>
        Write($"ERROR {context}: {ex}");
}
