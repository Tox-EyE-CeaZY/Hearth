using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Hearth.App.Hosting;
using Hearth.App.Widgets.Audio;
using Hearth.Core.Diagnostics;
using Hearth.Core.Shell;
using Hearth.Core.Interop;

namespace Hearth.App.Widgets.QuickToggles;

/// <summary>
/// Android-style quick action tiles: Mute audio, Night Light, Dark/Light Theme,
/// Screen Snip, Empty Recycle Bin, and Lock Workstation.
/// </summary>
public sealed class QuickTogglesWidget : IWidget
{
    public string Id => "toggles";
    public string Title => "Quick Toggles";
    public (int Columns, int Rows) DefaultSpan => (3, 2);
    public (int Columns, int Rows) MinimumSpan => (2, 2);

    public FrameworkElement CreateView(WidgetContext context) => new QuickTogglesView(context);

    public double BoardHeight(bool wide) => wide ? 110 : 140;
    public int Order => 80;

    public bool OnBoardByDefault => true;

    private sealed class QuickTogglesView : ContentControl
    {
        private readonly WidgetContext _context;
        private readonly UniformGrid _grid;
        private readonly TogglePill _mutePill;
        private readonly TogglePill _themePill;
        private readonly TogglePill _snipPill;
        private readonly TogglePill _nightLightPill;
        private readonly TogglePill _binPill;
        private readonly TogglePill _lockPill;
        private readonly DispatcherTimer _pollTimer;

        public QuickTogglesView(WidgetContext context)
        {
            _context = context;
            var s = context.Scale;

            _mutePill = new TogglePill(context, "\uE74F", "Mute", "Audio", OnToggleMute);
            _themePill = new TogglePill(context, "\uE708", "Theme", context.DarkTheme ? "Dark" : "Light", OnToggleTheme);
            _snipPill = new TogglePill(context, "\uE924", "Snip", "Capture", OnScreenSnip);
            _nightLightPill = new TogglePill(context, "\uE706", "Night Light", "Display", OnNightLight);
            _binPill = new TogglePill(context, "\uE74D", "Bin", "Check...", OnEmptyBin);
            _lockPill = new TogglePill(context, "\uE72E", "Lock", "Lock PC", OnLockWorkstation);

            _grid = new UniformGrid
            {
                Columns = 3,
                Rows = 2,
            };

            Content = WidgetChrome.Card(context, _grid);

            SizeChanged += (_, _) => LayoutTiles();

            _pollTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromSeconds(5)
            };
            _pollTimer.Tick += (_, _) => RefreshStates();

            Loaded += (_, _) =>
            {
                AudioService.Current.VolumeChanged += OnVolumeChanged;
                RefreshStates();
                _pollTimer.Start();
            };

            Unloaded += (_, _) =>
            {
                AudioService.Current.VolumeChanged -= OnVolumeChanged;
                _pollTimer.Stop();
            };
        }

        private void OnVolumeChanged(bool isInput, float level, bool isMuted)
        {
            if (!isInput) Dispatcher.InvokeAsync(RefreshMuteState);
        }

        private void RefreshStates()
        {
            RefreshMuteState();
            RefreshBinState();
            _themePill.Subtitle = App.Settings.DarkTheme ? "Dark" : "Light";
        }

        private void RefreshMuteState()
        {
            var isMuted = AudioService.Current.GetMute(false);
            _mutePill.IsActive = isMuted;
            _mutePill.Glyph = isMuted ? "\uE74F" : "\uE767";
            _mutePill.Title = isMuted ? "Muted" : "Sound";
        }

        private void RefreshBinState()
        {
            try
            {
                var info = new RecycleBin.SHQUERYRBINFO { cbSize = (uint)Marshal.SizeOf<RecycleBin.SHQUERYRBINFO>() };
                if (RecycleBin.SHQueryRecycleBin(null, ref info) == 0)
                {
                    var count = info.i64NumItems;
                    _binPill.Subtitle = count > 0 ? $"{count} items" : "Empty";
                    _binPill.IsActive = count > 0;
                }
            }
            catch { }
        }

        private void LayoutTiles()
        {
            var s = _context.Scale;
            var w = ActualWidth;
            var h = ActualHeight;
            if (w <= 0 || h <= 0) return;

            _grid.Children.Clear();

            // Sizing: at 2x2 or compact dimensions, show the 4 core pills
            var isCompact = w < 240 * s || h < 140 * s;

            if (isCompact)
            {
                _grid.Columns = 2;
                _grid.Rows = 2;
                _grid.Children.Add(_mutePill);
                _grid.Children.Add(_themePill);
                _grid.Children.Add(_snipPill);
                _grid.Children.Add(_binPill);
            }
            else
            {
                if (w / h > 2.2)
                {
                    _grid.Columns = 6;
                    _grid.Rows = 1;
                }
                else
                {
                    _grid.Columns = 3;
                    _grid.Rows = 2;
                }

                _grid.Children.Add(_mutePill);
                _grid.Children.Add(_themePill);
                _grid.Children.Add(_snipPill);
                _grid.Children.Add(_nightLightPill);
                _grid.Children.Add(_binPill);
                _grid.Children.Add(_lockPill);
            }

            var gap = Math.Clamp(w * 0.02, 3 * s, 8 * s);
            foreach (UIElement child in _grid.Children)
            {
                if (child is FrameworkElement elem)
                    elem.Margin = new Thickness(gap);
            }
        }

