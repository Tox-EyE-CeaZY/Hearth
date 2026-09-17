namespace Hearth.App.Widgets.Tasks;

internal sealed class TaskItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Text { get; set; } = string.Empty;
    public bool Done { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? DoneUtc { get; set; }
}

internal sealed class TaskListData
{
    public List<TaskItem> Items { get; set; } = [];
}

/// <summary>The one task list every Tasks widget shows. UI thread only.</summary>
internal static class TaskList
{
    private static readonly WidgetStore<TaskListData> Store = new("tasks.json");
    private static TaskListData? _data;

    public static event Action? Changed;

    private static TaskListData Data => _data ??= Store.Load();

    public static IReadOnlyList<TaskItem> Items => Data.Items;

    public static void Add(string text) => Update(d => d.Items.Add(new TaskItem { Text = text }));

    public static void Toggle(string id) => Update(d =>
    {
        if (d.Items.Find(t => t.Id == id) is not { } task) return;
        task.Done = !task.Done;
        task.DoneUtc = task.Done ? DateTime.UtcNow : null;
    });

    public static void Remove(string id) => Update(d => d.Items.RemoveAll(t => t.Id == id));

    public static void ClearDone() => Update(d => d.Items.RemoveAll(t => t.Done));

    private static void Update(Action<TaskListData> change)
    {
        change(Data);
        Store.Save(Data);
        Changed?.Invoke();
    }
}
