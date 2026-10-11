namespace Pollux.Core.Operations;

/// <summary>Size and last write time of a file: a cheap check that a file Pollux created was not changed since.</summary>
public sealed record FileStamp(long Length, DateTime LastWriteUtc)
{
    /// <summary>The stamp of the file at <paramref name="path"/>; null for a folder or nothing.</summary>
    public static FileStamp? Of(string path)
    {
        var file = new FileInfo(path);
        return file.Exists ? new(file.Length, file.LastWriteTimeUtc) : null;
    }
}

public enum UndoStepKind { Move, Recycle, Restore, CreateFolder, RemoveFolder }

/// <summary>
/// One reversible step of an <see cref="UndoEntry"/>. <see cref="Entry"/> is the file or folder it acts on; a Move takes it
/// <see cref="To"/>; a Recycle only takes a file whose <see cref="Stamp"/> still matches; a Restore brings back what was
/// deleted to the Recycle Bin at or after <see cref="Since"/>. An <see cref="Optional"/> folder step is housekeeping: it is
/// skipped when it does not apply (folder already there, not empty) instead of stopping the undo.
/// </summary>
public sealed record UndoStep(UndoStepKind Kind, string Entry, string? To = null, FileStamp? Stamp = null, DateTime Since = default, bool Optional = false)
{
    public static UndoStep Move(string from, string to) => new(UndoStepKind.Move, from, to);

    public static UndoStep Recycle(string entry, FileStamp? stamp = null) => new(UndoStepKind.Recycle, entry, Stamp: stamp);

    public static UndoStep Restore(string entry, DateTime sinceUtc) => new(UndoStepKind.Restore, entry, Since: sinceUtc);

    public static UndoStep CreateFolder(string folder, bool optional = false) => new(UndoStepKind.CreateFolder, folder, Optional: optional);

    public static UndoStep RemoveFolder(string folder, bool optional = false) => new(UndoStepKind.RemoveFolder, folder, Optional: optional);
}

/// <summary>
/// One file operation as the undo history keeps it: a label for the status message and the steps that undo it. Never made
/// for a permanent delete, which cannot be undone.
/// </summary>
public sealed record UndoEntry(string Label, IReadOnlyList<UndoStep> Steps)
{
    public static UndoEntry Rename(string from, string to) =>
        new($"Rename {Name(from)} → {Name(to)}", [UndoStep.Move(to, from)]);

    /// <summary>
    /// A batch rename in <paramref name="folder"/> from the renames it ran, in order (including hops over temporary names):
    /// undo moves every name back, newest first. <paramref name="itemCount"/> is the number of renamed items, for the label.
    /// </summary>
    public static UndoEntry? BatchRename(string folder, IReadOnlyList<Files.RenameStep> executed, int itemCount)
    {
        ArgumentException.ThrowIfNullOrEmpty(folder);
        ArgumentNullException.ThrowIfNull(executed);
        if (executed.Count == 0) return null;
        var steps = executed.Reverse().Select(s => UndoStep.Move(Path.Combine(folder, s.To), Path.Combine(folder, s.From))).ToList();
        return new(itemCount == 1 ? "Rename 1 item" : $"Rename {itemCount} items", steps);
    }

    /// <summary>A new file goes to the Recycle Bin on undo, but only while it is unchanged.</summary>
    public static UndoEntry NewFile(string path, IUndoTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return new($"New file {Name(path)}", [UndoStep.Recycle(path, target.Stamp(path))]);
    }

    /// <summary>A new folder is removed on undo, but only while it is empty.</summary>
    public static UndoEntry NewFolder(string path) => new($"New folder {Name(path)}", [UndoStep.RemoveFolder(path)]);

