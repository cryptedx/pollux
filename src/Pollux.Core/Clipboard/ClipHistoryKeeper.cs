using System.Security.Cryptography;

namespace Pollux.Core.Clipboard;

/// <summary>
/// Owns the lifecycle of the Clipboard History on disk: loading it (in the background if asked), pruning it by the
/// Retention, saving it when it changed, and the data-safety rules around that. A history that could not be read is
/// never overwritten: nothing is saved until the store becomes readable, then its Clips are merged in.
///
/// Threading: every member must be called from one thread (the UI thread). <see cref="FlushAsync"/> takes its snapshot
/// synchronously on the calling thread (before its first await, or after awaiting a running write with the caller's
/// context), so the snapshot always sees the history as the UI thread left it; only <see cref="ClipStore.Write"/> runs
/// on a background task, one write at a time. Changes made meanwhile stay dirty and are written by the next flush.
/// </summary>
public sealed class ClipHistoryKeeper(
    ClipStore store,
    Func<ClipRetention> retention,
    Action<string> info,
    Action<Exception> error,
    TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private ClipboardHistory? _history;
    private Task<ClipboardHistory>? _loading;
    private Task _writing = Task.CompletedTask;
    private volatile bool _dirty;
    private volatile bool _lastWriteFailed;

    /// <summary>Raised (on the thread of the flush or shutdown) after a store that could not be read became readable and its Clips were merged.</summary>
    public event Action? Merged;

    /// <summary>True when changes are waiting to be written.</summary>
    public bool IsDirty => _dirty;

    private DateTime Now => _time.GetLocalNow().DateTime;

    /// <summary>Starts loading the history on a background task (decrypting a large history takes a while); <see cref="History"/> waits for it.</summary>
    public void BeginLoad()
    {
        if (_history is null && _loading is null)
        {
            _loading = Task.Run(store.Load);
        }
    }

    /// <summary>The Clipboard History, loaded on first use (waits for a load started by <see cref="BeginLoad"/>) and pruned by the Retention.</summary>
    public ClipboardHistory History
    {
        get
        {
            if (_history is null)
            {
                _history = _loading is { } loading ? loading.GetAwaiter().GetResult() : store.Load();
                _loading = null;
                if (store.LoadFailed)
                {
                    // Locked or access denied: nothing is saved until the stored history could be read (see EnsureStoreReadable).
                    info("clipboard: history could not be read; saving paused until it can be");
                }

                _history.Retention = retention();
                _history.Prune(Now);
            }

            return _history;
        }
    }

    /// <summary>Records that the history changed and must be written by the next flush.</summary>
    public void MarkChanged() => _dirty = true;

    /// <summary>
    /// Applies a changed Retention to the loaded history: prunes it and marks it dirty. Returns true when it changed
    /// (the caller then schedules a flush). Does nothing before the history is loaded; loading applies it.
    /// </summary>
    public bool ApplyRetention()
    {
        if (_history is not { } history || history.Retention == retention())
        {
            return false;
        }

        history.Retention = retention();
        history.Prune(Now);
        MarkChanged();
        return true;
    }

    /// <summary>Removes every Clip except the Kept Clips and marks the history dirty.</summary>
    public void Clear()
    {
        History.ClearHistory();
        MarkChanged();
    }

    /// <summary>
    /// Writes the history when dirty. Waits for a write in progress first, then snapshots on the calling thread and
    /// writes in the background. A failed write marks the history dirty again (the next flush retries) and is logged;
    /// it never throws. Writes nothing while the store cannot be read.
    /// </summary>
    public async Task FlushAsync()
    {
        while (_dirty && _history is { } history)
        {
            if (!_writing.IsCompleted)
            {
                // Awaited with the caller's context, so the snapshot below is taken on the UI thread again.
                await _writing;
                if (_lastWriteFailed)
                {
                    return; // retried by the next flush, not in a loop
                }

                continue;
            }

            if (!EnsureStoreReadable() || store.Snapshot(history) is not { } snapshot)
            {
                return;
            }

            _dirty = false;
            _lastWriteFailed = false;
            var write = WriteAsync(snapshot);
            _writing = write;
            await write;
            return;
        }
    }

    private async Task WriteAsync(ClipSnapshot snapshot)
    {
        try
        {
            await Task.Run(() => store.Write(snapshot)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        {
            _lastWriteFailed = true;
            _dirty = true; // written with the next flush
            error(ex);
        }
    }

    /// <summary>On Quit: waits for a write in progress, then writes pending changes synchronously.</summary>
    public void Shutdown()
    {
        _writing.Wait();
        if (!_dirty || _history is not { } history || !EnsureStoreReadable())
        {
            return;
        }

        try
        {
            store.Save(history);
            _dirty = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        {
            error(ex);
        }
    }

    /// <summary>
    /// After the stored history could not be read at startup, tries again before any save and brings its Clips back
    /// into the current history. False (nothing may be saved) while it still cannot be read.
    /// </summary>
    private bool EnsureStoreReadable()
    {
        if (!store.LoadFailed || _history is null)
        {
            return true;
        }

        var stored = store.Load();
        if (store.LoadFailed)
        {
            return false;
        }

        _history.MergeStored(stored, Now);
        info("clipboard: stored history readable again, merged");
        MarkChanged(); // the merged Clips are not on disk in this form yet
        Merged?.Invoke();
        return true;
    }
}
