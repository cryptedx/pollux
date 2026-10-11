using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Pollux.App.Interop;
using Pollux.Core.Files;
using System.Runtime.InteropServices.WindowsRuntime;

namespace Pollux.App.Services;

/// <summary>
/// Shell thumbnails for the Tiles and Large Icons view modes. One background STA thread extracts them, newest request
/// first (what was just scrolled into view); the finished bitmaps are cached on the UI thread, which all Pollux
/// windows share. Only realized list containers ask, so a folder of 10,000 photos loads just the ones on screen.
/// </summary>
internal static class Thumbnails
{
    // UI thread only: the cache, and the callbacks waiting for a thumbnail being extracted.
    private static readonly ThumbnailCache<ImageSource> Cache = new(500);
    private static readonly Dictionary<ThumbnailKey, List<Action<ImageSource>>> Waiting = [];
    private static readonly ThumbnailRequests<Job> Requests = new(256);
    private static Thread? _worker;

    private sealed record Job(ThumbnailKey Key, DispatcherQueue Ui);

    /// <summary>
    /// UI thread. The cached thumbnail, or null: then it is queued (unless the shell had none last time) and
    /// <paramref name="loaded"/> runs on the UI thread once it is ready.
    /// </summary>
    public static ImageSource? Get(ThumbnailKey key, DispatcherQueue ui, Action<ImageSource> loaded)
    {
        if (Cache.TryGet(key, out var cached)) return cached;
        if (Waiting.TryGetValue(key, out var callbacks)) callbacks.Add(loaded);
        else Waiting[key] = [loaded];

        // Pushing a waiting key again moves it to the front; a request dropped from a full queue is asked for anew
        // when its entry is realized again.
        if (Requests.Push(new Job(key, ui)) is { } dropped) Waiting.Remove(dropped.Key);
        EnsureWorker();
        return null;
    }

    private static void EnsureWorker()
    {
        if (_worker is not null) return;
        _worker = new Thread(Run) { IsBackground = true, Name = "Pollux thumbnails", Priority = ThreadPriority.BelowNormal };
        // Shell thumbnail handlers are apartment-threaded COM objects.
        _worker.SetApartmentState(ApartmentState.STA);
        _worker.Start();
    }

    private static void Run()
    {
        while (Requests.Take(CancellationToken.None) is { } job)
        {
            ShellThumbnail.Pixels? pixels = null;
            try
            {
                pixels = ShellThumbnail.Extract(job.Key.Path, job.Key.Pixels, job.Key.IconOnly);
            }
            catch (Exception ex)
            {
                // The thread serves every Pane: one bad file must not stop it.
                CrashLog.Trace($"thumbnail {job.Key.Path} failed: {ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}");
            }

            var done = pixels;
            job.Ui.TryEnqueue(DispatcherQueuePriority.Low, () => Deliver(job.Key, done));
        }
    }

    private static void Deliver(ThumbnailKey key, ShellThumbnail.Pixels? pixels)
    {
        // A WriteableBitmap, not a SoftwareBitmapSource: the cached source is shown again by new Image elements when a
        // view mode is entered again, and reusing a SoftwareBitmapSource there crashes WinUI.
        ImageSource? source = null;
        if (pixels is not null)
        {
            try
            {
                var bitmap = new WriteableBitmap(pixels.Value.Width, pixels.Value.Height);
                using (var stream = bitmap.PixelBuffer.AsStream()) stream.Write(pixels.Value.Bgra);
                bitmap.Invalidate();
                source = bitmap;
            }
            catch (Exception ex)
            {
                CrashLog.Trace($"thumbnail {key.Path} not shown: {ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}");
            }
        }

        // A null entry remembers that the shell has no thumbnail, so scrolling does not ask again.
        Cache.Add(key, source);
        if (!Waiting.Remove(key, out var callbacks) || source is null) return;
        foreach (var callback in callbacks) callback(source);
    }
}
