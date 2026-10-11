namespace Pollux.Core.Operations;

/// <summary>What undo and redo touch on disk; <see cref="DiskUndoTarget"/> in the app, the same with a fake Recycle Bin in tests.</summary>
public interface IUndoTarget
{
    DateTime UtcNow { get; }
    bool FileExists(string path);
    bool DirectoryExists(string path);
    bool IsEmptyFolder(string path);
    FileStamp? Stamp(string path);

    /// <summary>Which of <paramref name="paths"/> are in the Recycle Bin, deleted at or after <paramref name="sinceUtc"/>.</summary>
    IReadOnlySet<string> InRecycleBin(IReadOnlyList<string> paths, DateTime sinceUtc);

    /// <summary>Moves a file or folder (creating missing parent folders); throws IOException when it cannot.</summary>
    void Move(string source, string destination);
    void CreateFolder(string path);
    void RemoveFolder(string path);

    /// <summary>Deletes to the Recycle Bin; whatever still exists afterwards counts as not deleted.</summary>
    void Recycle(IReadOnlyList<string> paths);
    void Restore(string path, DateTime sinceUtc);
}

public sealed record UndoResult(bool Ok, string Message);

/// <summary>
/// The undo and redo stacks of file operations, one per Pollux process (shared by all windows), at most
/// <c>capacity</c> entries deep. Before undoing, every step is checked against the disk; when the disk changed (an entry is
/// gone or changed, a destination is taken) nothing happens, the message says why and the entry is dropped, so older
/// entries stay reachable. A step that fails midway rolls back the steps before it and keeps the entry for a retry.
/// Thread-safe: queue jobs are recorded from worker threads.
/// </summary>
public sealed class UndoHistory(int capacity = UndoHistory.DefaultCapacity)
{
    public const int DefaultCapacity = 100;

    private readonly object _gate = new();
    private readonly List<UndoEntry> _undo = [];
    private readonly List<UndoEntry> _redo = [];
    private bool _running;

    public bool CanUndo
    {
        get { lock (_gate) return _undo.Count > 0; }
    }

    public bool CanRedo
    {
        get { lock (_gate) return _redo.Count > 0; }
    }

    /// <summary>The label of what <see cref="Undo"/> would undo next, or null.</summary>
    public string? UndoLabel
    {
        get { lock (_gate) return _undo.Count > 0 ? _undo[^1].Label : null; }
    }

    /// <summary>Records a new operation (null or empty is ignored); it clears the redo stack, like in any editor.</summary>
    public void Record(UndoEntry? entry)
    {
        if (entry is null || entry.Steps.Count == 0) return;
        lock (_gate)
        {
            _undo.Add(entry);
            if (_undo.Count > Math.Max(1, capacity)) _undo.RemoveAt(0);
            _redo.Clear();
        }
    }

    /// <summary>Records every job of <paramref name="transfers"/> that did something, when it ends.</summary>
    public void Follow(Transfers transfers)
    {
        ArgumentNullException.ThrowIfNull(transfers);
        transfers.Finished += job => Record(UndoEntry.Transfer(job));
    }

    public UndoResult Undo(IUndoTarget target) => Run(target, redo: false);

    public UndoResult Redo(IUndoTarget target) => Run(target, redo: true);

    private UndoResult Run(IUndoTarget target, bool redo)
    {
        ArgumentNullException.ThrowIfNull(target);
        var verb = redo ? "redo" : "undo";
        UndoEntry entry;
        lock (_gate)
        {
            var from = redo ? _redo : _undo;
            if (from.Count == 0) return new(false, $"Nothing to {verb}");
            if (_running) return new(false, "Another undo or redo is still running");
            entry = from[^1];
            from.RemoveAt(from.Count - 1);
            _running = true;
        }

        try
        {
            if (Check(target, entry.Steps) is { } problem) return new(false, $"Cannot {verb} {entry.Label}: {problem}");

            var inverse = new List<UndoStep>();
            try
            {
                Execute(target, entry.Steps, inverse);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                RollBack(target, inverse);
                lock (_gate) (redo ? _redo : _undo).Add(entry);
                return new(false, $"Cannot {verb} {entry.Label}: {ex.Message}");
            }

            inverse.Reverse();
            lock (_gate) (redo ? _undo : _redo).Add(entry with { Steps = inverse });
            return new(true, $"{(redo ? "Redone" : "Undone")}: {entry.Label}");
        }
        finally
        {
            lock (_gate) _running = false;
        }
    }