    /// <summary>
    /// A delete to the Recycle Bin started at <paramref name="sinceUtc"/>: undo restores the entries that really are in the
    /// Recycle Bin now (one Windows deleted permanently, e.g. on a drive without one, is left out); null when none is.
    /// </summary>
    public static UndoEntry? Recycle(IReadOnlyList<string> paths, DateTime sinceUtc, IUndoTarget target)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(target);
        var gone = paths.Where(p => !target.FileExists(p) && !target.DirectoryExists(p)).ToList();
        if (gone.Count == 0) return null;
        var recycled = target.InRecycleBin(gone, sinceUtc);
        var steps = gone.Where(recycled.Contains).Select(p => UndoStep.Restore(p, sinceUtc)).ToList();
        if (steps.Count == 0) return null;
        return new(steps.Count == 1 ? $"Delete {Name(steps[0].Entry)}" : $"Delete {steps.Count} items", steps);
    }

    /// <summary>A finished job of Pollux's own queue (<see cref="Transfer(TransferKind, IReadOnlyList{string}, string, IReadOnlyList{TransferStep}, Func{string, FileStamp?})"/>).</summary>
    public static UndoEntry? Transfer(TransferJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        return Transfer(job.Request.Kind, job.Request.Sources, job.Request.TargetDirectory, job.Journal, FileStamp.Of);
    }

    /// <summary>
    /// A copy or move from its journal; null when it did nothing. Undoing a copy recycles the files it created (only while
    /// unchanged) and removes the folders it created when they are empty then, and keeps files it replaced, whose old content is gone anyway. Undoing a
    /// move moves every entry back, recreating source folders and removing the target folders the move created.
    /// </summary>
    public static UndoEntry? Transfer(TransferKind kind, IReadOnlyList<string> sources, string targetDirectory, IReadOnlyList<TransferStep> journal, Func<string, FileStamp?> stamp)
    {
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(stamp);
        var steps = new List<UndoStep>();
        if (kind == TransferKind.Copy)
        {
            var created = new HashSet<string>(journal.Where(j => j.Kind == TransferStepKind.Folder && !j.Existed).Select(j => j.Target), StringComparer.OrdinalIgnoreCase);
            foreach (var j in journal)
            {
                if (j.Existed || j.Kind == TransferStepKind.Folder) continue;
                steps.Add(UndoStep.Recycle(j.Target, stamp(j.Target)));
            }

            // A created folder is not recycled as a whole: what was saved into it since would go along. Its journaled files
            // are recycled one by one (only while unchanged) and the folder is removed only if that left it empty.
            steps.AddRange(created.OrderByDescending(f => f.Length).Select(f => UndoStep.RemoveFolder(f, optional: true)));
        }
        else
        {
            steps.AddRange(journal.Where(j => j.Kind == TransferStepKind.Folder).Select(j => UndoStep.CreateFolder(j.Source, optional: true)));
            steps.AddRange(journal.Where(j => j.Kind != TransferStepKind.Folder).Select(j => UndoStep.Move(j.Target, j.Source)));
            steps.AddRange(journal.Where(j => j.Kind == TransferStepKind.Folder && !j.Existed).Reverse().Select(j => UndoStep.RemoveFolder(j.Target, optional: true)));
        }

        return steps.Count == 0 ? null : new(Describe(kind, sources, targetDirectory), steps);
    }

    /// <summary>
    /// A copy or move the Windows shell did, judged afterwards: each source went to its name in
    /// <paramref name="targetDirectory"/> or to the new name the shell gave it (<paramref name="renamed"/>, matched by
    /// source or intended target). Only entries that are new there are recorded; one whose name existed before
    /// (<paramref name="existedBefore"/>: replaced or merged) or that is not there (skipped, cancelled) is left out.
    /// </summary>
    public static UndoEntry? ShellTransfer(
        TransferKind kind,
        IReadOnlyList<string> sources,
        string targetDirectory,
        IReadOnlyList<(string OldPath, string NewPath)> renamed,
        Func<string, bool> existedBefore,
        IUndoTarget target)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(renamed);
        ArgumentNullException.ThrowIfNull(existedBefore);
        ArgumentNullException.ThrowIfNull(target);
        var steps = new List<UndoStep>();
        foreach (var source in sources)
        {
            var intended = Path.Combine(targetDirectory, Name(source));
            var actual = intended;
            foreach (var (oldPath, newPath) in renamed)
            {
                if (SamePath(oldPath, source) || SamePath(oldPath, intended))
                {
                    actual = newPath;
                    break;
                }
            }

            var isFile = target.FileExists(actual);
            if (existedBefore(actual) || !(isFile || target.DirectoryExists(actual))) continue;
            if (kind == TransferKind.Move)
            {
                // A folder moved to another drive cannot be moved back in one step (DiskUndoTarget.Move), so it is not recorded.
                var gone = !target.FileExists(source) && !target.DirectoryExists(source);
                if (gone && (isFile || SameDrive(source, actual))) steps.Add(UndoStep.Move(actual, source));
            }
            else
            {
                steps.Add(UndoStep.Recycle(actual, isFile ? target.Stamp(actual) : null));
            }
        }

        return steps.Count == 0 ? null : new(Describe(kind, sources, targetDirectory), steps);
    }

    internal static string Name(string path) => Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

    internal static bool SamePath(string a, string b) =>
        string.Equals(a.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), b.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);

    internal static bool SameDrive(string a, string b) =>
        string.Equals(Path.GetPathRoot(Path.GetFullPath(a)), Path.GetPathRoot(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);

    private static string Describe(TransferKind kind, IReadOnlyList<string> sources, string targetDirectory)
    {
        var verb = kind == TransferKind.Copy ? "Copy" : "Move";
        var what = sources.Count == 1 ? Name(sources[0]) : $"{sources.Count} items";
        return $"{verb} {what} → {Name(targetDirectory)}";
    }
}
