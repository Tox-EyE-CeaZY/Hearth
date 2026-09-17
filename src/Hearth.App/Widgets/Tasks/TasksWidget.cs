using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Hearth.App.Widgets.Tasks;

/// <summary>
/// A checklist: tick things off, add new ones inline, clear what's done.
/// Kept in %AppData%\Hearth\tasks.json; every Tasks widget shows the same list.
/// </summary>
public sealed class TasksWidget : IWidget
{
    public string Id => "tasks";
    public string Title => "Tasks";
    public (int Columns, int Rows) DefaultSpan => (3, 3);
    public (int Columns, int Rows) MinimumSpan => (2, 2);
    public double BoardHeight(bool wide) => 260;
    public int Order => 90;

    public FrameworkElement CreateView(WidgetContext context) => new TasksView(context);

    private sealed class TasksView : WidgetView
    {
        private const string Ring = "\uEA3A";
        private const string Ticked = "\uEC61";

        private readonly WidgetHeader _header;
        private readonly StackPanel _rows = new();
        private readonly Border _empty;

        public TasksView(WidgetContext context) : base(context)
        {
            _header = new WidgetHeader(context, "\uE71D", "Tasks");
            _header.AddAction("\uE74D", "Clear completed", TaskList.ClearDone);

            var input = new InlineInput(context, "Add a task");
            input.Committed += TaskList.Add;
            var add = new DockPanel
            {
                Margin = new Thickness(0, 6 * S, 0, 0),
                Children = { WidgetChrome.Glyph(context, "\uE710", 12, WidgetChrome.Secondary), input },
            };
            input.Margin = new Thickness(10 * S, 0, 0, 0);
            DockPanel.SetDock(add.Children[0], Dock.Left);

            _empty = new Border
            {
                Child = WidgetLayout.Empty(context, Ticked, "Nothing to do"),
                Visibility = Visibility.Collapsed,
            };

            var layout = new DockPanel();
            DockPanel.SetDock(_header, Dock.Top);
            DockPanel.SetDock(add, Dock.Bottom);
            layout.Children.Add(_header);
            layout.Children.Add(add);
            layout.Children.Add(new Grid { Children = { WidgetLayout.Scroller(_rows), _empty } });
            SetBody(layout);

            While(() => TaskList.Changed += OnChanged, () => TaskList.Changed -= OnChanged);
        }

        protected override void OnShown() => Rebuild();

        private void OnChanged() => Post(Rebuild);

        private void Rebuild()
        {
            var items = TaskList.Items;
            var left = items.Count(t => !t.Done);
            _header.Detail = items.Count == 0 ? string.Empty : left == 0 ? "all done" : $"{left} left";
            _empty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            _rows.Children.Clear();
            foreach (var task in items.Where(t => !t.Done).Concat(items.Where(t => t.Done)))
                _rows.Children.Add(Row(task));
        }

        private FrameworkElement Row(TaskItem task)
        {
            var check = WidgetChrome.Glyph(Context, task.Done ? Ticked : Ring, 15,
                task.Done ? WidgetChrome.Accent : WidgetChrome.Secondary);
            check.Margin = new Thickness(0, 0, 10 * S, 0);
            check.VerticalAlignment = VerticalAlignment.Top;

            var text = WidgetChrome.Text(Context, task.Text, 13, task.Done ? WidgetChrome.Faint : WidgetChrome.Primary);
            text.TextWrapping = TextWrapping.Wrap;
            text.TextTrimming = TextTrimming.None;
            if (task.Done) text.TextDecorations = TextDecorations.Strikethrough;

            var remove = new GlyphButton(Context, "\uE711", 22, () => TaskList.Remove(task.Id)) { ToolTip = "Delete" };
            remove.VerticalAlignment = VerticalAlignment.Top;
            remove.Margin = new Thickness(4 * S, -2 * S, -4 * S, 0);

            var dock = new DockPanel();
            DockPanel.SetDock(check, Dock.Left);
            DockPanel.SetDock(remove, Dock.Right);
            dock.Children.Add(check);
            dock.Children.Add(remove);
            dock.Children.Add(text);

            var row = new Pressable(() => TaskList.Toggle(task.Id))
            {
                CornerRadius = new CornerRadius(8 * S),
                Padding = new Thickness(6 * S, 5 * S, 6 * S, 5 * S),
                Margin = new Thickness(-6 * S, 0, -6 * S, 0),
                Child = dock,
                ToolTip = task.Done ? "Mark as not done" : "Mark as done",
            };
            row.ContextRequested = () => WidgetMenu.Show(row, menu =>
            {
                menu.Items.Add(WidgetMenu.Item(task.Done ? "Mark as not done" : "Mark as done", () => TaskList.Toggle(task.Id)));
                menu.Items.Add(WidgetMenu.Item("Copy text", () => WidgetFiles.SetClipboardText(task.Text)));
                menu.Items.Add(WidgetMenu.Item("Delete", () => TaskList.Remove(task.Id)));
            });
            WidgetLayout.RevealOnHover(row, remove);
            return row;
        }
    }
}
