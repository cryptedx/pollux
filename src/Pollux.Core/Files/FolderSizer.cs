using System.IO.Enumeration;

namespace Pollux.Core.Files;

/// <summary>Computes the total size of folders: recursive, skipping reparse points (no loops) and tolerating access denied.</summary>
public static class FolderSizer
{
    private static readonly EnumerationOptions Options = new()
    {
        AttributesToSkip = 0,
        IgnoreInaccessible = true,
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false,
    };

    /// <summary>Bytes of all files below <paramref name="folder"/>. Folders that are reparse points (junctions, symlinks) are not entered.</summary>
    public static long Measure(string folder, CancellationToken cancellationToken = default)
    {
        long total = 0;
        var pending = new Stack<string>();
        pending.Push(folder);
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var dir = pending.Pop();
            try
            {
                var entries = new FileSystemEnumerable<(bool IsDirectory, bool IsReparse, long Length, string Path)>(
                    dir,
                    (ref FileSystemEntry e) => (e.IsDirectory, e.Attributes.HasFlag(FileAttributes.ReparsePoint), e.IsDirectory ? 0 : e.Length, e.IsDirectory ? e.ToFullPath() : ""),
                    Options);
                foreach (var e in entries)
                {
                    if (!e.IsDirectory) total += e.Length;
                    else if (!e.IsReparse) pending.Push(e.Path);
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
            {
                // A folder we cannot read counts as empty.
            }
        }

        return total;
    }

    /// <summary>
    /// Measures each folder (a few at a time) and reports it through <paramref name="onMeasured"/> as soon as it is done.
    /// <paramref name="onMeasured"/> is called from worker threads. Returns when all are done or cancelled (no exception).
    /// </summary>
    public static async Task MeasureAllAsync(
        IEnumerable<string> folders,
        Action<string, long> onMeasured,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await Parallel.ForEachAsync(
                folders,
                new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellationToken },
                (folder, ct) =>
                {
                    var size = Measure(folder, ct);
                    onMeasured(folder, size);
                    return ValueTask.CompletedTask;
                }).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Cancelled on navigation.
        }
    }
}
