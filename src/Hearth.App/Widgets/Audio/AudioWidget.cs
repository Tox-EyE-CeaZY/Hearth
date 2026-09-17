using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Hearth.Core.Diagnostics;

namespace Hearth.App.Widgets.Audio;

/// <summary>
/// Audio device switcher and master volume/mute controls for playback (output)
/// and recording (input/mic).
/// </summary>
public sealed class AudioWidget : IWidget
{
    public string Id => "audio";
    public string Title => "Audio";
    public (int Columns, int Rows) DefaultSpan => (3, 2);
    public (int Columns, int Rows) MinimumSpan => (2, 2);

    public FrameworkElement CreateView(WidgetContext context) => new AudioView(context);

    public double BoardHeight(bool wide) => wide ? 130 : 150;
    public int Order => 70;

    public bool OnBoardByDefault => true;

    private sealed class AudioView : ContentControl
    {
        private readonly WidgetContext _context;
        private readonly DeviceRow _outputRow;
        private readonly DeviceRow _inputRow;

        public AudioView(WidgetContext context)
        {
            _context = context;
            var s = context.Scale;

            _outputRow = new DeviceRow(context, isInput: false);
            _inputRow = new DeviceRow(context, isInput: true);

            var stack = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                Children = { _outputRow, _inputRow }
            };

            Content = WidgetChrome.Card(context, stack);

            SizeChanged += (_, _) =>
            {
                var h = ActualHeight;
                var gap = h >= 180 * s ? 16 * s : h >= 140 * s ? 10 * s : 6 * s;
                _outputRow.Margin = new Thickness(0, 0, 0, gap);
            };

            Loaded += (_, _) =>
            {
                AudioService.Current.DevicesChanged += OnDevicesChanged;
                AudioService.Current.VolumeChanged += OnVolumeChanged;
                Refresh();
            };

            Unloaded += (_, _) =>
            {
                AudioService.Current.DevicesChanged -= OnDevicesChanged;
                AudioService.Current.VolumeChanged -= OnVolumeChanged;
            };
        }

        private void OnDevicesChanged() =>
            Dispatcher.InvokeAsync(Refresh);

        private void OnVolumeChanged(bool isInput, float level, bool isMuted) =>
            Dispatcher.InvokeAsync(() =>
            {
                if (isInput) _inputRow.UpdateVolume(level, isMuted);
                else _outputRow.UpdateVolume(level, isMuted);
            });

        private void Refresh()
        {
            _outputRow.Refresh();
            _inputRow.Refresh();
        }

        private sealed class DeviceRow : Grid
        {
            private const string SpeakerGlyph = "\uE767";
            private const string MuteGlyph = "\uE74F";
            private const string MicGlyph = "\uE720";
            private const string MicMuteGlyph = "\uF781";
            private const string ChevronGlyph = "\uE70D";

            private readonly WidgetContext _context;
            private readonly bool _isInput;
            private readonly GlyphButton _muteBtn;
            private readonly TextBlock _nameText;
            private readonly TextBlock _percentText;
            private readonly VolumeBar _volumeBar;
            private readonly Border _devicePickerBtn;

