using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Hearth.Core.Shell;

namespace Hearth.Core.Icons;

/// <summary>
/// Turns launcher items into finished tiles, with a two-level cache.
///
/// Extraction and rendering never touch the UI thread. They also never touch
/// the thread pool: DrawingVisual and RenderTargetBitmap are DispatcherObjects
/// whose constructors create a Dispatcher for whatever thread they land on, so
/// pooling them would quietly leak one Dispatcher per worker. Instead all
/// rendering is funnelled through a single long-lived render thread.
/// </summary>
public sealed class IconService : IDisposable
{
    /// <summary>Bump to invalidate every cached tile after a pipeline change.</summary>
    private const int CacheVersion = 5; // 2: bottom-up DIB fix, 3: padded small-icon trim, 4: plate zoom, 5: icon source

    private readonly string _diskCacheDirectory;
    private readonly ConcurrentDictionary<string, BitmapSource> _memoryCache = new();
    private readonly ConcurrentDictionary<string, Task<BitmapSource?>> _inFlight = new();

    private Thread? _renderThread;
    private Dispatcher? _renderDispatcher;
    private readonly ManualResetEventSlim _renderReady = new(false);
    private volatile bool _disposed;

    public IconService(string? diskCacheDirectory = null)
    {
        _diskCacheDirectory = diskCacheDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Hearth", "icons");

        Directory.CreateDirectory(_diskCacheDirectory);
        StartRenderThread();
    }

    private void StartRenderThread()
    {
        _renderThread = new Thread(() =>
        {
            _renderDispatcher = Dispatcher.CurrentDispatcher;
            _renderReady.Set();
            Dispatcher.Run();
        })
        {
            Name = "Hearth.IconRender",
            IsBackground = true,
        };

        // STA because the shell COM objects we bind to are apartment-threaded.
        _renderThread.SetApartmentState(ApartmentState.STA);
        _renderThread.Start();
        _renderReady.Wait();
    }

    /// <summary>
    /// Returns a frozen, ready-to-bind tile. Never throws for a bad item — a
    /// missing icon returns null and the caller shows its placeholder.
    /// </summary>
    public Task<BitmapSource?> GetAsync(LauncherItem item, IconRenderOptions options)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var key = BuildKey(item, options);

        if (_memoryCache.TryGetValue(key, out var cached))
            return Task.FromResult<BitmapSource?>(cached);

