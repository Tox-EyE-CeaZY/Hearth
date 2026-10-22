using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Hearth.App.Controls;
using Hearth.App.Hosting;
using Hearth.Core.Diagnostics;
using Hearth.Core.Icons;
using Hearth.Core.Settings;
using Microsoft.Win32;
using static Hearth.App.Views.SettingsScreen.SettingsStyle;

namespace Hearth.App.Views.SettingsScreen;

/// <summary>
/// Hearth's settings in one place (plan, section 9): tablet mode and its
/// parts, the home screen, Start, and startup. The desktop's right-click
/// menu still has the common ones; both change the same settings.
///
/// One window at a time. Pages are built when shown, so they always show
/// current values; the tablet page also refreshes its live parts (status,
/// attached devices) while open.
/// </summary>
internal sealed partial class SettingsWindow : Window
{
    private static SettingsWindow? _open;

    private readonly StackPanel _nav = new();
    private readonly ContentControl _page = new();
    private readonly ScrollViewer _scroll;
    private readonly Dictionary<string, (NavEntry Entry, Func<FrameworkElement> Build)> _pages = [];
    private string _current = "tablet";

    public static void Open(string? page = null)
    {
        try
        {
            _open ??= new SettingsWindow();
            _open.ShowPage(string.IsNullOrWhiteSpace(page) ? _open._current : page.Trim().ToLowerInvariant());
            if (!_open.IsVisible) _open.Show();
            if (_open.WindowState == WindowState.Minimized) _open.WindowState = WindowState.Normal;
            StartTrigger.ClaimForegroundRight();
            _open.Activate();
        }
        catch (Exception ex)
        {
            Log.Error("settings window", ex);
        }
    }

