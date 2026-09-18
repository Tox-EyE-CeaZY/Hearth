using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Hearth.App.Controls;
using Hearth.App.Tablet;
using Hearth.Core.Settings;
using static Hearth.App.Views.SettingsScreen.SettingsStyle;

namespace Hearth.App.Views.SettingsScreen;

internal sealed partial class SettingsWindow
{
    private Border? _liveStatus;
    private Border? _liveDevices;

    private void OnTabletChanged()
    {
        if (!IsVisible || TabletMode.Current is not { } tablet) return;
        if (_liveStatus is not null) _liveStatus.Child = BuildStatus(tablet);
        if (_liveDevices is not null) _liveDevices.Child = BuildDevices(tablet);
    }

    // ---- Tablet mode ------------------------------------------------------------------

    private FrameworkElement BuildTabletPage()
    {
        var page = new StackPanel();
        page.Children.Add(Heading("Tablet mode"));
        if (TabletMode.Current is not { } tablet)
        {
            page.Children.Add(Note("Tablet mode is not running.", Warn));
            return page;
        }
        var s = tablet.Settings;

        _liveStatus = new Border { Child = BuildStatus(tablet) };
        page.Children.Add(_liveStatus);

        page.Children.Add(Section("Mode"));
        page.Children.Add(Card(
            ChoiceRow("Tablet mode", "Auto switches by itself when the keyboard and mouse come and go.",
                new[] { (TabletModeSetting.Auto, "Auto"), (TabletModeSetting.On, "On"), (TabletModeSetting.Off, "Off") },
                s.Mode, tablet.SetMode),
            ToggleRow("Ctrl+Win+T switches tablet mode", tablet.HotkeyActive || !s.Hotkey
                    ? "In Auto, a switch by hand lasts until a keyboard or mouse is plugged in or removed."
                    : "Another program has this shortcut, so it is not working right now.",
                s.Hotkey, v => { s.Hotkey = v; tablet.SaveSettingsAndApply(); })));

        page.Children.Add(Section("How Auto decides"));
        page.Children.Add(Card(
            ToggleRow("Only on a touchscreen", "Leave this on unless you want Auto on a machine you can't touch.",
                s.RequireTouchscreen, v => { s.RequireTouchscreen = v; tablet.SaveSettingsAndApply(); }),
            ToggleRow("A mouse or touchpad means desktop mode", "Off: only a keyboard keeps tablet mode away.",
                s.MouseMeansDesktop, v => { s.MouseMeansDesktop = v; tablet.SaveSettingsAndApply(); }),
            ChoiceRow("Use the slate sensor", "2-in-1s report when the keyboard is folded back or removed. Auto trusts it on hardware that says it is a tablet or 2-in-1, or once it has been seen to change.",
                new[] { (SlateSensorUse.Auto, "Auto"), (SlateSensorUse.Always, "Always"), (SlateSensorUse.Never, "Never") },
                s.SlateSensor, v => { s.SlateSensor = v; tablet.SaveSettingsAndApply(); }, wrap: true),
            ToggleRow("Ask before switching", "Show a prompt instead of switching straight away.",
                s.AskBeforeSwitching, v => { s.AskBeforeSwitching = v; tablet.SaveSettingsAndApply(); }),
            SliderRow("Wait before switching", "Plugging things in is noisy; the hardware has to settle first.",
                0, 5, 0.25, s.SwitchDelayMs / 1000.0, v => $"{v:0.##} s",
                v => { s.SwitchDelayMs = (int)Math.Round(v * 1000); tablet.SaveSettingsAndApply(); })));

        page.Children.Add(Section("Keyboards, mice and touchpads"));
        page.Children.Add(Note("Auto: Hearth decides. Counts: while attached, Auto stays in desktop mode. Ignore: never affects Auto (for hardware buttons that show up as a keyboard, or a receiver left plugged in). Detach and attach your keyboard to see which entry changes."));
        _liveDevices = new Border { Margin = new Thickness(0, 10, 0, 0), Child = BuildDevices(tablet) };
        page.Children.Add(_liveDevices);
        return page;
    }

