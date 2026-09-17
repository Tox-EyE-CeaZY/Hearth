using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Hearth.Core.Diagnostics;

namespace Hearth.App.Widgets;

/// <summary>
/// A widget's saved data: one JSON file in %AppData%\Hearth, named by the
/// widget. Loading never throws (a missing or broken file gives a fresh
/// <typeparamref name="T"/>), and saving writes a temp file then moves it,
/// so a crash mid-write never loses what was there.
///
/// Keep <typeparamref name="T"/> a plain class with settable properties, and
/// only ever add properties: the file outlives every version of the widget.
/// </summary>
internal sealed class WidgetStore<T> where T : class, new()
{
    public static readonly string Folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Hearth");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public WidgetStore(string fileName) => FilePath = Path.Combine(Folder, fileName);

    public string FilePath { get; }

    public T Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new T();
            return JsonSerializer.Deserialize<T>(File.ReadAllText(FilePath), Options) ?? new T();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Log.Write($"widget store: couldn't read {FilePath}: {ex.Message}");
            return new T();
        }
    }

    public void Save(T value)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(value, Options));
            File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Write($"widget store: couldn't save {FilePath}: {ex.Message}");
        }
    }
}
