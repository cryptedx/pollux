using Pollux.Core.Navigation;

namespace Pollux.Core.Files;

/// <summary>
/// Identifies one shell thumbnail: a changed size or date makes a new key, so stale images are never shown.
/// <see cref="IconOnly"/> asks the shell for the file type icon instead of the content: reading the content of a
/// cloud-only placeholder would download it, and of a network file can hang the thumbnail thread.
/// </summary>
public readonly record struct ThumbnailKey(string Path, DateTime Modified, long Size, int Pixels, bool IconOnly)
{
    /// <param name="entry">The file.</param>
    /// <param name="pixels">The thumbnail size.</param>
    /// <param name="onNetworkDrive">The file lies on a mapped network drive (Z:\ looks like a local path, so the caller says it).</param>
    public static ThumbnailKey For(FileEntry entry, int pixels, bool onNetworkDrive = false) =>
        new(entry.FullPath, entry.Modified, entry.Size, pixels, onNetworkDrive || entry.Cloud == CloudState.CloudOnly || WinPath.IsUnc(entry.FullPath));
}

/// <summary>
/// Least-recently-used cache of thumbnails. A null value records a failed extraction, so it is not retried on every scroll.
/// Not thread-safe: the App uses it from the UI thread only.
/// </summary>
public sealed class ThumbnailCache<T>(int capacity) where T : class
{
    private readonly Dictionary<ThumbnailKey, LinkedListNode<(ThumbnailKey Key, T? Value)>> _map = [];
    private readonly LinkedList<(ThumbnailKey Key, T? Value)> _order = new();

    public int Count => _map.Count;

    public bool TryGet(ThumbnailKey key, out T? value)
    {
        if (!_map.TryGetValue(key, out var node))
        {
            value = null;
            return false;
        }

        _order.Remove(node);
        _order.AddFirst(node);
        value = node.Value.Value;
        return true;
    }

    public void Add(ThumbnailKey key, T? value)
    {
        if (_map.Remove(key, out var old)) _order.Remove(old);
        _map[key] = _order.AddFirst((key, value));
        while (_map.Count > Math.Max(1, capacity))
        {
            var last = _order.Last!;
            _order.RemoveLast();
            _map.Remove(last.Value.Key);
        }
    }
}

/// <summary>
/// Pending thumbnail requests, newest first: the entries just scrolled into view load before those scrolled past.
/// A request pushed again moves to the front; a full queue drops its oldest request. Thread-safe.
/// </summary>
public sealed class ThumbnailRequests<T>(int capacity) where T : class
{
    private readonly LinkedList<T> _items = new();
    private readonly object _gate = new();

    public int Count
    {
        get { lock (_gate) return _items.Count; }
    }

    /// <summary>Queues a request; returns the request dropped to make room, if any.</summary>
    public T? Push(T item)
    {
        lock (_gate)
        {
            _items.Remove(item);
            _items.AddFirst(item);
            T? dropped = null;
            if (_items.Count > Math.Max(1, capacity))
            {
                dropped = _items.Last!.Value;
                _items.RemoveLast();
            }

            Monitor.PulseAll(_gate);
            return dropped;
        }
    }

    public bool TryTake(out T? item)
    {
        lock (_gate) return TakeLocked(out item);
    }

    /// <summary>Blocks until a request is queued; null once <paramref name="ct"/> is cancelled.</summary>
    public T? Take(CancellationToken ct)
    {
        using var wake = ct.Register(() => { lock (_gate) Monitor.PulseAll(_gate); });
        lock (_gate)
        {
            while (true)
            {
                if (ct.IsCancellationRequested) return null;
                if (TakeLocked(out var item)) return item;
                Monitor.Wait(_gate);
            }
        }
    }

    private bool TakeLocked(out T? item)
    {
        if (_items.First is not { } first)
        {
            item = null;
            return false;
        }

        _items.RemoveFirst();
        item = first.Value;
        return true;
    }
}

public static class ThumbnailPixels
{
    /// <summary>
    /// Shell bitmaps of photos often come without alpha (every alpha byte 0), which would draw them invisible.
    /// Such a BGRA buffer is made fully opaque; one with any alpha is left as it is.
    /// </summary>
    public static void MakeOpaqueIfNoAlpha(Span<byte> bgra)
    {
        for (var i = 3; i < bgra.Length; i += 4)
        {
            if (bgra[i] != 0) return;
        }

        for (var i = 3; i < bgra.Length; i += 4) bgra[i] = 255;
    }
}
