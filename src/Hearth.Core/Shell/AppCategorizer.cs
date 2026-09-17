namespace Hearth.Core.Shell;

/// <summary>
/// Sorts installed apps into a handful of categories for the Start menu.
///
/// Windows records no category for an app, so this is a heuristic: the app's
/// identity first (a Steam or Battle.net game is a game whatever it is
/// called), then its Start-menu folder, then keywords in its name. It is
/// deliberately coarse; the user's own choice, stored separately, always wins.
/// </summary>
public static class AppCategorizer
{
    public const string Games = "Games";
    public const string Internet = "Internet";
    public const string Social = "Social";
    public const string Media = "Media";
    public const string Creative = "Creative";
    public const string Productivity = "Productivity";
    public const string Development = "Development";
    public const string System = "System";
    public const string Other = "Other";

    /// <summary>Display order.</summary>
    public static readonly IReadOnlyList<string> All =
        [Games, Social, Internet, Media, Creative, Productivity, Development, System, Other];

    private static readonly (string Category, string[] Keys)[] IdentityRules =
    [
        (Games, ["steam://", "battlenet://", "com.epicgames", "uplay://", "origin2://", "ea://", "riotclient", "gog", "xboxapp", "gamingapp", "minecraft", "roblox"]),
        (Internet, ["_crx_", "msedge", "chrome", "firefox", "vivaldi", "opera", "brave"]),
        (Productivity, ["microsoft.office", "officehub"]),
        (System, ["windows.immersivecontrolpanel", "microsoft.windows.controlpanel", "microsoft.windows.explorer", "microsoft.windowsstore", "sechealthui", "microsoft.getstarted", "microsoft.windows.search"]),
        (Development, ["microsoft.windowsterminal", "microsoft.visualstudio", "vscode", "jetbrains"]),
    ];

    private static readonly (string Category, string[] Keys)[] FolderRules =
    [
        (System, ["windows tools", "administrative tools", "accessibility", "system tools", "windows powershell", "maintenance"]),
        (Games, ["steam", "battle.net", "epic games", "games", "ubisoft", "ea app", "riot games", "gog galaxy"]),
        (Development, ["visual studio", "python", "node.js", "git", "jetbrains", "android studio", "docker", "wsl", "llvm", "windows kits"]),
        (Creative, ["adobe", "affinity", "autodesk", "blender"]),
        (Productivity, ["microsoft office", "libreoffice"]),
    ];

