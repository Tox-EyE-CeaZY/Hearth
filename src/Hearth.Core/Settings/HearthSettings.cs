using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Hearth.Core.Icons;

namespace Hearth.Core.Settings;

/// <summary>
/// Android puts the label under every icon; iOS does too but tighter, with a
/// fixed grid and no free placement. The difference is mostly density, label
/// treatment and whether items may sit wherever they are dropped.
/// </summary>
public enum HomeStyle
{
    /// <summary>Free placement, roomy grid, labels under every tile.</summary>
    Android,
    /// <summary>Packed top-left, uniform grid, tighter labels.</summary>
    Ios,
}

public sealed class HearthSettings
{
    // ---- Look -----------------------------------------------------------

    public HomeStyle Style { get; set; } = HomeStyle.Android;

    public IconShapeKind IconShape { get; set; } = IconShapeKind.Squircle;

    /// <summary>Tile edge in DIPs. 64 is roughly Android's density on a phone.</summary>
    public double IconSize { get; set; } = 64;

    /// <summary>Gap between grid cells, in DIPs.</summary>
    public double GridGap { get; set; } = 28;

    public bool ShowLabels { get; set; } = true;

    public bool BakeShadows { get; set; } = true;

    /// <summary>
    /// Force a generated background even on icons that already fill a square.
    /// Maximum uniformity, at the cost of shrinking artwork that did not need
    /// the help.
    /// </summary>
    public bool UniformBackgrounds { get; set; }

    public bool DarkTheme { get; set; } = true;

    /// <summary>0 = wallpaper untouched, 1 = solid scrim. Lifts icon legibility.</summary>
    public double WallpaperDim { get; set; } = 0.15;

    // ---- Behaviour ------------------------------------------------------

    /// <summary>
    /// Hide Explorer's own icon layer while Hearth is running. Nothing is
    /// deleted — the files stay put and the icons come back on exit.
    /// </summary>
    public bool HideShellIcons { get; set; } = true;

    /// <summary>Seed a fresh layout with installed apps as well as desktop files.</summary>
    public bool IncludeInstalledApps { get; set; }

    public bool LaunchOnSingleClick { get; set; } = true;

    // ---- Start menu -------------------------------------------------------

    /// <summary>
    /// Open Hearth's Start menu instead of Windows' when the Windows key is
    /// pressed on its own or the Start button is clicked.
    /// </summary>
    public bool ReplaceStartMenu { get; set; } = true;

    public StartBackdrop StartBackdrop { get; set; } = StartBackdrop.Blur;

    /// <summary>Web search for Start's search box; the query replaces {0}.</summary>
    public string WebSearchUrl { get; set; } = "https://www.google.com/search?q={0}";

    // ---- Widgets ----------------------------------------------------------

    /// <summary>
    /// Where older versions kept the weather widget's settings. The widget
    /// now keeps them itself (weather.json) and moves this across once.
    /// </summary>
    [JsonPropertyName("Weather")]
    public JsonElement? LegacyWeather { get; set; }

    // ---- Persistence ----------------------------------------------------

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Hearth", "settings.json");

    public static HearthSettings Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (!File.Exists(path)) return new HearthSettings();
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<HearthSettings>(json, SerializerOptions) ?? new HearthSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // A broken settings file must never stop the desktop from coming
            // up — the user would have no way back in to fix it.
            System.Diagnostics.Debug.WriteLine($"[Hearth] settings load failed, using defaults: {ex.Message}");
            return new HearthSettings();
        }
    }

    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, SerializerOptions));
        File.Move(temp, path, overwrite: true);
    }

    public IconRenderOptions ToRenderOptions(double scale) => new()
    {
        Shape = IconShape,
        Size = IconSize,
        Scale = scale,
        BakeShadow = BakeShadows,
        ForceBackground = UniformBackgrounds,
        DarkTheme = DarkTheme,
    };
}
