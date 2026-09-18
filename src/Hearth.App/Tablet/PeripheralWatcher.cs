using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;
using Hearth.Core.Diagnostics;

namespace Hearth.App.Tablet;

/// <summary>
/// Tells tablet mode when the hardware may have changed: a keyboard, mouse or
/// touchpad arriving or leaving, the convertible sensor flipping, a display
/// change (touchscreens come and go with docks), and the tablet hotkey.
///
/// It owns a hidden top-level window, because the broadcasts it needs
/// (WM_SETTINGCHANGE for the slate sensor, WM_DISPLAYCHANGE) are not sent to
/// message-only windows.
/// </summary>
internal sealed class PeripheralWatcher : IDisposable
{
    private static readonly Guid KeyboardInterface = new("884b96c3-56ef-11d1-bc8c-00a0c91405dd");
    private static readonly Guid MouseInterface = new("378de44c-56ef-11d1-bc8c-00a0c91405dd");
    private static readonly Guid HidInterface = new("4d1e55b2-f16f-11cf-88cb-001111000030");

    private readonly HwndSource _window;
    private readonly List<IntPtr> _notifications = [];
    private readonly DispatcherTimer _debounce;
    private readonly DispatcherTimer _poll;
    private bool _hotkeyRegistered;
    private bool _lastSlate;
    private string _lastDevices = string.Empty;

    /// <summary>Something may have changed; raised on the UI thread, debounced.</summary>
    public event Action? Changed;

    public event Action? HotkeyPressed;

    /// <summary>The slate sensor has been seen to change while Hearth ran, which proves this machine has one.</summary>
    public bool SensorSeenChanging { get; private set; }

    public int ChassisType { get; }
    public int PlatformRole { get; }

    public PeripheralWatcher()
    {
        ChassisType = SafeRead(Peripherals.ReadChassisType);
        PlatformRole = SafeRead(Peripherals.ReadPlatformRole);

        _window = new HwndSource(new HwndSourceParameters("Hearth.TabletWatcher")
        {
            WindowStyle = unchecked((int)0x80000000), // WS_POPUP, never shown
            ExtendedWindowStyle = 0x00000080,         // WS_EX_TOOLWINDOW
            Width = 0,
            Height = 0,
        });
        _window.AddHook(WndProc);

        foreach (var guid in new[] { KeyboardInterface, MouseInterface, HidInterface })
        {
            var filter = new DEV_BROADCAST_DEVICEINTERFACE
            {
                dbcc_size = Marshal.SizeOf<DEV_BROADCAST_DEVICEINTERFACE>(),
                dbcc_devicetype = DBT_DEVTYP_DEVICEINTERFACE,
                dbcc_classguid = guid,
            };
            var handle = RegisterDeviceNotification(_window.Handle, ref filter, DEVICE_NOTIFY_WINDOW_HANDLE);
            if (handle != IntPtr.Zero) _notifications.Add(handle);
            else Log.Write($"tablet: device notifications for {guid} failed ({Marshal.GetLastWin32Error()})");
        }

        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            Changed?.Invoke();
        };

        // Belt and braces: a missed notification must not leave the mode
        // wrong for long. Enumerating takes about 5 ms.
        _poll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _poll.Tick += (_, _) => PollForChanges();
        _poll.Start();