    private static FrameworkElement BuildStatus(TabletMode tablet)
    {
        var decision = tablet.LastDecision;
        var on = tablet.IsActive;

        var title = new TextBlock
        {
            Text = on ? "Tablet mode is on" : "Tablet mode is off",
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
        };
        var why = Note(decision?.Reason ?? "");
        why.FontSize = 13.5;
        why.Margin = new Thickness(0, 4, 0, 0);

        var details = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        if (tablet.ManualOverride is { } manual)
            details.Children.Add(Note($"Switched {(manual ? "on" : "off")} by hand; Auto takes over again when a keyboard or mouse is plugged in or removed.", Warn));
        if (decision is not null)
        {
            var m = decision.Machine;
            details.Children.Add(Note(
                $"Touchscreen: {(m.HasTouchscreen ? $"yes, {m.MaxTouches} touch points" : "none")}  ·  " +
                $"Hardware: {m.ChassisText}  ·  " +
                $"Slate sensor: {(m.SlateSensorSaysSlate ? "slate" : "laptop")} ({(decision.SensorTrusted ? "used" : "not used")})",
                Faint));
        }

        var buttons = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(Button(on ? "Leave tablet mode" : "Enter tablet mode", tablet.Toggle, primary: true));
        buttons.Children.Add(Button("Check the hardware again", () => tablet.Evaluate(settled: true, prompt: false)));

        return new Border
        {
            Background = on ? DialogChrome.Brush("#268AB4F8") : SettingsStyle.CardBrush,
            BorderBrush = on ? Accent : CardEdge,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(18, 16, 18, 16),
            Child = new StackPanel { Children = { title, why, details, buttons } },
        };
    }

    private static FrameworkElement BuildDevices(TabletMode tablet)
    {
        var s = tablet.Settings;
        var rows = new List<UIElement>();
        var decision = tablet.LastDecision;
        var present = decision?.Devices ?? [];

        foreach (var verdict in present)
        {
            var d = verdict.Device;
            var where = d.Internal ? "built in" : "external";
            var detail = string.IsNullOrEmpty(d.Detail) ? "" : $" ({d.Detail})";
            var status = verdict.Counts ? "Counts" : "Ignored";
            var note = $"{d.KindText}, {where}{detail}. {status}: {verdict.Why}";
            rows.Add(DeviceRow(tablet, d.Key, d.Name, note, verdict.Counts ? Good : Faint, verdict.Rule));
        }

        // Devices with a rule that are not attached right now: still editable.
        foreach (var (key, rule) in s.Devices)
        {
            if (present.Any(v => v.Device.Key == key)) continue;
            var name = s.DeviceNames.TryGetValue(key, out var n) ? n : key;
            rows.Add(DeviceRow(tablet, key, name, "Not attached right now.", Faint, rule, forgettable: true));
        }

        if (rows.Count == 0) return Card(Row("No keyboards, mice or touchpads found", null, null));
        return Card(rows.ToArray());
    }

    private static FrameworkElement DeviceRow(TabletMode tablet, string key, string name, string note, Brush dot, DeviceRule rule, bool forgettable = false)
    {
        var s = tablet.Settings;
        var choice = new Segmented<DeviceRule>(
            new[] { (DeviceRule.Auto, "Auto"), (DeviceRule.Counts, "Counts"), (DeviceRule.Ignore, "Ignore") },
            rule,
            v =>
            {
                if (v == DeviceRule.Auto && !forgettable)
                {
                    s.Devices.Remove(key);
                    s.DeviceNames.Remove(key);
                }
                else
                {
                    s.Devices[key] = v;
                    s.DeviceNames[key] = name;
                }
                tablet.SaveSettingsAndApply();
            });

        var controls = new StackPanel { Orientation = Orientation.Horizontal };
        controls.Children.Add(choice);
        if (forgettable)
        {
            controls.Children.Add(Button("Forget", () =>
            {
                s.Devices.Remove(key);
                s.DeviceNames.Remove(key);
                tablet.SaveSettingsAndApply();
            }));
        }

        var header = new StackPanel { Orientation = Orientation.Horizontal };
        header.Children.Add(new System.Windows.Shapes.Ellipse { Width = 8, Height = 8, Fill = dot, Margin = new Thickness(0, 1, 8, 0), VerticalAlignment = VerticalAlignment.Center });
        header.Children.Add(new TextBlock { Text = name, FontSize = 14, FontWeight = FontWeights.SemiBold });

        var text = Note(note);
        text.Margin = new Thickness(16, 3, 0, 0);

        return new StackPanel
        {
            Margin = new Thickness(16, 12, 14, 12),
            Children =
            {
                header,
                text,
                new Border { Margin = new Thickness(16, 8, 0, 0), Child = controls },
            },
        };
    }

