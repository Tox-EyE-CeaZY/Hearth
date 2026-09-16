using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;

namespace Hearth.App.Widgets;

/// <summary>
/// CPU, memory, battery and system-drive usage.
///
/// Reads the kernel directly (GetSystemTimes, GlobalMemoryStatusEx,
/// GetSystemPowerStatus) rather than through performance counters, which in
/// .NET 8 would mean an extra package and a noticeably slower first sample.
/// </summary>
public sealed class SystemWidget : IWidget
{
    public string Id => "system";
    public string Title => "System";
    public (int Columns, int Rows) DefaultSpan => (3, 2);
    public (int Columns, int Rows) MinimumSpan => (2, 2);

    public FrameworkElement CreateView(WidgetContext context) => new SystemView(context);

    private sealed class SystemView : ContentControl
    {
        private readonly Row _cpu;
        private readonly Row _memory;
        private readonly Row _battery;
        private readonly Row _disk;
        private readonly DispatcherTimer _tick;

        private ulong _lastIdle, _lastTotal;

        public SystemView(WidgetContext context)
        {
            _cpu = new Row(context, "\uE950", "CPU");
            _memory = new Row(context, "\uE964", "Memory");
            _battery = new Row(context, "\uE83F", "Battery");
            _disk = new Row(context, "\uEDA2", "Disk (C:)");

            var rows = new UniformGrid
            {
                Columns = 1,
                Children = { _cpu, _memory, _battery, _disk },
            };

            Content = WidgetChrome.Card(context, rows);

            _tick = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
            _tick.Tick += (_, _) => Sample();

            Loaded += (_, _) => { Sample(); _tick.Start(); };
            Unloaded += (_, _) => _tick.Stop();
        }

        private void Sample()
        {
            SampleCpu();
            SampleMemory();
            SampleBattery();
            SampleDisk();
        }

        /// <summary>
        /// Busy fraction since the previous sample. Kernel time includes idle
        /// time, which is why idle is subtracted from kernel + user.
        /// </summary>
        private void SampleCpu()
        {
            if (!GetSystemTimes(out var idleTime, out var kernelTime, out var userTime)) return;

            var idle = idleTime.ToUInt64();
            var total = kernelTime.ToUInt64() + userTime.ToUInt64();

            if (_lastTotal != 0 && total > _lastTotal)
            {
                var busy = 1.0 - (double)(idle - _lastIdle) / (total - _lastTotal);
                _cpu.Set(busy, $"{busy * 100:F0}%");
            }

            _lastIdle = idle;
            _lastTotal = total;
        }

        private void SampleMemory()
        {
            var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            if (!GlobalMemoryStatusEx(ref status) || status.ullTotalPhys == 0) return;

            var used = status.ullTotalPhys - status.ullAvailPhys;
            _memory.Set((double)used / status.ullTotalPhys, $"{Gb(used):F1} / {Gb(status.ullTotalPhys):F0} GB");
        }

        private void SampleBattery()
        {
            // 128 = no system battery; 255 = unknown. Desktops simply hide the row.
            if (!GetSystemPowerStatus(out var power) || power.BatteryFlag == 128 || power.BatteryLifePercent == 255)
            {
                _battery.Visibility = Visibility.Collapsed;
                return;
            }

            _battery.Visibility = Visibility.Visible;
            var charging = power.ACLineStatus == 1;
            var level = power.BatteryLifePercent / 100.0;
            _battery.Set(level, charging ? $"{power.BatteryLifePercent}% · charging" : $"{power.BatteryLifePercent}%");
            _battery.Warn = !charging && power.BatteryLifePercent <= 20;
        }

        private void SampleDisk()
        {
            try
            {
                var root = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
                var drive = new DriveInfo(root);
                if (!drive.IsReady || drive.TotalSize == 0) return;

                var used = drive.TotalSize - drive.AvailableFreeSpace;
                _disk.Set((double)used / drive.TotalSize, $"{Gb((ulong)drive.AvailableFreeSpace):F0} GB free");
            }
            catch (IOException)
            {
                // A drive going away mid-sample is not worth surfacing.
            }
        }

        private static double Gb(ulong bytes) => bytes / 1024.0 / 1024.0 / 1024.0;

        /// <summary>One labelled bar.</summary>
        private sealed class Row : Grid
        {
            private readonly TextBlock _value;
            private readonly BarView _bar;
            private static readonly Brush WarnBrush = WidgetChrome.Frozen(new SolidColorBrush(Color.FromRgb(0xF2, 0x8B, 0x82)));

            public Row(WidgetContext context, string glyph, string label)
            {
                var s = context.Scale;
                VerticalAlignment = VerticalAlignment.Center;
                Margin = new Thickness(0, 2 * s, 0, 2 * s);

                ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26 * s) });
                ColumnDefinitions.Add(new ColumnDefinition());
                ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                var icon = WidgetChrome.Glyph(context, glyph, 14, WidgetChrome.Secondary);
                icon.HorizontalAlignment = HorizontalAlignment.Left;
                Grid.SetRowSpan(icon, 2);

                var name = WidgetChrome.Text(context, label, 13, weight: FontWeights.SemiBold);
                Grid.SetColumn(name, 1);

                _value = WidgetChrome.Text(context, "…", 12, WidgetChrome.Secondary);
                Grid.SetColumn(_value, 2);

                _bar = new BarView { Height = 4 * s, Margin = new Thickness(0, 5 * s, 0, 0) };
                Grid.SetRow(_bar, 1);
                Grid.SetColumn(_bar, 1);
                Grid.SetColumnSpan(_bar, 2);

                Children.Add(icon);
                Children.Add(name);
                Children.Add(_value);
                Children.Add(_bar);
            }

            public bool Warn
            {
                set => _bar.Fill = value ? WarnBrush : WidgetChrome.Accent;
            }

            public void Set(double fraction, string text)
            {
                _bar.Value = fraction;
                _value.Text = text;
            }
        }

        // ---- Kernel interop -------------------------------------------------

        [StructLayout(LayoutKind.Sequential)]
        private struct FILETIME
        {
            public uint Low;
            public uint High;
            public readonly ulong ToUInt64() => ((ulong)High << 32) | Low;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SYSTEM_POWER_STATUS
        {
            public byte ACLineStatus;
            public byte BatteryFlag;
            public byte BatteryLifePercent;
            public byte SystemStatusFlag;
            public int BatteryLifeTime;
            public int BatteryFullLifeTime;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetSystemTimes(out FILETIME idle, out FILETIME kernel, out FILETIME user);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);
    }
}
