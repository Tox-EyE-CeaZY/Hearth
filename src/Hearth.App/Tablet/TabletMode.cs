using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Hearth.App.Hosting;
using Hearth.App.Views.Start;
using Hearth.Core.Diagnostics;
using Hearth.Core.Settings;

namespace Hearth.App.Tablet;

/// <summary>
/// Tablet mode: decides when it is on, and turns its parts on and off.
///
/// The mode is Off, On, or Auto. Auto follows the hardware (see
/// <see cref="TabletDecision"/>): no keyboard or mouse attached means tablet.
/// In Auto, the hotkey, the Quick Toggles pill or the navigation bar can
/// still flip the mode by hand; that choice lasts until the hardware next
/// changes, the way Windows 10 behaved.
///
/// Every part (taskbar, navigation bar, auto-maximize, edge gestures,
/// full-screen Start) has its own switch and is started and stopped on its
/// own, each step logged, so one failing part never leaves the others stuck.
/// </summary>
internal sealed class TabletMode : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly PeripheralWatcher _watcher;
    private readonly WindowManager _windows;
    private readonly EdgeGestures _edges;
    private readonly DispatcherTimer _enforce;
    private readonly DispatcherTimer _pendingSwitch;
    private NavigationBar? _bar;
    private TaskSwitcher? _switcher;
    private SwitchPrompt? _prompt;
    private bool? _override;
    private string? _overrideFingerprint;
    private bool _disposed;

    public static TabletMode? Current { get; private set; }

    public TabletSettings Settings { get; private set; }

    public bool IsActive { get; private set; }

    public TabletDecision? LastDecision { get; private set; }

    /// <summary>Set when Auto was overruled by hand; cleared when the hardware changes.</summary>
    public bool? ManualOverride => _override;

    public bool HotkeyActive => _watcher.HotkeyActive;

    /// <summary>Raised on the UI thread whenever the state or the reasons behind it change.</summary>
    public event Action? Changed;

    public TabletMode(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        Current = this;
        Settings = TabletSettings.Load();

        // A run that died with the taskbar hidden is undone before anything else.
        TaskbarControl.RecoverFromPreviousRun();

        _watcher = new PeripheralWatcher();
        _watcher.Changed += () => Evaluate();
        _watcher.HotkeyPressed += Toggle;
        _watcher.SetHotkey(Settings.Hotkey);

        _windows = new WindowManager(() => Settings);
        _edges = new EdgeGestures(this);

        _enforce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        _enforce.Tick += (_, _) => TaskbarControl.EnforceHidden();

        _pendingSwitch = new DispatcherTimer();
        _pendingSwitch.Tick += (_, _) =>
        {
            _pendingSwitch.Stop();
            Evaluate(settled: true);
        };

        Log.Write($"tablet: mode setting {Settings.Mode}");
        _ = StartAsync();
    }

    /// <summary>
    /// The first decision runs once the desktop is up, so entering tablet
    /// mode at sign-in never delays the home screen.
    /// </summary>
    private async Task StartAsync()
    {
        await _dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        StartStyle.UseSystemTheme();
        Evaluate(settled: true, prompt: false);
    }

    /// <summary>Reads the hardware and applies the rules, without changing anything.</summary>
    public TabletDecision Decide()
    {
        var devices = Peripherals.Enumerate();
        _watcher.NoteDevices(devices);
        foreach (var device in devices)
        {
            // Remember names, so the settings screen can list devices that are unplugged.
            if (Settings.Devices.ContainsKey(device.Key)) Settings.DeviceNames[device.Key] = device.Name;
        }
        var machine = Peripherals.ReadMachine(_watcher.ChassisType, _watcher.PlatformRole);
        return TabletDecision.Decide(Settings, machine, devices, _watcher.SensorSeenChanging);
    }

    /// <summary>
    /// Re-reads the hardware and switches if needed. In Auto, a change has to
    /// hold for <see cref="TabletSettings.SwitchDelayMs"/> first: plugging a
    /// dock in arrives as a burst of arrivals and removals.
    /// </summary>
    public void Evaluate(bool settled = false, bool prompt = true)
    {
        if (_disposed) return;
        TabletDecision decision;
        try
        {
            decision = Decide();
        }
        catch (Exception ex)
        {
            Log.Error("tablet: evaluate", ex);
            return;
        }

        var previous = LastDecision;
        LastDecision = decision;
        if (previous is null || previous.Fingerprint != decision.Fingerprint || previous.Tablet != decision.Tablet)
            Log.Write($"tablet: decision {(decision.Tablet ? "tablet" : "desktop")}: {decision.Reason}");

        if (_override is not null && decision.Fingerprint != _overrideFingerprint)
        {
            Log.Write("tablet: hardware changed; manual choice cleared");
            _override = null;
        }

        var want = _override ?? decision.Tablet;
        if (want == IsActive)
        {
            _pendingSwitch.Stop();
            ClosePrompt();
            RaiseChanged();
            return;
        }

        if (Settings.Mode == TabletModeSetting.Auto && _override is null && !settled)
        {
            _pendingSwitch.Interval = TimeSpan.FromMilliseconds(Math.Clamp(Settings.SwitchDelayMs, 0, 30000));
            _pendingSwitch.Stop();
            _pendingSwitch.Start();
            RaiseChanged();
            return;
        }

        if (Settings.Mode == TabletModeSetting.Auto && _override is null && Settings.AskBeforeSwitching && prompt)
        {
            Ask(want, decision.Reason);
            RaiseChanged();
            return;
        }

        Apply(want);
    }

    public void SetMode(TabletModeSetting mode)
    {
        Settings.Mode = mode;
        _override = null;
        Save();
        Log.Write($"tablet: mode set to {mode}");
        Evaluate(settled: true, prompt: false);
    }

    /// <summary>The hotkey / pill / bar: flip now. In Auto this overrules the hardware until it changes.</summary>
    public void Toggle()
    {
        ClosePrompt();
        if (Settings.Mode != TabletModeSetting.Auto)
        {
            SetMode(IsActive ? TabletModeSetting.Off : TabletModeSetting.On);
            return;
        }
        var want = !IsActive;
        var decision = LastDecision ?? Decide();
        if (want == decision.Tablet)
        {
            _override = null;
        }
        else
        {
            _override = want;
            _overrideFingerprint = decision.Fingerprint;
        }
        Log.Write($"tablet: switched {(want ? "on" : "off")} by hand (Auto {(_override is null ? "agrees" : "overruled")})");
        Apply(want);
    }

    /// <summary>Leaves tablet mode now, whatever the setting; the panic button.</summary>
    public void ForceDesktop()
    {
        if (!IsActive) return;
        _override = false;
        _overrideFingerprint = (LastDecision ?? Decide()).Fingerprint;
        Apply(false);
    }

    // ---- Switching ------------------------------------------------------------------

    private void Apply(bool on)
    {
        ClosePrompt();
        _pendingSwitch.Stop();
        if (on == IsActive)
        {
            RaiseChanged();
            return;
        }
        var clock = System.Diagnostics.Stopwatch.StartNew();
        if (on) StartParts();
        else StopParts(restoreSizes: Settings.RestoreSizesOnExit);
        IsActive = on;
        Log.Write($"tablet: mode {(on ? "ENTERED" : "LEFT")} in {clock.ElapsedMilliseconds} ms");
        RaiseChanged();
    }

    private void StartParts()
    {
        Step("taskbar", () =>
        {
            if (!Settings.HideTaskbar) return;
            Watchdog.Start();
            HideTaskbarThenRedock();
            _enforce.Start();
        });
        Step("navigation bar", () =>
        {
            if (!Settings.NavigationBar) return;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            _bar = new NavigationBar(this, Settings);
            Log.Write($"tablet: navigation bar built in {clock.ElapsedMilliseconds} ms");
            clock.Restart();
            _bar.Show();
            Log.Write($"tablet: navigation bar shown in {clock.ElapsedMilliseconds} ms");
        });
        Step("auto-maximize", () =>
        {
            if (Settings.AutoMaximize) _windows.Start();
        });
        Step("edge gestures", () =>
        {
            if (Settings.EdgeGestures) _edges.Start(Settings, Settings.NavigationBar ? Settings.NavigationBarEdge : null);
        });
    }

    private void StopParts(bool restoreSizes)
    {
        Step("recent apps", () => _switcher?.HideSwitcher());
        Step("edge gestures", _edges.Stop);
        Step("auto-maximize", () => _windows.Stop(restoreSizes));
        Step("navigation bar", () =>
        {
            _bar?.CloseForExit();
            _bar = null;
        });
        Step("taskbar", () =>
        {
            _enforce.Stop();
            if (TaskbarControl.IsHiddenByUs)
            {
                if (_disposed) TaskbarControl.Restore();
                else TaskbarControl.RestoreInBackground();
            }
            if (_disposed) Watchdog.Stop();
            else Watchdog.StopAfter(TaskbarControl.Flush);
        });
    }

    /// <summary>
    /// The navigation bar asks Windows for its strip while the taskbar may
    /// still reserve space (hiding it runs in the background). Measured on a
    /// Surface Pro 7+ whose taskbar was not auto-hidden: the bar sat a
    /// taskbar's height above the bottom edge. So it claims its strip again
    /// once the taskbar is out of the way.
    /// </summary>
    private void HideTaskbarThenRedock() =>
        TaskbarControl.HideInBackground().ContinueWith(
            _ => _dispatcher.BeginInvoke(() => _bar?.Redock()),
            TaskScheduler.Default);

    private static void Step(string what, Action action)
    {
        try { action(); }
        catch (Exception ex) { Log.Error($"tablet: {what}", ex); }
    }

    /// <summary>Settings changed: save, and bring the running parts in line without leaving tablet mode.</summary>
    public void SaveSettingsAndApply()
    {
        Save();
        _watcher.SetHotkey(Settings.Hotkey);
        if (IsActive)
        {
            // Everything but the taskbar restarts; the taskbar only changes if its own switch did.
            Step("edge gestures", _edges.Stop);
            Step("auto-maximize", () => _windows.Stop(restoreSizes: false));
            Step("navigation bar", () =>
            {
                _bar?.CloseForExit();
                _bar = null;
            });
            if (!Settings.HideTaskbar && TaskbarControl.IsHiddenByUs)
            {
                Step("taskbar", () =>
                {
                    _enforce.Stop();
                    TaskbarControl.RestoreInBackground();
                    Watchdog.StopAfter(TaskbarControl.Flush);
                });
            }
            if (Settings.HideTaskbar && !TaskbarControl.IsHiddenByUs)
            {
                Step("taskbar", () =>
                {
                    Watchdog.Start();
                    HideTaskbarThenRedock();
                    _enforce.Start();
                });
            }
            Step("navigation bar", () =>
            {
                if (!Settings.NavigationBar) return;
                _bar = new NavigationBar(this, Settings);
                _bar.Show();
            });
            Step("auto-maximize", () =>
            {
                if (Settings.AutoMaximize) _windows.Start();
            });
            Step("edge gestures", () =>
            {
                if (Settings.EdgeGestures) _edges.Start(Settings, Settings.NavigationBar ? Settings.NavigationBarEdge : null);
            });
        }
        Evaluate(settled: true, prompt: false);
    }

    private void Save()
    {
        try { Settings.Save(); }
        catch (Exception ex) { Log.Error("tablet settings save", ex); }
    }

    private void RaiseChanged()
    {
        try { Changed?.Invoke(); }
        catch (Exception ex) { Log.Error("tablet: changed handler", ex); }
    }

    // ---- Asking first ---------------------------------------------------------------

    private void Ask(bool tablet, string reason)
    {
        if (_prompt is { IsVisible: true } open && open.Tablet == tablet) return;
        ClosePrompt();
        _prompt = new SwitchPrompt(tablet, reason, accept =>
        {
            _prompt = null;
            var decision = LastDecision ?? Decide();
            if (accept)
            {
                Apply(tablet);
            }
            else
            {
                // "Stay": treat it as a hand-made choice until the hardware changes again.
                _override = IsActive;
                _overrideFingerprint = decision.Fingerprint;
                RaiseChanged();
            }
        });
        _prompt.Show();
    }

    private void ClosePrompt()
    {
        _prompt?.Dismiss();
        _prompt = null;
    }

    // ---- What the parts ask for -----------------------------------------------------

    /// <summary>Whether Start should fill the screen right now.</summary>
    public bool FullScreenStart => IsActive && Settings.FullScreenStart;

    public void OpenStart() => StartMenuController.Current?.Toggle();

    /// <summary>
    /// Home: the desktop, always. Start and Recent apps close first. Win+D
    /// (which Hearth follows) shows the desktop; it is only sent when an app
    /// is in front, because pressed on the desktop it brings the apps back.
    /// </summary>
    public void GoHome()
    {
        _switcher?.HideSwitcher();
        if (StartMenuController.Current is { IsOpen: true } start) start.Toggle();

        var foreground = WindowTools.GetForegroundWindow();
        var onDesktop = DesktopHost.Current?.IsDesktopShown == true ||
                        WindowTools.ClassName(foreground) is "Progman" or "WorkerW";
        if (onDesktop) return;
        WindowTools.SendWinChord('D');
    }

    public void ShowTaskSwitcher(bool withTray = false)
    {
        _switcher ??= new TaskSwitcher();
        if (withTray) _switcher.ShowWithTray();
        else _switcher.Toggle();
    }

    /// <summary>Explorer restarted: its taskbar is visible again and app bars are forgotten.</summary>
    public void OnExplorerRestarted()
    {
        if (!IsActive) return;
        Step("taskbar", TaskbarControl.Reapply);
        Step("navigation bar", () =>
        {
            if (_bar is null) return;
            _bar.CloseForExit();
            _bar = new NavigationBar(this, Settings);
            _bar.Show();
        });
    }

    public void OnDisplaysChanged()
    {
        _bar?.Redock();
        _edges.Reposition();
        _watcher.Poke();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _pendingSwitch.Stop();
        ClosePrompt();
        // Quitting puts the shell back but leaves windows the size they are.
        if (IsActive) StopParts(restoreSizes: false);
        IsActive = false;
        _switcher?.CloseForExit();
        _switcher = null;
        _watcher.Dispose();
        if (ReferenceEquals(Current, this)) Current = null;
    }
}