    // ---- Tablet features ---------------------------------------------------------------

    private FrameworkElement BuildTabletPartsPage()
    {
        var page = new StackPanel();
        page.Children.Add(Heading("Tablet features"));
        if (TabletMode.Current is not { } tablet)
        {
            page.Children.Add(Note("Tablet mode is not running.", Warn));
            return page;
        }
        var s = tablet.Settings;
        void Apply() => tablet.SaveSettingsAndApply();

        page.Children.Add(Note("Each part can be switched off on its own. Changes apply straight away, even while tablet mode is on."));

        page.Children.Add(Section("Screen"));
        page.Children.Add(Card(
            ToggleRow("Hide the taskbar", "It comes back when tablet mode ends or Hearth closes, even after a crash.",
                s.HideTaskbar, v => { s.HideTaskbar = v; Apply(); }),
            ToggleRow("Start fills the screen", null, s.FullScreenStart, v => { s.FullScreenStart = v; Apply(); })));

        page.Children.Add(Section("Navigation bar"));
        page.Children.Add(Card(
            ToggleRow("Show the navigation bar", "Back, Home and Recent apps, like a phone.", s.NavigationBar,
                v => { s.NavigationBar = v; Apply(); }),
            ChoiceRow("Position", null,
                new[] { (ScreenEdge.Bottom, "Bottom"), (ScreenEdge.Top, "Top"), (ScreenEdge.Left, "Left"), (ScreenEdge.Right, "Right") },
                s.NavigationBarEdge, v => { s.NavigationBarEdge = v; Apply(); }, wrap: true),
            SliderRow("Height", null, 36, 80, 4, s.NavigationBarSize, v => $"{v:F0}",
                v => { s.NavigationBarSize = v; Apply(); }),
            ToggleRow("Start button", null, s.NavShowStart, v => { s.NavShowStart = v; Apply(); }),
            ToggleRow("Touch keyboard button", null, s.NavShowKeyboard, v => { s.NavShowKeyboard = v; Apply(); }),
            ToggleRow("Quick settings button", null, s.NavShowQuickSettings, v => { s.NavShowQuickSettings = v; Apply(); }),
            ToggleRow("Notifications button", null, s.NavShowNotifications, v => { s.NavShowNotifications = v; Apply(); }),
            ToggleRow("Clock", null, s.NavShowClock, v => { s.NavShowClock = v; Apply(); })));

        page.Children.Add(Section("Back button"));
        page.Children.Add(Card(
            ChoiceRow("What Back sends", "Desktop apps have no single Back; Alt+Left works in browsers, File Explorer and most apps.",
                BackKeyOptions, s.DefaultBackKey, v => { s.DefaultBackKey = v; Apply(); }, wrap: true),
            BackOverrides(tablet)));

        page.Children.Add(Section("Windows"));
        page.Children.Add(Card(
            ToggleRow("Open apps maximized", null, s.AutoMaximize, v => { s.AutoMaximize = v; Apply(); }),
            ToggleRow("Maximize apps already open", "When tablet mode starts.", s.MaximizeExisting, v => { s.MaximizeExisting = v; Apply(); }),
            ToggleRow("Put window sizes back afterwards", "When tablet mode ends, windows Hearth maximized go back to their size.",
                s.RestoreSizesOnExit, v => { s.RestoreSizesOnExit = v; Apply(); }),
            ProgramList("Never maximize", "Program file names, one per line (for example calc.exe).", s.MaximizeExceptions, Apply)));

        page.Children.Add(Section("Edge swipes"));
        var edgeOptions = new[]
        {
            (EdgeAction.None, "Nothing"), (EdgeAction.TaskSwitcher, "Recent apps"), (EdgeAction.Start, "Start"),
            (EdgeAction.QuickSettings, "Quick settings"), (EdgeAction.Notifications, "Notifications"),
            (EdgeAction.CloseApp, "Drag to close"), (EdgeAction.Desktop, "Desktop"),
        };
        page.Children.Add(Card(
            ToggleRow("Swipe in from the edges", "Starts with touch or a pen. The edge next to the navigation bar is left to the bar.",
                s.EdgeGestures, v => { s.EdgeGestures = v; Apply(); }),
            ToggleRow("Mouse drags count too", "Off keeps the screen edges free for the mouse.", s.EdgeGesturesWithMouse,
                v => { s.EdgeGesturesWithMouse = v; Apply(); }),
            ChoiceRow("From the left", null, edgeOptions, s.LeftEdge, v => { s.LeftEdge = v; Apply(); }, wrap: true),
            ChoiceRow("From the right", "Windows opens notifications from here itself on most tablets.", edgeOptions, s.RightEdge, v => { s.RightEdge = v; Apply(); }, wrap: true),
            ChoiceRow("From the top", "Drag to close: pull the app down; let go at the bottom to close it, at a side to snap it.",
                edgeOptions, s.TopEdge, v => { s.TopEdge = v; Apply(); }, wrap: true),
            ChoiceRow("From the bottom", null, edgeOptions, s.BottomEdge, v => { s.BottomEdge = v; Apply(); }, wrap: true)));

        page.Children.Add(Section("Advanced tablet features"));
        page.Children.Add(Card(
            Row("Helper", "Not installed yet. A later step adds an optional helper that runs with UI access, so swipes can start anywhere and elevated apps can be maximized and closed too. It asks for administrator approval once when you install it.", null)));
        return page;
    }