    /// <summary>Why the steps cannot run on the disk as it is now; null when they can.</summary>
    internal static string? Check(IUndoTarget target, IReadOnlyList<UndoStep> steps)
    {
        bool Exists(string p) => target.FileExists(p) || target.DirectoryExists(p);

        // Earlier moves of the same entry change what later ones find (a swap goes through a temporary name), so the
        // moves are checked against the disk as it will be when they run, not as it is now.
        var vacated = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var arrived = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        static string Key(string p) => p.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        bool ExistsAfterMoves(string p) => arrived.Contains(Key(p)) || (Exists(p) && !vacated.Contains(Key(p)));
        foreach (var step in steps.Where(s => !s.Optional))
        {
            var name = UndoEntry.Name(step.Entry);
            switch (step.Kind)
            {
                case UndoStepKind.Move:
                    if (!ExistsAfterMoves(step.Entry)) return $"{name} no longer exists";
                    if (ExistsAfterMoves(step.To!) && !UndoEntry.SamePath(step.Entry, step.To!)) return $"{UndoEntry.Name(step.To!)} already exists in {Path.GetDirectoryName(step.To)}";
                    vacated.Add(Key(step.Entry));
                    arrived.Remove(Key(step.Entry));
                    arrived.Add(Key(step.To!));
                    vacated.Remove(Key(step.To!));
                    break;
                case UndoStepKind.Recycle:
                    if (!Exists(step.Entry)) return $"{name} no longer exists";
                    if (step.Stamp is { } stamp && target.Stamp(step.Entry) != stamp) return $"{name} was changed since";
                    break;
                case UndoStepKind.RemoveFolder:
                    if (!target.DirectoryExists(step.Entry)) return $"{name} no longer exists";
                    if (!target.IsEmptyFolder(step.Entry)) return $"{name} is no longer empty";
                    break;
                case UndoStepKind.CreateFolder or UndoStepKind.Restore:
                    if (Exists(step.Entry)) return $"{name} already exists in {Path.GetDirectoryName(step.Entry)}";
                    break;
            }
        }

        foreach (var group in steps.Where(s => s.Kind == UndoStepKind.Restore && !s.Optional).GroupBy(s => s.Since))
        {
            var paths = group.Select(s => s.Entry).ToList();
            var found = target.InRecycleBin(paths, group.Key);
            if (paths.FirstOrDefault(p => !found.Contains(p)) is { } missing) return $"{UndoEntry.Name(missing)} is no longer in the Recycle Bin";
        }

        return null;
    }

    /// <summary>Runs the steps in order and collects the step that reverses each one; consecutive recycles go in one batch.</summary>
    private static void Execute(IUndoTarget target, IReadOnlyList<UndoStep> steps, List<UndoStep> inverse)
    {
        for (var i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            switch (step.Kind)
            {
                case UndoStepKind.Move:
                    target.Move(step.Entry, step.To!);
                    inverse.Add(UndoStep.Move(step.To!, step.Entry));
                    break;
                case UndoStepKind.Recycle:
                    var batch = steps.Skip(i).TakeWhile(s => s.Kind == UndoStepKind.Recycle).Select(s => s.Entry).ToList();
                    i += batch.Count - 1;
                    var since = target.UtcNow;
                    target.Recycle(batch);
                    var left = new List<string>();
                    foreach (var path in batch)
                    {
                        if (target.FileExists(path) || target.DirectoryExists(path)) left.Add(path);
                        else inverse.Add(UndoStep.Restore(path, since));
                    }

                    if (left.Count > 0) throw new IOException($"{UndoEntry.Name(left[0])} was not moved to the Recycle Bin");
                    break;
                case UndoStepKind.Restore:
                    target.Restore(step.Entry, step.Since);
                    inverse.Add(UndoStep.Recycle(step.Entry, target.Stamp(step.Entry)));
                    break;
                case UndoStepKind.CreateFolder:
                    if (step.Optional && target.DirectoryExists(step.Entry)) break;
                    target.CreateFolder(step.Entry);
                    inverse.Add(UndoStep.RemoveFolder(step.Entry, step.Optional));
                    break;
                case UndoStepKind.RemoveFolder:
                    if (step.Optional && !(target.DirectoryExists(step.Entry) && target.IsEmptyFolder(step.Entry))) break;
                    target.RemoveFolder(step.Entry);
                    inverse.Add(UndoStep.CreateFolder(step.Entry, step.Optional));
                    break;
            }
        }
    }

    /// <summary>Best effort: reverses what already ran, newest first; what cannot be reversed stays as it is.</summary>
    private static void RollBack(IUndoTarget target, List<UndoStep> done)
    {
        for (var i = done.Count - 1; i >= 0; i--)
        {
            try
            {
                Execute(target, [done[i]], []);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
