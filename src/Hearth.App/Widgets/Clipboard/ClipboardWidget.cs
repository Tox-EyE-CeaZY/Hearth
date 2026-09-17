using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Hearth.App.Widgets.Clipboard;

/// <summary>
/// Recently copied text and pinned snippets; click one to copy it again.
/// Colour codes get a swatch. See <see cref="ClipboardHistory"/> for what's
/// kept and for how long.
/// </summary>
public sealed partial class ClipboardWidget : IWidget
{
    public string Id => "clipboard";
    public string Title => "Clipboard";
    public (int Columns, int Rows) DefaultSpan => (3, 3);
    public (int Columns, int Rows) MinimumSpan => (2, 2);
    public double BoardHeight(bool wide) => 240;
    public int Order => 140;

    public void StopServices() => ClipboardHistory.Stop();

    public FrameworkElement CreateView(WidgetContext context) => new ClipboardView(context);

    [GeneratedRegex(@"^#(?:[0-9a-fA-F]{3}|[0-9a-fA-F]{6}|[0-9a-fA-F]{8})$")]
    private static partial Regex HexColour();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    private sealed class ClipboardView : WidgetView
    {
        private readonly WidgetHeader _header;
        private readonly StackPanel _rows = new();
        private readonly FrameworkElement _empty;
        private readonly Brush _secondary = WidgetChrome.Secondary;

        public ClipboardView(WidgetContext context) : base(context)
        {
            _header = new WidgetHeader(context, "\uE77F", "Clipboard");
            _header.AddAction("\uE74D", "Clear history (pins stay)", ClipboardHistory.ClearRecent);

            _empty = WidgetLayout.Empty(context, "\uE8C8", "Copy some text and it shows up here");

            var layout = new DockPanel();
            DockPanel.SetDock(_header, Dock.Top);
            layout.Children.Add(_header);
            layout.Children.Add(new Grid { Children = { WidgetLayout.Scroller(_rows), _empty } });
            SetBody(layout);

            While(() =>
            {
                ClipboardHistory.Start();
                ClipboardHistory.Changed += OnChanged;
            }, () => ClipboardHistory.Changed -= OnChanged);
            Every(TimeSpan.FromMinutes(1), Rebuild);
        }

        protected override void OnShown() => Rebuild();

        private void OnChanged() => Post(Rebuild);

        private void Rebuild()
        {
            var pinned = ClipboardHistory.Pinned;
            var recent = ClipboardHistory.RecentEntries;
            _header.Detail = recent.Count > 0 ? $"{recent.Count} recent" : string.Empty;
            _empty.Visibility = pinned.Count + recent.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            _rows.Children.Clear();
            foreach (var entry in pinned) _rows.Children.Add(Row(entry, isPinned: true));
            foreach (var entry in recent) _rows.Children.Add(Row(entry, isPinned: false));
        }

        private FrameworkElement Row(ClipEntry entry, bool isPinned)
        {
            var text = entry.Text;
            var line = Whitespace().Replace(text, " ").Trim();

            FrameworkElement lead;
            if (HexColour().IsMatch(line) && ColorConverter.ConvertFromString(ToArgb(line)) is Color colour)
            {
                lead = new Border
                {
                    Width = 14 * S,
                    Height = 14 * S,
                    CornerRadius = new CornerRadius(4 * S),
                    Background = WidgetChrome.Frozen(new SolidColorBrush(colour)),
                    BorderBrush = WidgetChrome.CardEdge,
                    BorderThickness = new Thickness(1),
                };
            }
            else
            {
                lead = WidgetChrome.Glyph(Context, isPinned ? "\uE840" : "\uE8C8", 12, isPinned ? WidgetChrome.Accent : WidgetChrome.Faint);
            }
            lead.Margin = new Thickness(0, 0, 10 * S, 0);
            lead.VerticalAlignment = VerticalAlignment.Center;

            var label = WidgetChrome.Text(Context, line, 12.5);
            label.VerticalAlignment = VerticalAlignment.Center;
            var when = WidgetChrome.Text(Context, isPinned ? string.Empty : WidgetFormat.Ago(entry.CopiedUtc), 10.5, _secondary);
            when.VerticalAlignment = VerticalAlignment.Center;
            when.Margin = new Thickness(8 * S, 0, 0, 0);

            var pin = new GlyphButton(Context, isPinned ? "\uE77A" : "\uE718", 22, () =>
            {
                if (isPinned) ClipboardHistory.Unpin(text);
                else ClipboardHistory.Pin(text);
            }) { ToolTip = isPinned ? "Unpin" : "Pin" };
            var remove = new GlyphButton(Context, "\uE711", 22, () => ClipboardHistory.Remove(text)) { ToolTip = "Remove" };
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4 * S, 0, -4 * S, 0), Children = { pin, remove } };

            // The time and the buttons share the right-hand end; hovering swaps them.
            var end = new Grid { Children = { when, actions } };
            actions.HorizontalAlignment = HorizontalAlignment.Right;
            when.HorizontalAlignment = HorizontalAlignment.Right;

            var dock = new DockPanel();
            DockPanel.SetDock(lead, Dock.Left);
            DockPanel.SetDock(end, Dock.Right);
            dock.Children.Add(lead);
            dock.Children.Add(end);
            dock.Children.Add(label);

            var row = new Pressable(() =>
            {
                ClipboardHistory.Copy(text);
                _header.Detail = "Copied";
            })
            {
                CornerRadius = new CornerRadius(8 * S),
                Padding = new Thickness(6 * S, 4 * S, 6 * S, 4 * S),
                Margin = new Thickness(-6 * S, 0, -6 * S, 0),
                MinHeight = 30 * S,
                Child = dock,
                ToolTip = (text.Length > 400 ? text[..400] + "…" : text) + "\n\nClick to copy",
            };
            row.ContextRequested = () => WidgetMenu.Show(row, menu =>
            {
                menu.Items.Add(WidgetMenu.Item("Copy", () => ClipboardHistory.Copy(text)));
                menu.Items.Add(WidgetMenu.Item(isPinned ? "Unpin" : "Pin", () =>
                {
                    if (isPinned) ClipboardHistory.Unpin(text);
                    else ClipboardHistory.Pin(text);
                }));
                menu.Items.Add(WidgetMenu.Item("Remove", () => ClipboardHistory.Remove(text)));
            });

            WidgetLayout.RevealOnHover(row, actions);
            row.MouseEnter += (_, _) => when.Visibility = Visibility.Hidden;
            row.MouseLeave += (_, _) => when.Visibility = Visibility.Visible;
            return row;
        }

        /// <summary>CSS order (#RGBA / #RRGGBBAA) to WPF's (#AARRGGBB).</summary>
        private static string ToArgb(string hex) => hex.Length switch
        {
            4 => $"#{hex[1]}{hex[1]}{hex[2]}{hex[2]}{hex[3]}{hex[3]}",
            9 => $"#{hex[7..9]}{hex[1..7]}",
            _ => hex,
        };
    }
}