            public DeviceRow(WidgetContext context, bool isInput)
            {
                _context = context;
                _isInput = isInput;
                var s = context.Scale;

                RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32 * s) });
                ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                // Mute / Icon Button
                var defaultGlyph = isInput ? MicGlyph : SpeakerGlyph;
                _muteBtn = new GlyphButton(context, defaultGlyph, 28, ToggleMute);
                Grid.SetRowSpan(_muteBtn, 2);
                Children.Add(_muteBtn);

                // Device selector (name + dropdown chevron)
                _nameText = WidgetChrome.Text(context, isInput ? "Microphone" : "Speaker", 12.5, weight: FontWeights.SemiBold);
                _nameText.TextTrimming = TextTrimming.CharacterEllipsis;
                _nameText.VerticalAlignment = VerticalAlignment.Center;

                var chevron = WidgetChrome.Glyph(context, ChevronGlyph, 9, WidgetChrome.Secondary);
                chevron.Margin = new Thickness(6 * s, 1 * s, 0, 0);

                var pickerContent = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Children = { _nameText, chevron }
                };

                _devicePickerBtn = new Border
                {
                    Background = Brushes.Transparent,
                    CornerRadius = new CornerRadius(4 * s),
                    Padding = new Thickness(4 * s, 2 * s, 6 * s, 2 * s),
                    Margin = new Thickness(-4 * s, 0, 0, 2 * s),
                    Cursor = Cursors.Hand,
                    Child = pickerContent,
                    HorizontalAlignment = HorizontalAlignment.Left
                };
                _devicePickerBtn.MouseEnter += (_, _) => _devicePickerBtn.Background = WidgetChrome.Hover;
                _devicePickerBtn.MouseLeave += (_, _) => _devicePickerBtn.Background = Brushes.Transparent;
                _devicePickerBtn.MouseLeftButtonDown += (s_sender, e) =>
                {
                    e.Handled = true;
                    ShowDeviceMenu();
                };

                Grid.SetColumn(_devicePickerBtn, 1);
                Children.Add(_devicePickerBtn);

                // Percentage readout
                _percentText = WidgetChrome.Text(context, "100%", 11.5, WidgetChrome.Secondary);
                _percentText.VerticalAlignment = VerticalAlignment.Center;
                _percentText.Margin = new Thickness(6 * s, 0, 0, 2 * s);
                Grid.SetColumn(_percentText, 2);
                Children.Add(_percentText);

                // Volume Bar
                _volumeBar = new VolumeBar(context, OnSliderMoved)
                {
                    Height = 7 * s,
                    Margin = new Thickness(0, 4 * s, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetRow(_volumeBar, 1);
                Grid.SetColumn(_volumeBar, 1);
                Grid.SetColumnSpan(_volumeBar, 2);
                Children.Add(_volumeBar);
            }

            private void ToggleMute()
            {
                var isMuted = AudioService.Current.GetMute(_isInput);
                AudioService.Current.SetMute(_isInput, !isMuted);
                Refresh();
            }

            private void OnSliderMoved(float level)
            {
                AudioService.Current.SetVolume(_isInput, level);
                if (AudioService.Current.GetMute(_isInput) && level > 0.01f)
                    AudioService.Current.SetMute(_isInput, false);
                Refresh();
            }

            public void Refresh()
            {
                var def = AudioService.Current.GetDefaultDevice(_isInput);
                _nameText.Text = def?.Name ?? (_isInput ? "No Microphone" : "No Speaker");

                var vol = AudioService.Current.GetVolume(_isInput);
                var isMuted = AudioService.Current.GetMute(_isInput);
                UpdateVolume(vol, isMuted);
            }

            public void UpdateVolume(float level, bool isMuted)
            {
                _volumeBar.Value = isMuted ? 0 : level;
                _volumeBar.IsMuted = isMuted;
                _percentText.Text = isMuted ? "Muted" : $"{Math.Round(level * 100):F0}%";

                if (_isInput)
                {
                    _muteBtn.GlyphText = isMuted ? MicMuteGlyph : MicGlyph;
                }
                else
                {
                    _muteBtn.GlyphText = isMuted || level == 0 ? MuteGlyph : SpeakerGlyph;
                }
            }

            private void ShowDeviceMenu()
            {
                var devices = AudioService.Current.GetDevices(_isInput);
                if (devices.Count == 0) return;

                var menu = new ContextMenu
                {
                    PlacementTarget = _devicePickerBtn,
                    Placement = PlacementMode.Bottom,
                };

                foreach (var dev in devices)
                {
                    var item = new MenuItem
                    {
                        Header = dev.Name,
                        IsCheckable = true,
                        IsChecked = dev.IsDefault,
                        FontFamily = WidgetChrome.Body,
                        FontSize = 13,
                    };

                    item.Click += (_, e) =>
                    {
                        e.Handled = true;
                        AudioService.Current.SetDefaultDevice(dev.Id);
                        Refresh();
                    };

                    menu.Items.Add(item);
                }

                menu.IsOpen = true;
            }
        }

        private sealed class VolumeBar : FrameworkElement
        {
            private readonly Action<float> _onMoved;
            private readonly Brush _track = WidgetChrome.Track;
            private readonly Brush _accent = WidgetChrome.Accent;
            private readonly Brush _faint = WidgetChrome.Faint;
            private double _value = 1.0;
            private bool _isMuted;
            private bool _isDragging;

            public VolumeBar(WidgetContext context, Action<float> onMoved)
            {
                _onMoved = onMoved;
                Cursor = Cursors.Hand;
            }

            public double Value
            {
                get => _value;
                set
                {
                    _value = Math.Clamp(value, 0.0, 1.0);
                    InvalidateVisual();
                }
            }

            public bool IsMuted
            {
                get => _isMuted;
                set
                {
                    _isMuted = value;
                    InvalidateVisual();
                }
            }

            protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
            {
                base.OnMouseLeftButtonDown(e);
                _isDragging = true;
                CaptureMouse();
                UpdateFromMouse(e.GetPosition(this).X);
                e.Handled = true;
            }

            protected override void OnMouseMove(MouseEventArgs e)
            {
                base.OnMouseMove(e);
                if (_isDragging)
                {
                    UpdateFromMouse(e.GetPosition(this).X);
                    e.Handled = true;
                }
            }

            protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
            {
                base.OnMouseLeftButtonUp(e);
                if (_isDragging)
                {
                    _isDragging = false;
                    ReleaseMouseCapture();
                    UpdateFromMouse(e.GetPosition(this).X);
                    e.Handled = true;
                }
            }

            private void UpdateFromMouse(double mouseX)
            {
                if (ActualWidth <= 0) return;
                var frac = Math.Clamp(mouseX / ActualWidth, 0.0, 1.0);
                Value = frac;
                _onMoved((float)frac);
            }

            protected override void OnRender(DrawingContext dc)
            {
                var w = RenderSize.Width;
                var h = RenderSize.Height;
                if (w <= 0 || h <= 0) return;

                var radius = h / 2.0;
                dc.DrawRoundedRectangle(_track, null, new Rect(0, 0, w, h), radius, radius);

                var fillWidth = w * _value;
                if (fillWidth > 0)
                {
                    var brush = _isMuted ? _faint : _accent;
                    dc.DrawRoundedRectangle(brush, null, new Rect(0, 0, Math.Max(h, fillWidth), h), radius, radius);
                }
            }
        }
    }
}