    private static readonly (BackKey, string)[] BackKeyOptions =
    [
        (BackKey.AltLeft, "Alt+Left"), (BackKey.BrowserBack, "Browser Back key"),
        (BackKey.Escape, "Esc"), (BackKey.Backspace, "Backspace"), (BackKey.None, "Nothing"),
    ];

    private static FrameworkElement BackOverrides(TabletMode tablet)
    {
        var s = tablet.Settings;
        var list = new StackPanel();
        foreach (var (program, key) in s.BackKeys.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
        {
            var row = new DockPanel { Margin = new Thickness(0, 4, 0, 4) };
            var remove = Button("Remove", () =>
            {
                s.BackKeys.Remove(program);
                tablet.SaveSettingsAndApply();
                SettingsWindow._open?.ShowPage("tabletparts");
            });
            DockPanel.SetDock(remove, Dock.Right);
            row.Children.Add(remove);
            row.Children.Add(new TextBlock
            {
                Text = $"{program}: {BackKeyOptions.First(o => o.Item1 == key).Item2}",
                VerticalAlignment = VerticalAlignment.Center,
            });
            list.Children.Add(row);
        }

        var name = new TextBox { Width = 180, Margin = new Thickness(0, 0, 8, 0) };
        var chosen = BackKey.Escape;
        var picker = new Segmented<BackKey>(BackKeyOptions, chosen, v => chosen = v);
        var add = Button("Add", () =>
        {
            var program = name.Text.Trim();
            if (program.Length == 0) return;
            if (!program.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) program += ".exe";
            s.BackKeys[program] = chosen;
            tablet.SaveSettingsAndApply();
            SettingsWindow._open?.ShowPage("tabletparts");
        });

        var addRow = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        addRow.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { name, add } });
        picker.Margin = new Thickness(0, 6, 0, 0);
        addRow.Children.Add(picker);
        list.Children.Add(addRow);

        return Row("Per app", "A different key for particular programs (type the program file name).", list, wrapControl: true);
    }

    private static FrameworkElement ProgramList(string title, string description, List<string> items, Action save)
    {
        var box = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            MinHeight = 72,
            VerticalContentAlignment = VerticalAlignment.Top,
            Text = string.Join(Environment.NewLine, items),
        };
        box.LostKeyboardFocus += (_, _) =>
        {
            var lines = box.Text
                .Split([(char)10, (char)13], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (lines.SequenceEqual(items, StringComparer.OrdinalIgnoreCase)) return;
            items.Clear();
            items.AddRange(lines);
            save();
        };
        return Row(title, description, box, wrapControl: true);
    }
}
