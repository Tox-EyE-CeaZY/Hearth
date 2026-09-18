using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hearth.Core.Settings;

/// <summary>Whether tablet mode is forced off, forced on, or follows the hardware.</summary>
public enum TabletModeSetting
{
    Off,
    On,
    /// <summary>Tablet mode whenever no keyboard (or mouse) is attached, like Windows 10.</summary>
    Auto,
}

public enum ScreenEdge { Bottom, Top, Left, Right }

/// <summary>What a swipe in from a screen edge does.</summary>
public enum EdgeAction
{
    None,
    TaskSwitcher,
    Start,
    QuickSettings,
    Notifications,
    CloseApp,
    Desktop,
}

/// <summary>How one keyboard, mouse or touchpad counts in Auto mode.</summary>
public enum DeviceRule
{
    /// <summary>Hearth decides (see PeripheralWatcher for the rules).</summary>
    Auto,
    /// <summary>While attached, Auto mode stays in desktop mode.</summary>
    Counts,
    /// <summary>Never affects Auto mode (hardware buttons, a dongle left plugged in).</summary>
    Ignore,
}

/// <summary>Whether Auto mode believes the convertible/slate sensor.</summary>
public enum SlateSensorUse
{
    /// <summary>Only on hardware that says it is a tablet or 2-in-1, or once the sensor has been seen to change.</summary>
    Auto,
    Always,
    Never,
}

/// <summary>The key the navigation bar's Back button sends.</summary>
public enum BackKey
{
    AltLeft,
    BrowserBack,
    Escape,
    Backspace,
    None,
}

/// <summary>
/// Tablet mode settings, kept in their own file (tablet.json) the way the
/// weather settings are, so the main settings file stays about the desktop.
/// Only ever add properties: older files must keep loading.
/// </summary>
public sealed class TabletSettings
{
    // ---- Mode ---------------------------------------------------------------

    public TabletModeSetting Mode { get; set; } = TabletModeSetting.Auto;

    // ---- Auto mode ----------------------------------------------------------

    /// <summary>Auto never picks tablet mode on a machine without a touchscreen.</summary>
    public bool RequireTouchscreen { get; set; } = true;

    /// <summary>A mouse or touchpad on its own keeps Auto in desktop mode.</summary>
    public bool MouseMeansDesktop { get; set; } = true;

    public SlateSensorUse SlateSensor { get; set; } = SlateSensorUse.Auto;

    /// <summary>Show a prompt instead of switching straight away.</summary>
    public bool AskBeforeSwitching { get; set; }

    /// <summary>How long the hardware has to stay put before Auto switches (plugging in is noisy).</summary>
    public int SwitchDelayMs { get; set; } = 1200;

    /// <summary>Per-device choices, keyed by <c>PeripheralDevice.Key</c>.</summary>
    public Dictionary<string, DeviceRule> Devices { get; set; } = [];

    /// <summary>Names remembered for devices in <see cref="Devices"/>, so the list can show absent ones.</summary>
    public Dictionary<string, string> DeviceNames { get; set; } = [];

    // ---- What tablet mode changes -------------------------------------------

    public bool HideTaskbar { get; set; } = true;

    public bool NavigationBar { get; set; } = true;

    public ScreenEdge NavigationBarEdge { get; set; } = ScreenEdge.Bottom;

    /// <summary>Thickness of the navigation bar, in DIPs.</summary>
    public double NavigationBarSize { get; set; } = 48;

    public bool NavShowStart { get; set; } = true;
    public bool NavShowKeyboard { get; set; } = true;
    public bool NavShowQuickSettings { get; set; } = true;
    public bool NavShowNotifications { get; set; } = true;
    public bool NavShowClock { get; set; } = true;

    /// <summary>Start fills the screen instead of floating above the taskbar.</summary>
    public bool FullScreenStart { get; set; } = true;

    public bool AutoMaximize { get; set; } = true;

    /// <summary>Maximize the windows already open when tablet mode starts.</summary>
    public bool MaximizeExisting { get; set; } = true;

    /// <summary>Put windows Hearth maximized back to their size when tablet mode ends.</summary>
    public bool RestoreSizesOnExit { get; set; } = true;

    /// <summary>Program file names (e.g. "calc.exe") that are never maximized.</summary>
    public List<string> MaximizeExceptions { get; set; } = [];

    public bool EdgeGestures { get; set; } = true;

    /// <summary>Edge strips also react to a mouse drag (normally touch and pen only).</summary>
    public bool EdgeGesturesWithMouse { get; set; }

    public EdgeAction LeftEdge { get; set; } = EdgeAction.TaskSwitcher;
    public EdgeAction RightEdge { get; set; } = EdgeAction.None;
    public EdgeAction TopEdge { get; set; } = EdgeAction.CloseApp;
    public EdgeAction BottomEdge { get; set; } = EdgeAction.Start;

    public BackKey DefaultBackKey { get; set; } = BackKey.AltLeft;

    /// <summary>Back key per program file name, overriding <see cref="DefaultBackKey"/>.</summary>
    public Dictionary<string, BackKey> BackKeys { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Ctrl+Win+T switches tablet mode on and off.</summary>
    public bool Hotkey { get; set; } = true;

    public EdgeAction ActionFor(ScreenEdge edge) => edge switch
    {
        ScreenEdge.Left => LeftEdge,
        ScreenEdge.Right => RightEdge,
        ScreenEdge.Top => TopEdge,
        _ => BottomEdge,
    };

    // ---- Persistence --------------------------------------------------------

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Hearth", "tablet.json");

    public static TabletSettings Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (!File.Exists(path)) return new TabletSettings();
            var loaded = JsonSerializer.Deserialize<TabletSettings>(File.ReadAllText(path), SerializerOptions) ?? new TabletSettings();
            // JSON gives back an ordinal dictionary; program names compare without case.
            loaded.BackKeys = new Dictionary<string, BackKey>(loaded.BackKeys, StringComparer.OrdinalIgnoreCase);
            return loaded;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Same rule as settings.json: a bad file must never keep the desktop from starting.
            System.Diagnostics.Debug.WriteLine($"[Hearth] tablet settings load failed, using defaults: {ex.Message}");
            return new TabletSettings();
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
}
