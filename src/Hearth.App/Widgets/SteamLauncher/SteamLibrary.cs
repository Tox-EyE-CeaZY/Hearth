using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Hearth.Core.Diagnostics;

namespace Hearth.App.Widgets.SteamLauncher;

internal sealed record SteamGame(string Id, string Name);

internal static class SteamLibrary
{
    public static IReadOnlyList<SteamGame> GetInstalledGames()
    {
        var games = new List<SteamGame>();
        try
        {
            var steamPath = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string; // check-widgets: allow - read only
            if (string.IsNullOrEmpty(steamPath)) return games;

            var vdfPath = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
            var libraryPaths = new List<string> { steamPath }; // Default

            if (File.Exists(vdfPath))
            {
                var content = File.ReadAllText(vdfPath);
                var matches = Regex.Matches(content, @"\""path\""\s+\""([^\""]+)\""");
                foreach (Match match in matches)
                {
                    var path = match.Groups[1].Value.Replace(@"\\", @"\");
                    if (!libraryPaths.Contains(path, StringComparer.OrdinalIgnoreCase))
                        libraryPaths.Add(path);
                }
            }

            foreach (var lib in libraryPaths)
            {
                var steamApps = Path.Combine(lib, "steamapps");
                if (!Directory.Exists(steamApps)) continue;

                var acfFiles = Directory.GetFiles(steamApps, "appmanifest_*.acf");
                foreach (var acf in acfFiles)
                {
                    try
                    {
                        var acfContent = File.ReadAllText(acf);
                        var idMatch = Regex.Match(acfContent, @"\""appid\""\s+\""([^\""]+)\""");
                        var nameMatch = Regex.Match(acfContent, @"\""name\""\s+\""([^\""]+)\""");
                        if (idMatch.Success && nameMatch.Success)
                        {
                            var id = idMatch.Groups[1].Value;
                            var name = nameMatch.Groups[1].Value;
                            // Skip Steamworks Common Redistributables etc.
                            if (id != "228980" && !name.Contains("Steamworks"))
                            {
                                games.Add(new SteamGame(id, name));
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Error($"SteamLauncher: failed to read {acf}", ex);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("SteamLauncher: failed to load library", ex);
        }
        
        games.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
        return games;
    }
}
