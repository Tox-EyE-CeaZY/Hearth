using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Hearth.Core.Diagnostics;
using Windows.Media.Control;
using PlaybackStatus = Windows.Media.Control.GlobalSystemMediaTransportControlsSessionPlaybackStatus;

namespace Hearth.App.Widgets.Media;

/// <summary>
/// Now playing: whatever Windows' own media overlay is showing — Spotify,
/// YouTube Music, a browser tab, VLC — with artwork and transport controls.
///
/// Reads from GlobalSystemMediaTransportControlsSessionManager, the same source
/// the volume flyout uses, so it works with any app that reports media to
/// Windows and needs no per-app integration.
/// </summary>
public sealed class MediaWidget : IWidget
{
    public string Id => "media";
    public string Title => "Now playing";
    public (int Columns, int Rows) DefaultSpan => (4, 2);
    public (int Columns, int Rows) MinimumSpan => (3, 1);

    public FrameworkElement CreateView(WidgetContext context) => new MediaView(context);

    public double BoardHeight(bool wide) => wide ? 140 : 170;
    public int Order => 20;

    public bool OnBoardByDefault => true;

    private sealed class MediaView : ContentControl
    {
        private const string PlayGlyph = "\uE768";
        private const string PauseGlyph = "\uE769";
        private const string PreviousGlyph = "\uE892";
        private const string NextGlyph = "\uE893";
        private const string MusicGlyph = "\uE8D6";

        private readonly WidgetContext _context;
        private readonly Border _art;
        private readonly Image _artImage;
        private readonly TextBlock _artPlaceholder;
        private readonly TextBlock _title;
        private readonly TextBlock _artist;
        private readonly GlyphButton _playPause;
        private readonly GlyphButton _previous;
        private readonly GlyphButton _next;
        private readonly BarView _progress;
        private readonly DispatcherTimer _tick;

        private GlobalSystemMediaTransportControlsSessionManager? _manager;
        private GlobalSystemMediaTransportControlsSession? _session;
        private int _refreshGeneration;

        public MediaView(WidgetContext context)
        {
            _context = context;
            var s = context.Scale;

            _artImage = new Image { Stretch = Stretch.UniformToFill };
            _artPlaceholder = WidgetChrome.Glyph(context, MusicGlyph, 26, WidgetChrome.Faint);
            _art = new Border
            {
                CornerRadius = new CornerRadius(14 * s),
                Background = WidgetChrome.Track,
                ClipToBounds = true,
                Child = new Grid { Children = { _artPlaceholder, _artImage } },
                Margin = new Thickness(0, 0, 14 * s, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };

            _title = WidgetChrome.Text(context, "Nothing playing", 16, weight: FontWeights.SemiBold, display: true);
            _artist = WidgetChrome.Text(context, "Play something to see it here", 13, WidgetChrome.Secondary);

            _previous = new GlyphButton(context, PreviousGlyph, 34, () => _ = Control(sn => sn.TrySkipPreviousAsync().AsTask()));
            _playPause = new GlyphButton(context, PlayGlyph, 40, () => _ = Control(sn => sn.TryTogglePlayPauseAsync().AsTask()), filled: true);
            _next = new GlyphButton(context, NextGlyph, 34, () => _ = Control(sn => sn.TrySkipNextAsync().AsTask()));

            var controls = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(-6 * s, 8 * s, 0, 0),
                Children = { _previous, Spacer(6 * s), _playPause, Spacer(6 * s), _next },
            };

            _progress = new BarView { Height = 4 * s, Margin = new Thickness(0, 10 * s, 0, 0) };

            var text = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                Children = { _title, _artist, controls, _progress },
            };

            var layout = new Grid();
            layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            layout.ColumnDefinitions.Add(new ColumnDefinition());
            Grid.SetColumn(text, 1);
            layout.Children.Add(_art);
            layout.Children.Add(text);

            Content = WidgetChrome.Card(context, layout);

            // Artwork is a square as tall as the card allows; hidden when the
            // widget is squeezed down to a single row.
            SizeChanged += (_, _) =>
            {
                var side = Math.Max(0, Math.Min(ActualHeight - 34 * s, ActualWidth * 0.4));
                _art.Width = side;
                _art.Height = side;
                _art.Visibility = side >= 56 * s ? Visibility.Visible : Visibility.Collapsed;
                _progress.Visibility = ActualHeight >= 150 * s ? Visibility.Visible : Visibility.Collapsed;
            };

            _tick = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
            _tick.Tick += (_, _) => UpdateProgress();

            Loaded += async (_, _) => await ConnectAsync().ConfigureAwait(true);
            Unloaded += (_, _) => Disconnect();
        }

        private static FrameworkElement Spacer(double width) => new Border { Width = width };

        private async Task ConnectAsync()
        {
            try
            {
                _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
                _manager.CurrentSessionChanged += OnCurrentSessionChanged;
                Attach(_manager.GetCurrentSession());
                _tick.Start();
            }
            catch (Exception ex)
            {
                Log.Error("media widget connect", ex);
                _artist.Text = "Media controls unavailable";
            }
        }