/// <summary>"Switch to tablet mode?" — shown instead of switching when the user asked to be asked.</summary>
internal sealed class SwitchPrompt : Window
{
    private readonly Action<bool> _answered;
    private readonly DispatcherTimer _timeout;
    private bool _done;

    public bool Tablet { get; }

    public SwitchPrompt(bool tablet, string reason, Action<bool> answered)
    {
        Tablet = tablet;
        _answered = answered;
        StartStyle.UseSystemTheme();

        Title = "Tablet mode — Hearth";
        Width = 360;
        SizeToContent = SizeToContent.Height;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        FontFamily = StartStyle.Body;
        Foreground = StartStyle.Text;
        Background = new SolidColorBrush(StartStyle.IsLight ? Color.FromRgb(0xF7, 0xF7, 0xF9) : Color.FromRgb(0x24, 0x24, 0x2A));
        BorderBrush = StartStyle.CardEdge;
        BorderThickness = new Thickness(1);

        var heading = StartStyle.Label(tablet ? "Switch to tablet mode?" : "Leave tablet mode?", 16, weight: FontWeights.SemiBold);
        var body = StartStyle.Label(reason, 13, StartStyle.Muted);
        body.TextWrapping = TextWrapping.Wrap;
        body.Margin = new Thickness(0, 4, 0, 14);

        var yes = new PressableBorder(StartStyle.Accent, StartStyle.Accent, radius: 6)
        {
            Padding = new Thickness(16, 7, 16, 8),
            Child = StartStyle.Label(tablet ? "Switch" : "Leave", 14, StartStyle.OnAccent, FontWeights.SemiBold),
        };
        var no = new PressableBorder(StartStyle.Card, StartStyle.CardHover, radius: 6)
        {
            Padding = new Thickness(16, 7, 16, 8),
            Margin = new Thickness(8, 0, 0, 0),
            Child = StartStyle.Label("Stay", 14),
        };
        yes.Clicked += _ => Answer(true);
        no.Clicked += _ => Answer(false);

        Content = new StackPanel
        {
            Margin = new Thickness(18, 16, 18, 16),
            Children =
            {
                heading,
                body,
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { yes, no } },
            },
        };

        // Unanswered means "stay": nothing changes behind the user's back.
        _timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        _timeout.Tick += (_, _) => Answer(false);

        Loaded += (_, _) =>
        {
            var work = SystemParameters.WorkArea;
            Left = work.Right - ActualWidth - 16;
            Top = work.Bottom - ActualHeight - 16;
            _timeout.Start();
        };
    }

    private void Answer(bool accept)
    {
        if (_done) return;
        _done = true;
        _timeout.Stop();
        Close();
        _answered(accept);
    }

    public void Dismiss()
    {
        if (_done) return;
        _done = true;
        _timeout.Stop();
        Close();
    }
}