        private void OnToggleMute()
        {
            var isMuted = AudioService.Current.GetMute(false);
            AudioService.Current.SetMute(false, !isMuted);
            RefreshMuteState();
        }

        private void OnToggleTheme()
        {
            App.Settings.DarkTheme = !App.Settings.DarkTheme;
            _themePill.Subtitle = App.Settings.DarkTheme ? "Dark" : "Light";
            DesktopHost.Current?.Surface?.ApplySettingsChange(reRenderIcons: false);
        }

        private static void OnScreenSnip()
        {
            try
            {
                ShellLauncher.Open("ms-screenclip:");
            }
            catch (Exception ex)
            {
                Log.Write($"snip launch failed: {ex.Message}");
            }
        }

        private static void OnNightLight()
        {
            try
            {
                ShellLauncher.Open("ms-settings:nightlight");
            }
            catch (Exception ex)
            {
                Log.Write($"night light settings failed: {ex.Message}");
            }
        }

        private void OnEmptyBin()
        {
            try
            {
                RecycleBin.SHEmptyRecycleBin(IntPtr.Zero, null, 0);
                RefreshBinState();
            }
            catch (Exception ex)
            {
                Log.Write($"empty recycle bin failed: {ex.Message}");
            }
        }

        private static void OnLockWorkstation()
        {
            try
            {
                Win32.LockWorkStation();
            }
            catch (Exception ex)
            {
                Log.Write($"lock workstation failed: {ex.Message}");
            }
        }

        private sealed class TogglePill : Border
        {
            private readonly TextBlock _glyphBlock;
            private readonly TextBlock _titleBlock;
            private readonly TextBlock _subBlock;
            private readonly Action _onClick;

            // Captured while the theme is in scope; later state changes happen outside it.
            private readonly Brush _track = WidgetChrome.Track;
            private readonly Brush _hover = WidgetChrome.Hover;
            private readonly Brush _accent = WidgetChrome.Accent;
            private readonly Brush _onAccent = WidgetChrome.OnAccent;
            private readonly Brush _primary = WidgetChrome.Primary;
            private readonly Brush _secondary = WidgetChrome.Secondary;
            private bool _pressed;
            private bool _isActive;

            public TogglePill(WidgetContext context, string glyph, string title, string subtitle, Action onClick)
            {
                _onClick = onClick;
                var s = context.Scale;

                CornerRadius = new CornerRadius(12 * s);
                Background = _track;
                Cursor = Cursors.Hand;
                Padding = new Thickness(10 * s, 6 * s, 10 * s, 6 * s);

                _glyphBlock = WidgetChrome.Glyph(context, glyph, 16, WidgetChrome.Primary);
                _glyphBlock.HorizontalAlignment = HorizontalAlignment.Left;
                _glyphBlock.VerticalAlignment = VerticalAlignment.Center;
                _glyphBlock.Margin = new Thickness(0, 0, 8 * s, 0);

                _titleBlock = WidgetChrome.Text(context, title, 12, weight: FontWeights.SemiBold);
                _titleBlock.TextTrimming = TextTrimming.CharacterEllipsis;

                _subBlock = WidgetChrome.Text(context, subtitle, 10.5, WidgetChrome.Secondary);
                _subBlock.TextTrimming = TextTrimming.CharacterEllipsis;

                var textStack = new StackPanel
                {
                    VerticalAlignment = VerticalAlignment.Center,
                    Children = { _titleBlock, _subBlock }
                };

                var layout = new Grid();
                layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                layout.ColumnDefinitions.Add(new ColumnDefinition());
                Grid.SetColumn(textStack, 1);
                layout.Children.Add(_glyphBlock);
                layout.Children.Add(textStack);

                Child = layout;

                MouseEnter += (_, _) => UpdateAppearance(isHovered: true);
                MouseLeave += (_, _) => { _pressed = false; UpdateAppearance(isHovered: false); };
            }

            public bool IsActive
            {
                get => _isActive;
                set
                {
                    _isActive = value;
                    UpdateAppearance(isHovered: IsMouseOver);
                }
            }

            public string Glyph
            {
                get => _glyphBlock.Text;
                set => _glyphBlock.Text = value;
            }

            public string Title
            {
                get => _titleBlock.Text;
                set => _titleBlock.Text = value;
            }

            public string Subtitle
            {
                get => _subBlock.Text;
                set => _subBlock.Text = value;
            }

            private void UpdateAppearance(bool isHovered)
            {
                if (_isActive)
                {
                    Background = _accent;
                    _glyphBlock.Foreground = _onAccent;
                    _titleBlock.Foreground = _onAccent;
                    _subBlock.Foreground = _onAccent;
                    Opacity = isHovered ? 0.9 : 1.0;
                }
                else
                {
                    Background = isHovered ? _hover : _track;
                    _glyphBlock.Foreground = _primary;
                    _titleBlock.Foreground = _primary;
                    _subBlock.Foreground = _secondary;
                    Opacity = 1.0;
                }
            }

            protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
            {
                base.OnMouseLeftButtonDown(e);
                _pressed = true;
                e.Handled = true;
            }

            protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
            {
                base.OnMouseLeftButtonUp(e);
                if (_pressed)
                {
                    _pressed = false;
                    _onClick();
                }
                e.Handled = true;
            }
        }
    }
}