    private static readonly (string Category, string[] Keys)[] NameRules =
    [
        (Games, ["steam", "battle.net", "epic games", "xbox", "minecraft", "call of duty", "fortnite", "valorant", "league of legends", "overwatch", "diablo", "hearthstone", "starbound", "terraria", "deltarune", "undertale", "hollow knight", "don't starve", "lethal company", "spore", "marvel rivals", "game", "solitaire", "launcher", "playnite", "gog galaxy", "ea app", "ubisoft", "rockstar", "roblox", "itch", "modrinth", "elvenar", "forge of empires", "curseforge", "prism launcher", "heroic"]),
        (Social, ["discord", "telegram", "whatsapp", "signal", "slack", "teams", "zoom", "skype", "messenger", "outlook", "mail", "thunderbird", "phone link", "element", "guilded", "revolt"]),
        (Internet, ["browser", "edge", "chrome", "firefox", "vivaldi", "opera", "brave", "tor ", "internet", "cloudflare", "tailscale", "vpn", "qbittorrent", "transmission", "filezilla", "dropbox", "onedrive", "google drive", "nordvpn", "protonvpn", "localsend", "mullvad", "wireguard"]),
        (Media, ["spotify", "youtube", "music", "vlc", "media player", "netflix", "prime video", "disney", "twitch", "plex", "jellyfin", "photos", "video", "movies", "camera", "podcast", "itunes", "foobar", "audacity", "obs", "streamlabs", "sound", "radio", "clipchamp"]),
        (Creative, ["photoshop", "illustrator", "premiere", "after effects", "lightroom", "gimp", "krita", "inkscape", "blender", "figma", "canva", "paint", "davinci", "resolve", "affinity", "clip studio", "aseprite", "fl studio", "ableton", "reaper", "unity", "unreal", "godot", "designer", "3d", "draw"]),
        (Productivity, ["word", "excel", "powerpoint", "onenote", "access", "publisher", "office", "notion", "obsidian", "evernote", "calendar", "to do", "todo", "notepad", "sticky notes", "acrobat", "pdf", "calibre", "kindle", "reader", "calculator", "clock", "alarms", "maps", "weather", "news", "copilot", "chatgpt", "claude", "bionic", "falvion", "planner", "journal", "whiteboard", "e-book", "books", "scanner", "print", "joplin", "power automate", "logseq", "anki"]),
        (Development, ["visual studio", "code", "terminal", "powershell", "command prompt", "developer", "git", "python", "node", "idle", "jupyter", "intellij", "pycharm", "rider", "webstorm", "android studio", "docker", "wsl", "ubuntu", "debian", "postman", "sql", "antigravity", "cursor", "windsurf", "neovim", "vim", "sublime", "x64", "x86", "native tools", "sdk", "compiler", "dev home", "pgadmin", "postgresql", "stack builder", "debuggable", "application verifier", "psql", "software development kit", "cert kit", "tools for uwp", "tools for desktop", "sample uwp", "sample desktop", "documentation for"]),
        (System, ["settings", "control panel", "task manager", "file explorer", "explorer", "7-zip", "winrar", "powertoys", "snipping", "defender", "security", "disk", "recovery", "remote desktop", "magnifier", "narrator", "on-screen keyboard", "registry", "services", "event viewer", "system", "device", "driver", "nvidia", "amd", "intel", "realtek", "acer", "dell", "hp ", "lenovo", "asus", "backup", "update", "uninstall", "help", "manual", "readme", "license", "tips", "get started", "feedback", "quick assist", "store", "character map", "odbc", "iscsi", "memory diagnostic", "performance", "resource monitor", "steps recorder", "wordpad", "math input", "fax", "xps", "cleanup", "defragment", "firewall", "hyper-v", "sandbox", "command palette", "computer management", "run", "live captions", "voice access", "family", "nitrosense", "equalizer", "hesuvi", "geforce", "dts", "windows tools", "task scheduler"]),
    ];

    /// <summary>The best guess for one app.</summary>
    public static string Categorize(LauncherItem item)
    {
        var id = item.Id.ToLowerInvariant();
        foreach (var (category, keys) in IdentityRules)
        {
            if (keys.Any(id.Contains)) return category;
        }

        if (StartMenuFolder(item.FileSystemPath) is { } folder)
        {
            foreach (var (category, keys) in FolderRules)
            {
                if (keys.Any(folder.Contains)) return category;
            }
        }

        var name = " " + item.DisplayName.ToLowerInvariant() + " ";
        foreach (var (category, keys) in NameRules)
        {
            if (keys.Any(key => ContainsWord(name, key))) return category;
        }

        return Other;
    }

    /// <summary>
    /// A key matches at a word start, so "code" matches "Visual Studio Code"
    /// but not "Unicode". Keys ending in a space are matched as-is.
    /// </summary>
    private static bool ContainsWord(string name, string key)
    {
        var index = name.IndexOf(key, StringComparison.Ordinal);
        while (index >= 0)
        {
            if (index == 0 || !char.IsLetterOrDigit(name[index - 1])) return true;
            index = name.IndexOf(key, index + 1, StringComparison.Ordinal);
        }
        return false;
    }

    /// <summary>"...\Start Menu\Programs\Steam\Game.lnk" gives "steam".</summary>
    private static string? StartMenuFolder(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        const string marker = @"\programs\";
        var lower = path.ToLowerInvariant();
        var start = lower.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0) return null;
        var rest = lower[(start + marker.Length)..];
        var slash = rest.IndexOf(global::System.IO.Path.DirectorySeparatorChar);
        return slash > 0 ? rest[..slash] : null;
    }
}