        private void Disconnect()
        {
            _tick.Stop();
            Attach(null);
            if (_manager is not null) _manager.CurrentSessionChanged -= OnCurrentSessionChanged;
            _manager = null;
        }

        // WinRT raises these on background threads.
        private void OnCurrentSessionChanged(GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs args) =>
            Dispatcher.InvokeAsync(() => Attach(sender.GetCurrentSession()));

        private void OnSessionChanged(GlobalSystemMediaTransportControlsSession sender, object args) =>
            Dispatcher.InvokeAsync(async () => await RefreshAsync().ConfigureAwait(true));

        private void Attach(GlobalSystemMediaTransportControlsSession? session)
        {
            if (_session is not null)
            {
                _session.MediaPropertiesChanged -= OnSessionChanged;
                _session.PlaybackInfoChanged -= OnSessionChanged;
            }

            _session = session;

            if (_session is not null)
            {
                _session.MediaPropertiesChanged += OnSessionChanged;
                _session.PlaybackInfoChanged += OnSessionChanged;
            }

            _ = RefreshAsync();
        }

        private async Task RefreshAsync()
        {
            var generation = ++_refreshGeneration;
            var session = _session;

            if (session is null)
            {
                ShowIdle();
                return;
            }

            try
            {
                var props = await session.TryGetMediaPropertiesAsync();
                if (generation != _refreshGeneration) return; // a newer refresh won

                _title.Text = string.IsNullOrWhiteSpace(props.Title) ? "Unknown title" : props.Title;
                _artist.Text = string.IsNullOrWhiteSpace(props.Artist) ? FriendlySource(session.SourceAppUserModelId) : props.Artist;

                var playback = session.GetPlaybackInfo();
                var playing = playback.PlaybackStatus == PlaybackStatus.Playing;
                _playPause.GlyphText = playing ? PauseGlyph : PlayGlyph;
                _previous.IsEnabled = playback.Controls.IsPreviousEnabled;
                _next.IsEnabled = playback.Controls.IsNextEnabled;
                _previous.Opacity = _previous.IsEnabled ? 1 : 0.35;
                _next.Opacity = _next.IsEnabled ? 1 : 0.35;

                _artImage.Source = props.Thumbnail is null ? null : await LoadArtAsync(props.Thumbnail);
                if (generation != _refreshGeneration) return;
                _artPlaceholder.Visibility = _artImage.Source is null ? Visibility.Visible : Visibility.Collapsed;

                UpdateProgress();
            }
            catch (Exception ex)
            {
                // Sessions vanish mid-call when an app closes; not worth more
                // than a log line and an idle card.
                Log.Write($"media widget refresh failed: {ex.Message}");
                ShowIdle();
            }
        }

        private void ShowIdle()
        {
            _title.Text = "Nothing playing";
            _artist.Text = "Play something to see it here";
            _playPause.GlyphText = PlayGlyph;
            _artImage.Source = null;
            _artPlaceholder.Visibility = Visibility.Visible;
            _progress.Value = 0;
        }

        private static async Task<ImageSource?> LoadArtAsync(Windows.Storage.Streams.IRandomAccessStreamReference reference)
        {
            try
            {
                using var winrt = await reference.OpenReadAsync();
                using var source = winrt.AsStreamForRead();
                var memory = new MemoryStream();
                await source.CopyToAsync(memory).ConfigureAwait(true);
                memory.Position = 0;

                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.DecodePixelWidth = 256;
                image.StreamSource = memory;
                image.EndInit();
                image.Freeze();
                return image;
            }
            catch (Exception ex)
            {
                Log.Write($"media artwork failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Interpolates between the timeline updates apps send, which can be
        /// several seconds apart, so the bar moves smoothly.
        /// </summary>
        private void UpdateProgress()
        {
            if (_session is null) return;
            try
            {
                var timeline = _session.GetTimelineProperties();
                var length = (timeline.EndTime - timeline.StartTime).TotalSeconds;
                if (length <= 0)
                {
                    _progress.Value = 0;
                    return;
                }

                var position = timeline.Position.TotalSeconds;
                if (_session.GetPlaybackInfo().PlaybackStatus == PlaybackStatus.Playing)
                    position += (DateTimeOffset.Now - timeline.LastUpdatedTime).TotalSeconds;

                _progress.Value = Math.Clamp(position / length, 0, 1);
            }
            catch (Exception)
            {
                _progress.Value = 0;
            }
        }

        private async Task Control(Func<GlobalSystemMediaTransportControlsSession, Task<bool>> action)
        {
            if (_session is null) return;
            try { await action(_session).ConfigureAwait(true); }
            catch (Exception ex) { Log.Write($"media control failed: {ex.Message}"); }
        }

        /// <summary>"SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify" → "Spotify".</summary>
        private static string FriendlySource(string aumid)
        {
            if (string.IsNullOrEmpty(aumid)) return string.Empty;
            var name = aumid.Contains('!') ? aumid[(aumid.LastIndexOf('!') + 1)..] : aumid;
            if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
            return name.Length == 0 ? aumid : char.ToUpperInvariant(name[0]) + name[1..];
        }
    }
}