    private SettingsWindow()
    {
        DialogChrome.Apply(this);
        Resources[typeof(Slider)] = SliderStyle.Create();
        Title = "Hearth settings";
        Width = 940;
        Height = 720;
        MinWidth = 520;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        AddPage("tablet", 0xE70A, "Tablet mode", BuildTabletPage);
        AddPage("tabletparts", 0xE771, "Tablet features", BuildTabletPartsPage);
        AddPage("home", 0xE80F, "Home screen", BuildHomePage);
        AddPage("start", 0xF0E2, "Start", BuildStartPage);
        AddPage("bar", 0xE700, "Bar", BuildBarPage);
        AddPage("startup", 0xE7E8, "Startup", BuildStartupPage);
        AddPage("about", 0xE946, "About", BuildAboutPage);

        _nav.Margin = new Thickness(10, 14, 10, 10);
        var navHost = new Border
        {
            Width = 230,
            Background = DialogChrome.Brush("#FF18181C"),
            BorderBrush = CardEdge,
            BorderThickness = new Thickness(0, 0, 1, 0),
            Child = new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = "Settings", FontSize = 13, Foreground = Muted, Margin = new Thickness(22, 18, 0, 0) },
                    _nav,
                },
            },
        };

        _scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            PanningMode = PanningMode.VerticalOnly,
            Content = new Border { Padding = new Thickness(32, 24, 32, 32), MaxWidth = 820, HorizontalAlignment = HorizontalAlignment.Left, Child = _page },
        };

        var root = new DockPanel();
        DockPanel.SetDock(navHost, Dock.Left);
        root.Children.Add(navHost);
        root.Children.Add(_scroll);
        Content = root;

        if (Tablet.TabletMode.Current is { } tablet) tablet.Changed += OnTabletChanged;
        Closed += (_, _) =>
        {
            if (Tablet.TabletMode.Current is { } mode) mode.Changed -= OnTabletChanged;
            _open = null;
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            Close();
        };
    }

    private void AddPage(string key, int glyph, string title, Func<FrameworkElement> build)
    {
        var entry = new NavEntry(key, glyph, title);
        entry.Clicked += e => ShowPage(e.Key);
        _nav.Children.Add(entry);
        _pages[key] = (entry, build);
    }

    public void ShowPage(string key)
    {
        if (!_pages.ContainsKey(key)) key = "tablet";
        _current = key;
        foreach (var (pageKey, page) in _pages) page.Entry.IsActive = pageKey == key;
        _liveStatus = null;
        _liveDevices = null;
        try
        {
            _page.Content = _pages[key].Build();
        }
        catch (Exception ex)
        {
            Log.Error($"settings page {key}", ex);
            _page.Content = Note($"This page could not be shown: {ex.Message}", Warn);
        }
        _scroll.ScrollToTop();
    }

    // ---- Home screen ------------------------------------------------------------------

    private static FrameworkElement BuildHomePage()
    {
        var s = App.Settings;
        var page = new StackPanel();
        page.Children.Add(Heading("Home screen"));

        void Apply(bool icons) => DesktopHost.Current?.Surface?.ApplySettingsChange(reRenderIcons: icons);

        page.Children.Add(Section("Icons"));
        page.Children.Add(Card(
            ChoiceRow("Icon shape", "Every icon is fitted into this shape.",
                Enum.GetValues<IconShapeKind>().Select(k => (k, k.ToString())), s.IconShape,
                v => { s.IconShape = v; Apply(true); }, wrap: true),
            ChoiceRow("Icon size", null,
                new double[] { 48, 56, 64, 72, 88, 104 }.Select(v => (v, $"{v:F0}")),
                new double[] { 48, 56, 64, 72, 88, 104 }.OrderBy(v => Math.Abs(v - s.IconSize)).First(),
                v => { s.IconSize = v; Apply(true); }, wrap: true),
            SliderRow("Space between icons", null, 8, 64, 2, s.GridGap, v => $"{v:F0}",
                v => { s.GridGap = v; Apply(false); }),
            ToggleRow("Show labels", null, s.ShowLabels, v => { s.ShowLabels = v; Apply(false); }),
            ToggleRow("Uniform backgrounds", "Give every icon a generated background, even ones that already fill the shape.",
                s.UniformBackgrounds, v => { s.UniformBackgrounds = v; Apply(true); }),
            ToggleRow("Icon shadows", null, s.BakeShadows, v => { s.BakeShadows = v; Apply(true); }),
            ToggleRow("Sharpen small icons",
                "Some programs ship nothing bigger than a 32 px icon. Those are enlarged along their own edges " +
                "rather than blurred up. It cannot invent detail that was never there.",
                s.SharpenSmallIcons, v => { s.SharpenSmallIcons = v; Apply(true); }),
            Row("Icon quality",
                "Icons are pulled from Windows at 256 px and re-rendered whenever the shape, size or scale changes, " +
                "and a newly installed app is rendered the first time it is shown. Rebuild them if one looks wrong or " +
                "was cached before you changed something.",
                Button("Rebuild every icon", () =>
                {
                    App.Icons.ClearDiskCache();
                    DesktopHost.Current?.Surface?.ApplySettingsChange(reRenderIcons: true);
                    StartMenuController.Current?.InvalidateWindow();
                    Log.Write("settings: icon cache cleared; every icon will be rendered again");
                }))));

        page.Children.Add(Section("Theme"));
        page.Children.Add(Card(
            ChoiceRow("Hearth's own light and dark",
                "Auto follows \"Choose your default Windows mode\", and changes with it while Hearth is running.",
                new[] { (HearthTheme.Auto, "Auto"), (HearthTheme.Light, "Light"), (HearthTheme.Dark, "Dark") },
                s.Theme,
                v =>
                {
                    s.Theme = v;
                    s.Save();
                    Hosting.HearthTheming.Reapply();
                })));

        page.Children.Add(Section("Layout and behaviour"));
        page.Children.Add(Card(
            ChoiceRow("Home style", "Android: free placement, roomy. iOS: packed, tighter.",
                Enum.GetValues<HomeStyle>().Select(v => (v, v == HomeStyle.Ios ? "iOS" : v.ToString())), s.Style,
                v => { s.Style = v; Apply(false); }),
            ToggleRow("Open with a single click", null, s.LaunchOnSingleClick, v => { s.LaunchOnSingleClick = v; Apply(false); }),
            ToggleRow("Open folders in the centre", "Otherwise a folder opens over its own icon.", s.CenterOpenFolders,
                v => { s.CenterOpenFolders = v; s.Save(); }),
            SliderRow("Dim the wallpaper", "Makes labels easier to read.", 0, 0.6, 0.05, s.WallpaperDim, v => $"{v * 100:F0}%",
                v => { s.WallpaperDim = v; Apply(false); }),
            ToggleRow("Hide the Windows desktop icons", "They come back whenever Hearth closes.", s.HideShellIcons,
                v => DesktopHost.Current?.Surface?.SetHideShellIcons(v)),
            ToggleRow("Wallpaper Engine support",
                "While Wallpaper Engine is running, Hearth stops painting its own wallpaper and lets Wallpaper Engine show through. " +
                "Turn it off to always use Hearth's wallpaper (and the dimming above).",
                s.WallpaperEngineSupport,
                v => DesktopHost.Current?.Surface?.SetWallpaperEngineSupport(v)),
            ToggleRow("Show every installed app", "Otherwise only desktop items and the apps you add.", s.IncludeInstalledApps,
                v => _ = DesktopHost.Current?.Surface?.SetIncludeInstalledAppsAsync(v))));

        page.Children.Add(Note("Widgets, hidden items and adding apps are on the desktop's right-click menu."));
        return page;
    }

    // ---- Start ------------------------------------------------------------------------

    private static FrameworkElement BuildStartPage()
    {
        var s = App.Settings;
        var page = new StackPanel();
        page.Children.Add(Heading("Start"));

        var search = new TextBox { Text = s.WebSearchUrl, Width = 340 };
        search.LostKeyboardFocus += (_, _) =>
        {
            var value = search.Text.Trim();
            if (value.Length == 0 || value == s.WebSearchUrl) return;
            s.WebSearchUrl = value;
            s.Save();
        };

        page.Children.Add(Card(
            ToggleRow("Open Hearth's Start with the Windows key and Start button",
                "Ctrl+Esc always opens Windows' own Start.", s.ReplaceStartMenu,
                v => StartMenuController.Current?.SetReplaceStart(v)),
            ChoiceRow("Background", "Mica and Acrylic turn flat grey when Windows transparency effects are off (Energy Saver does that).",
                new[]
                {
                    (StartBackdrop.Blur, "Blur"), (StartBackdrop.Mica, "Mica"),
                    (StartBackdrop.MicaAlt, "Mica Alt"), (StartBackdrop.Acrylic, "Acrylic"),
                }, s.StartBackdrop,
                v =>
                {
                    s.StartBackdrop = v;
                    s.Save();
                    StartMenuController.Current?.InvalidateWindow();
                }, wrap: true),
            Row("Web search address", "{0} is replaced by what you typed.", search)));

        page.Children.Add(Section("Where it opens"));
        page.Children.Add(Card(
            ChoiceRow("Position", "Above the taskbar is where Windows' own Start sits. Every other choice detaches the menu from the taskbar and puts it in the work area.",
                new[]
                {
                    (StartPlacement.AboveTaskbar, "Above the taskbar"),
                    (StartPlacement.BottomLeft, "Bottom left"),
                    (StartPlacement.BottomCentre, "Bottom centre"),
                    (StartPlacement.BottomRight, "Bottom right"),
                    (StartPlacement.LeftCentre, "Left"),
                    (StartPlacement.Centre, "Middle"),
                    (StartPlacement.CentreLarge, "Middle, larger"),
                    (StartPlacement.RightCentre, "Right"),
                    (StartPlacement.TopLeft, "Top left"),
                    (StartPlacement.TopCentre, "Top centre"),
                    (StartPlacement.TopRight, "Top right"),
                }, s.StartPlacement,
                v => { s.StartPlacement = v; s.Save(); StartMenuController.Current?.InvalidateWindow(); }, wrap: true),
            ChoiceRow("Density", "Compact gives the pages, apps and widgets about a tenth more height by tightening the search box, tabs and footer. Auto is compact in the middle of the screen and standard everywhere else.",
                new[]
                {
                    (StartDensity.Auto, "Auto"), (StartDensity.Standard, "Standard"), (StartDensity.Compact, "Compact"),
                }, s.StartDensity,
                v => { s.StartDensity = v; s.Save(); StartMenuController.Current?.InvalidateWindow(); }),
            SliderRow("Size", "The menu keeps its shape and is scaled as a whole; a display with no room for it shrinks it anyway.",
                0.7, 1.6, 0.05, s.StartScale, v => $"{v * 100:F0}%",
                v => { s.StartScale = v; s.Save(); }),
            SliderRow("Gap from the edges", null, 0, 80, 4, s.StartMargin, v => $"{v:F0}",
                v => { s.StartMargin = v; s.Save(); })));

        page.Children.Add(Section("Tablet mode"));
        page.Children.Add(Card(
            ToggleRow("Fill the screen in tablet mode", null, Tablet.TabletMode.Current?.Settings.FullScreenStart ?? true, v =>
            {
                if (Tablet.TabletMode.Current is not { } tablet) return;
                tablet.Settings.FullScreenStart = v;
                tablet.SaveSettingsAndApply();
            })));
        return page;
    }

    // ---- Startup ------------------------------------------------------------------------

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private static FrameworkElement BuildStartupPage()
    {
        var page = new StackPanel();
        page.Children.Add(Heading("Startup"));

        var exe = Environment.ProcessPath ?? string.Empty;
        string? current;
        using (var key = Registry.CurrentUser.OpenSubKey(RunKey)) current = key?.GetValue("Hearth") as string;
        var enabled = !string.IsNullOrEmpty(current);
        var pointsHere = enabled && current!.Trim('"').Equals(exe, StringComparison.OrdinalIgnoreCase);

        var note = !enabled
            ? "Hearth does not start by itself."
            : pointsHere ? "Hearth starts when you sign in." : $"Sign-in starts a different copy: {current}";

        page.Children.Add(Card(
            ToggleRow("Start Hearth when I sign in", note, enabled, v =>
            {
                try
                {
                    using var key = Registry.CurrentUser.CreateSubKey(RunKey);
                    if (v) key.SetValue("Hearth", $"\"{exe}\"");
                    else key.DeleteValue("Hearth", throwOnMissingValue: false);
                    Log.Write($"settings: start at sign-in {(v ? "on (" + exe + ")" : "off")}");
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
                {
                    Log.Error("settings: run key", ex);
                }
            })));
        page.Children.Add(Note($"This copy: {exe}"));
        return page;
    }

    // ---- About --------------------------------------------------------------------------

    private static FrameworkElement BuildAboutPage()
    {
        var page = new StackPanel();
        page.Children.Add(Heading("About Hearth"));
        var version = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?";
        var plus = version.IndexOf('+');
        if (plus > 0) version = version[..plus];

        var appData = Path.GetDirectoryName(HearthSettings.DefaultPath)!;
        var buttons = new WrapPanel
        {
            Margin = new Thickness(16, 4, 16, 14),
            Children =
            {
                Button("Open the log", () => OpenPath(Log.FilePath)),
                Button("Open the settings folder", () => OpenPath(appData)),
            },
        };
        page.Children.Add(Card(
            Row("Version", null, new TextBlock { Text = version, Foreground = Muted }),
            Row("Quit Hearth", "The Windows desktop, taskbar and icons come straight back.",
                Button("Quit", () => Application.Current.Shutdown())),
            buttons));
        return page;
    }

    private static void OpenPath(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            Log.Write($"settings: could not open {path}: {ex.Message}");
        }
    }
}