        _lastSlate = GetSystemMetrics(SM_CONVERTIBLESLATEMODE) == 0;
        Log.Write($"tablet: watching input devices (chassis {ChassisType}, platform role {PlatformRole}, slate={_lastSlate})");
    }

    private static int SafeRead(Func<int> read)
    {
        try { return read(); }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or ArgumentException)
        {
            Log.Write($"tablet: machine info unavailable: {ex.Message}");
            return 0;
        }
    }

    public void SetHotkey(bool enabled)
    {
        if (enabled == _hotkeyRegistered) return;
        if (enabled)
        {
            _hotkeyRegistered = RegisterHotKey(_window.Handle, HotkeyId, MOD_CONTROL | MOD_WIN | MOD_NOREPEAT, VK_T);
            Log.Write(_hotkeyRegistered
                ? "tablet: Ctrl+Win+T toggles tablet mode"
                : $"tablet: Ctrl+Win+T is taken by another program ({Marshal.GetLastWin32Error()})");
        }
        else
        {
            UnregisterHotKey(_window.Handle, HotkeyId);
            _hotkeyRegistered = false;
        }
    }

    public bool HotkeyActive => _hotkeyRegistered;

    /// <summary>Remembers what was seen, so the poll only fires on real change.</summary>
    public void NoteDevices(IEnumerable<PeripheralDevice> devices) =>
        _lastDevices = string.Join(",", devices.Select(d => d.Key + ":" + (int)d.Kinds).Order(StringComparer.Ordinal));

    private void PollForChanges()
    {
        try
        {
            var devices = string.Join(",", Peripherals.Enumerate().Select(d => d.Key + ":" + (int)d.Kinds).Order(StringComparer.Ordinal));
            var slate = GetSystemMetrics(SM_CONVERTIBLESLATEMODE) == 0;
            if (devices != _lastDevices || slate != _lastSlate)
            {
                Log.Write("tablet: poll noticed a hardware change");
                if (slate != _lastSlate) SensorSeenChanging = true;
                _lastSlate = slate;
                Poke();
            }
        }
        catch (Exception ex)
        {
            Log.Error("tablet poll", ex);
        }
    }

    public void Poke()
    {
        _debounce.Stop();
        _debounce.Start();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case WM_DEVICECHANGE:
                var change = wParam.ToInt64();
                if (change is DBT_DEVICEARRIVAL or DBT_DEVICEREMOVECOMPLETE or DBT_DEVNODES_CHANGED) Poke();
                break;

            case WM_SETTINGCHANGE:
                var area = lParam != IntPtr.Zero ? Marshal.PtrToStringUni(lParam) : null;
                if (area is "ConvertibleSlateMode" or "UserInteractionMode" or "SystemDockMode")
                {
                    var slate = GetSystemMetrics(SM_CONVERTIBLESLATEMODE) == 0;
                    if (area == "ConvertibleSlateMode")
                    {
                        // The broadcast itself proves there is a sensor.
                        SensorSeenChanging = true;
                        Log.Write($"tablet: slate sensor changed, slate={slate}");
                    }
                    _lastSlate = slate;
                    Poke();
                }
                break;

            case WM_DISPLAYCHANGE:
                Poke();
                break;

            case WM_HOTKEY when wParam.ToInt32() == HotkeyId:
                handled = true;
                HotkeyPressed?.Invoke();
                break;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        _poll.Stop();
        _debounce.Stop();
        SetHotkey(false);
        foreach (var handle in _notifications) UnregisterDeviceNotification(handle);
        _notifications.Clear();
        _window.RemoveHook(WndProc);
        _window.Dispose();
    }

    // ---- Native ---------------------------------------------------------------

    private const int HotkeyId = 0x4854; // "HT"

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEV_BROADCAST_DEVICEINTERFACE
    {
        public int dbcc_size;
        public int dbcc_devicetype;
        public int dbcc_reserved;
        public Guid dbcc_classguid;
        public short dbcc_name;
    }

    private const int WM_DEVICECHANGE = 0x0219;
    private const int WM_SETTINGCHANGE = 0x001A;
    private const int WM_DISPLAYCHANGE = 0x007E;
    private const int WM_HOTKEY = 0x0312;
    private const long DBT_DEVICEARRIVAL = 0x8000;
    private const long DBT_DEVICEREMOVECOMPLETE = 0x8004;
    private const long DBT_DEVNODES_CHANGED = 0x0007;
    private const int DBT_DEVTYP_DEVICEINTERFACE = 5;
    private const int DEVICE_NOTIFY_WINDOW_HANDLE = 0;
    private const int SM_CONVERTIBLESLATEMODE = 0x2003;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_WIN = 0x0008;
    private const uint MOD_NOREPEAT = 0x4000;
    private const uint VK_T = 0x54;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr RegisterDeviceNotification(IntPtr recipient, ref DEV_BROADCAST_DEVICEINTERFACE filter, int flags);

    [DllImport("user32.dll")]
    private static extern bool UnregisterDeviceNotification(IntPtr handle);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hwnd, int id);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
}
