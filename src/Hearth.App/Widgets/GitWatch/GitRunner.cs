using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Hearth.Core.Diagnostics;

namespace Hearth.App.Widgets.GitWatch;

internal sealed record GitStatus(string Branch, string AheadBehind, int Modified);

internal static class GitRunner
{
    public static async Task<GitStatus?> GetStatusAsync(string repoPath)
    {
        if (string.IsNullOrWhiteSpace(repoPath)) return null;

        try
        {
            var tcs = new TaskCompletionSource<string>();
            var process = new Process
            {
                StartInfo = new ProcessStartInfo // check-widgets: allow - background CLI only
                {
                    FileName = "git.exe",
                    Arguments = "status -sb",
                    WorkingDirectory = repoPath,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                }
            };
            
            process.EnableRaisingEvents = true;
            process.Exited += (sender, args) =>
            {
                try { tcs.TrySetResult(process.StandardOutput.ReadToEnd()); }
                catch (Exception ex) { tcs.TrySetException(ex); }
            };

            process.Start(); // check-widgets: allow - background CLI only
            var output = await tcs.Task;
            
            if (string.IsNullOrWhiteSpace(output)) return null;

            var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length == 0) return null;

            var header = lines[0].Trim();
            if (!header.StartsWith("## ")) return null;

            var branchInfo = header.Substring(3);
            var branch = branchInfo;
            var aheadBehind = "";

            var bracketIndex = branchInfo.IndexOf('[');
            if (bracketIndex > 0)
            {
                branch = branchInfo.Substring(0, bracketIndex).Trim();
                aheadBehind = branchInfo.Substring(bracketIndex).Trim();
            }

            // The rest of the lines are modified/untracked files
            var modified = lines.Length - 1;

            return new GitStatus(branch, aheadBehind, modified);
        }
        catch (Exception ex)
        {
            Log.Error("GitWatch", ex);
            return null;
        }
    }
}
