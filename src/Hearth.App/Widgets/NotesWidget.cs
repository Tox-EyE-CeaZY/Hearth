using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Hearth.App.Hosting;
using Hearth.Core.Diagnostics;

namespace Hearth.App.Widgets;

/// <summary>
/// A sticky note on the desktop.
///
/// Hearth's window never takes keyboard focus, so typing needs it to opt in
/// for the duration of an edit (DesktopHost.BeginKeyboardInput) and opt back
/// out afterwards. Text is saved shortly after you stop typing and again when
/// you click away, to %AppData%\Hearth\notes.txt — a plain file you can open
/// anywhere.
/// </summary>
public sealed class NotesWidget : IWidget
{
    public string Id => "notes";
    public string Title => "Notes";
    public (int Columns, int Rows) DefaultSpan => (3, 3);
    public (int Columns, int Rows) MinimumSpan => (2, 2);

    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Hearth", "notes.txt");

    public FrameworkElement CreateView(WidgetContext context) => new NotesView(context);

    private sealed class NotesView : ContentControl
    {
        private readonly TextBox _editor;
        private readonly TextBlock _placeholder;
        private readonly DispatcherTimer _saveDelay;
        private bool _editing;
        private bool _dirty;

        public NotesView(WidgetContext context)
        {
            var s = context.Scale;

            var header = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 0, 0, 8 * s),
                Children =
                {
                    WidgetChrome.Glyph(context, "\uE70B", 14, WidgetChrome.Accent),
                    new Border { Width = 8 * s },
                    WidgetChrome.Text(context, "Notes", 14, weight: FontWeights.SemiBold, display: true),
                },
            };

            _editor = new TextBox
            {
                Text = Load(),
                AcceptsReturn = true,
                AcceptsTab = true,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Foreground = WidgetChrome.Primary,
                CaretBrush = WidgetChrome.Primary,
                SelectionBrush = WidgetChrome.Accent,
                FontFamily = WidgetChrome.Body,
                FontSize = 14 * s,
                Padding = new Thickness(0),
                Cursor = Cursors.IBeam,
            };

            _placeholder = WidgetChrome.Text(context, "Click to write something…", 14, WidgetChrome.Faint);
            _placeholder.IsHitTestVisible = false;
            _placeholder.VerticalAlignment = VerticalAlignment.Top;

            var body = new Grid { Children = { _editor, _placeholder } };

            var layout = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(header, Dock.Top);
            layout.Children.Add(header);
            layout.Children.Add(body);

            Content = WidgetChrome.Card(context, layout);

            _saveDelay = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(700) };
            _saveDelay.Tick += (_, _) => { _saveDelay.Stop(); Save(); };

            _editor.PreviewMouseLeftButtonDown += (_, _) => BeginEditing();
            _editor.TextChanged += (_, _) =>
            {
                _dirty = true;
                UpdatePlaceholder();
                _saveDelay.Stop();
                _saveDelay.Start();
            };
            _editor.LostKeyboardFocus += (_, _) => EndEditing();
            _editor.PreviewKeyDown += (_, e) =>
            {
                if (e.Key != Key.Escape) return;
                Keyboard.ClearFocus();
                e.Handled = true;
            };

            UpdatePlaceholder();
            Unloaded += (_, _) => { EndEditing(); Save(); };
        }

        private void BeginEditing()
        {
            if (_editing) return;
            _editing = true;
            DesktopHost.Current?.BeginKeyboardInput();
            _placeholder.Visibility = Visibility.Collapsed;
            Dispatcher.BeginInvoke(() => Keyboard.Focus(_editor), DispatcherPriority.Input);
        }

        private void EndEditing()
        {
            if (!_editing) return;
            _editing = false;
            Save();
            UpdatePlaceholder();
            DesktopHost.Current?.EndKeyboardInput();
        }

        private void UpdatePlaceholder() =>
            _placeholder.Visibility = _editing || _editor.Text.Length > 0 ? Visibility.Collapsed : Visibility.Visible;

        private static string Load()
        {
            try
            {
                return File.Exists(FilePath) ? File.ReadAllText(FilePath, Encoding.UTF8) : string.Empty;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Write($"notes load failed: {ex.Message}");
                return string.Empty;
            }
        }

        private void Save()
        {
            if (!_dirty) return;
            try
            {
                var directory = Path.GetDirectoryName(FilePath);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

                // Temp-then-move, so a crash mid-write never truncates the note.
                var temp = FilePath + ".tmp";
                File.WriteAllText(temp, _editor.Text, Encoding.UTF8);
                File.Move(temp, FilePath, overwrite: true);
                _dirty = false;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Write($"notes save failed: {ex.Message}");
            }
        }
    }
}
