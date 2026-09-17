using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Hearth.Core.Diagnostics;

namespace Hearth.App.Widgets.Clipboard;

internal sealed class ClipEntry
{
    public string Text { get; set; } = string.Empty;
    public DateTime CopiedUtc { get; set; } = DateTime.UtcNow;
}

internal sealed class ClipPins
{
    public List<ClipEntry> Pins { get; set; } = [];
}

/// <summary>
/// Recently copied text, gathered while any Clipboard widget has been shown.
///
/// Recent entries live in memory only and are gone when Hearth exits; pinned
/// ones are saved to clipboard-pins.json. Anything an app marks as private
/// (password managers do) is skipped, the same way Windows' own clipboard
/// history skips it. UI thread only.
/// </summary>
internal static class ClipboardHistory
{
    private const int Limit = 30;
    private const int MaxLength = 10_000;

    // Formats apps set to keep content out of clipboard history and viewers.
    private const string ExcludeFormat = "ExcludeClipboardContentFromMonitorProcessing";
    private const string HistoryFormat = "CanIncludeInClipboardHistory";
    private const string ViewerIgnoreFormat = "Clipboard Viewer Ignore";

    private static readonly WidgetStore<ClipPins> Store = new("clipboard-pins.json");
    private static readonly List<ClipEntry> Recent = [];
    private static ClipPins? _pins;
    private static HwndSource? _listener;

    public static event Action? Changed;

    private static ClipPins PinData => _pins ??= Store.Load();

    public static IReadOnlyList<ClipEntry> Pinned => PinData.Pins;
    public static IReadOnlyList<ClipEntry> RecentEntries => Recent;

    public static bool IsPinned(string text) => PinData.Pins.Exists(p => p.Text == text);

    /// <summary>Starts listening, if it isn't already. Called when a Clipboard widget is shown.</summary>
    public static void Start()
    {
        if (_listener is not null) return;

        // A message-only window: it receives WM_CLIPBOARDUPDATE and nothing else.
        _listener = new HwndSource(new HwndSourceParameters("Hearth.Clipboard")
        {
            ParentWindow = new IntPtr(-3), // HWND_MESSAGE
            Width = 0,
            Height = 0,
            WindowStyle = 0,
        });
        _listener.AddHook(WndProc);
        if (!AddClipboardFormatListener(_listener.Handle))
        {
            Log.Write($"clipboard: listener refused ({Marshal.GetLastWin32Error()})");
        }
        Capture();
    }

    public static void Stop()
    {
        if (_listener is null) return;
        RemoveClipboardFormatListener(_listener.Handle);
        _listener.RemoveHook(WndProc);
        _listener.Dispose();
        _listener = null;
    }

    public static void Copy(string text)
    {
        if (!WidgetFiles.SetClipboardText(text)) return;
        Remember(text);
    }

    public static void Pin(string text)
    {
        if (IsPinned(text)) return;
        PinData.Pins.Insert(0, new ClipEntry { Text = text });
        Recent.RemoveAll(e => e.Text == text);
        Commit(savePins: true);
    }

    public static void Unpin(string text)
    {
        PinData.Pins.RemoveAll(p => p.Text == text);
        Remember(text);
        Commit(savePins: true);
    }

    public static void Remove(string text)
    {
        var pinned = PinData.Pins.RemoveAll(p => p.Text == text) > 0;
        Recent.RemoveAll(e => e.Text == text);
        Commit(savePins: pinned);
    }

    public static void ClearRecent()
    {
        Recent.Clear();
        Commit(savePins: false);
    }

    private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_CLIPBOARDUPDATE)
        {
            // The app that copied may still be holding the clipboard open.
            Dispatcher.CurrentDispatcher.BeginInvoke(Capture, DispatcherPriority.Background);
            handled = true;
        }
        return IntPtr.Zero;
    }

    private static void Capture()
    {
        try
        {
            var data = System.Windows.Clipboard.GetDataObject();
            if (data is null || IsPrivate(data) || !data.GetDataPresent(DataFormats.UnicodeText)) return;
            if (data.GetData(DataFormats.UnicodeText) is not string text || string.IsNullOrWhiteSpace(text)) return;
            if (text.Length > MaxLength) text = text[..MaxLength];
            if (IsPinned(text)) return;

            Remember(text);
            Commit(savePins: false);
        }
        catch (Exception ex) when (ex is COMException or ExternalException or OutOfMemoryException)
        {
            Log.Write($"clipboard: couldn't read: {ex.Message}");
        }
    }

    private static bool IsPrivate(IDataObject data)
    {
        if (data.GetDataPresent(ExcludeFormat) || data.GetDataPresent(ViewerIgnoreFormat)) return true;
        if (!data.GetDataPresent(HistoryFormat)) return false;
        return data.GetData(HistoryFormat) is System.IO.MemoryStream stream &&
               stream.Length >= 4 && BitConverter.ToInt32(stream.ToArray(), 0) == 0;
    }

    private static void Remember(string text)
    {
        if (IsPinned(text)) return;
        Recent.RemoveAll(e => e.Text == text);
        Recent.Insert(0, new ClipEntry { Text = text });
        if (Recent.Count > Limit) Recent.RemoveRange(Limit, Recent.Count - Limit);
    }

    private const int WM_CLIPBOARDUPDATE = 0x031D;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    private static void Commit(bool savePins)
    {
        if (savePins) Store.Save(PinData);
        Changed?.Invoke();
    }
}
