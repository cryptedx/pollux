using Pollux.Core.Files;

namespace Pollux.Core.Operations;

/// <summary>What the transfers UI shows for the queue: badge, status-bar text, row texts, toasts, conflict dialog text.</summary>
public static class TransferBoard
{
    private const int MaxRows = 8;

    /// <summary>The badge stays while something runs and after a job ended with problems, until the user clears the list.</summary>
    public static bool BadgeVisible(IReadOnlyList<TransferJob> jobs) =>
        jobs.Count > 0 && jobs.Any(j => !j.IsFinished || j.Errors.Count > 0 || j.State == TransferState.Failed);

    /// <summary>Status-bar text: the first unfinished job plus " +n" for the queued ones, or the problems marker.</summary>
    public static string StatusText(IReadOnlyList<TransferJob> jobs)
    {
        var active = jobs.Where(j => !j.IsFinished).ToList();
        if (active.Count == 0) return "⚠ problems";

        var current = active[0];
        var p = current.Progress;
        var queued = active.Count > 1 ? $" +{active.Count - 1}" : "";
        return current.State == TransferState.Paused
            ? $"paused {p.Fraction:P0}{queued}"
            : $"{(current.Request.Kind == TransferKind.Copy ? "copy" : "move")} {p.Fraction:P0} · {Format.Size((long)p.BytesPerSecond)}/s{queued}";
    }

    /// <summary>The newest jobs first, as many as the panel shows.</summary>
    public static List<TransferJob> VisibleJobs(IReadOnlyList<TransferJob> jobs) => jobs.AsEnumerable().Reverse().Take(MaxRows).ToList();

    /// <summary>What a panel row was built from; a change in either needs new buttons and error lines.</summary>
    public readonly record struct RowSnapshot(TransferState State, int Errors)
    {
        public static RowSnapshot Of(TransferJob job) => new(job.State, job.Errors.Count);
    }

    /// <summary>True when the open panel has to be rebuilt; otherwise its rows can just be refilled in place.</summary>
    public static bool NeedsRebuild(IReadOnlyDictionary<int, RowSnapshot> shown, IReadOnlyList<TransferJob> visible) =>
        visible.Count != shown.Count || visible.Any(j => !shown.TryGetValue(j.Id, out var row) || row != RowSnapshot.Of(j));

    /// <summary>The progress line of a panel row.</summary>
    public static string RowStatus(TransferJob job)
    {
        var p = job.Progress;
        return job.State switch
        {
            TransferState.Completed => $"done · {p.TotalFiles} files · {Format.Size(p.TotalBytes)}",
            TransferState.Failed => "failed: " + job.Error,
            TransferState.Cancelled => "cancelled",
            TransferState.Queued => "waiting",
            TransferState.Paused => $"paused · {Format.Size(p.DoneBytes)} of {Format.Size(p.TotalBytes)}",
            _ => $"{p.DoneFiles}/{p.TotalFiles} files · {Format.Size(p.DoneBytes)} of {Format.Size(p.TotalBytes)} · {Format.Size((long)p.BytesPerSecond)}/s",
        };
    }

    /// <summary>The toast for a finished job; problems and failures stay longer.</summary>
    public static (string Text, int Seconds) FinishedToast(TransferJob job)
    {
        var errors = job.Errors.Count;
        var text = job.State switch
        {
            TransferState.Completed when errors == 0 => $"✓ {job.Title}",
            TransferState.Completed => $"⚠ {job.Title}: {errors} problem(s) — see transfers",
            TransferState.Cancelled => $"Cancelled: {job.Title}",
            _ => $"✗ {job.Title}: {job.Error}",
        };
        return (text, errors > 0 || job.State == TransferState.Failed ? 6 : 3);
    }

    /// <summary>True when the job changed the content of <paramref name="folder"/>: its target, or the parent of a source (moves).</summary>
    public static bool Affects(TransferJob job, string folder) =>
        string.Equals(folder, job.Request.TargetDirectory, StringComparison.OrdinalIgnoreCase)
        || job.Request.Sources.Any(s => string.Equals(Path.GetDirectoryName(s), folder, StringComparison.OrdinalIgnoreCase));

    /// <summary>The conflict dialog body; a file in the way of a folder has no comparison.</summary>
    public static string ConflictText(TransferConflict c) => c.IsDirectory
        ? $"A file named \"{Path.GetFileName(c.TargetPath)}\" is in the way of a folder."
        : $"{Path.GetFileName(c.TargetPath)} already exists in {Path.GetDirectoryName(c.TargetPath)}\n\n" +
          $"new:      {Format.Size(c.SourceSize),10}  {c.SourceModified:yyyy-MM-dd HH:mm}{(c.SourceModified > c.TargetModified ? "  (newer)" : "")}\n" +
          $"existing: {Format.Size(c.TargetSize),10}  {c.TargetModified:yyyy-MM-dd HH:mm}{(c.TargetModified > c.SourceModified ? "  (newer)" : "")}";
}