        // Collapse duplicate requests: the grid asks for the same icon from
        // several tiles at once during a relayout.
        return _inFlight.GetOrAdd(key, _ => ProduceAsync(item, options, key));
    }

    private async Task<BitmapSource?> ProduceAsync(LauncherItem item, IconRenderOptions options, string key)
    {
        try
        {
            var diskPath = Path.Combine(_diskCacheDirectory, key + ".png");

            if (File.Exists(diskPath))
            {
                var fromDisk = await Task.Run(() => LoadPng(diskPath)).ConfigureAwait(false);
                if (fromDisk is not null)
                {
                    _memoryCache[key] = fromDisk;
                    return fromDisk;
                }
                // Corrupt entry: fall through and regenerate over the top.
            }

            var rendered = await RenderOnDedicatedThreadAsync(item, options).ConfigureAwait(false);
            if (rendered is null)
            {
                // The shell's image factory does fail transiently, and it does
                // it most when every icon is being extracted at once — which is
                // exactly what "rebuild every icon" asks for, and why a few
                // tiles came back blank after one. A failure is not cached, so
                // one more try a moment later is all it takes.
                await Task.Delay(180).ConfigureAwait(false);
                rendered = await RenderOnDedicatedThreadAsync(item, options).ConfigureAwait(false);
            }
            if (rendered is null)
            {
                Debug.WriteLine($"[Hearth] no icon for '{item.DisplayName}' after a retry");
                return null;
            }

            _memoryCache[key] = rendered;
            _ = Task.Run(() => SavePng(rendered, diskPath));
            return rendered;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Hearth] icon pipeline failed for '{item.DisplayName}': {ex.Message}");
            return null;
        }
        finally
        {
            _inFlight.TryRemove(key, out _);
        }
    }

    private Task<BitmapSource?> RenderOnDedicatedThreadAsync(LauncherItem item, IconRenderOptions options)
    {
        var dispatcher = _renderDispatcher;
        if (dispatcher is null) return Task.FromResult<BitmapSource?>(null);

        return dispatcher.InvokeAsync(() =>
        {
            var parsingName = item.Kind == LauncherItemKind.App
                ? $@"shell:AppsFolder\{item.Target}"
                : item.Target ?? item.Id;

            // Steam's own library artwork, composed here because this is the
            // one thread in the process that may touch WPF imaging.
            // "Original" skips both Steam artwork and overrides and goes
            // straight to the icon the shell reports for the item.
            string? picked = null;
            if (options.Source != IconSourceMode.Original)
            {
                var steam = item.SteamAppId is { Length: > 0 } appId ? SteamArtwork.IconFor(appId) : null;
                picked = item.IconOverridePath is { Length: > 0 } chosen && File.Exists(chosen) ? chosen : steam;
            }

            var source = picked is not null
                ? LoadRawFromFile(picked)
                : IconExtractor.Extract(parsingName);

            if (source is null) return (BitmapSource?)null;

            // Artwork smaller than the tile is enlarged before anything is
            // measured from it, so the analysis and the mask both work on the
            // pixels that will actually be drawn.
            if (options.Sharpen)
            {
                var wanted = (int)Math.Ceiling(options.Size * options.Scale);
                source = IconUpscaler.Enlarge(source, wanted);
            }

            var analysis = IconAnalysis.Analyse(source);
            return AdaptiveIconRenderer.Render(source, analysis, options);
        }, DispatcherPriority.Background).Task;
    }

    /// <summary>Loads an icon-pack PNG into the same straight-BGRA form as a shell extraction.</summary>
    private static RawIcon? LoadRawFromFile(string path)
    {
        try
        {
            var decoded = LoadPng(path);
            if (decoded is null) return null;

            var converted = new FormatConvertedBitmap(decoded, System.Windows.Media.PixelFormats.Bgra32, null, 0);
            converted.Freeze();

            var stride = converted.PixelWidth * 4;
            var pixels = new byte[stride * converted.PixelHeight];
            converted.CopyPixels(pixels, stride, 0);

            return new RawIcon
            {
                Width = converted.PixelWidth,
                Height = converted.PixelHeight,
                Pixels = pixels,
            };
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Hearth] could not load icon override '{path}': {ex.Message}");
            return null;
        }
    }

    private static BitmapSource? LoadPng(string path)
    {
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad; // release the file handle
            bitmap.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
            bitmap.UriSource = new Uri(path);
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UriFormatException)
        {
            return null;
        }
    }

    private static void SavePng(BitmapSource bitmap, string path)
    {
        try
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));

            // Write to a temp name then move, so a crash mid-write cannot leave
            // a truncated PNG that we would happily load next launch.
            var temp = path + ".tmp";
            using (var stream = File.Create(temp)) encoder.Save(stream);
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"[Hearth] icon cache write failed: {ex.Message}");
        }
    }

    private static string BuildKey(LauncherItem item, IconRenderOptions options)
    {
        // Size is not in the key: tiles are rendered once at the configured
        // size and the cache is dropped when that setting changes.
        var fingerprint = string.Join('|',
            CacheVersion,
            item.Id,
            item.IconOverridePath ?? item.SteamAppId ?? string.Empty,
            options.Sharpen ? "s" : "-",
            options.Shape,
            options.Size.ToString("F0"),
            options.Scale.ToString("F2"),
            options.BakeShadow,
            options.Fit,
            options.Source,
            options.ForceBackground,
            options.DarkTheme);

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(fingerprint));
        return Convert.ToHexString(hash, 0, 16).ToLowerInvariant();
    }

    /// <summary>Drops in-memory tiles. Disk entries survive, keyed by content.</summary>
    public void ClearMemoryCache() => _memoryCache.Clear();

    /// <summary>Wipes the on-disk cache — the fix for "my icons look stale".</summary>
    public void ClearDiskCache()
    {
        _memoryCache.Clear();
        // Requests already under way would otherwise be handed back from
        // _inFlight and re-save the very entries being deleted.
        _inFlight.Clear();

        // Steam's composed icons are cached separately; without this a change
        // to how they are made would never show up.
        SteamArtwork.Clear();
        try
        {
            foreach (var file in Directory.EnumerateFiles(_diskCacheDirectory, "*.png"))
                File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"[Hearth] icon cache clear failed: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _renderDispatcher?.InvokeShutdown();
        _renderReady.Dispose();
        _memoryCache.Clear();
    }
}
