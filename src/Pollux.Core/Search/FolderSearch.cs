using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Pollux.Core.Files;

namespace Pollux.Core.Search;

/// <summary>One Folder Search result: the entry, its path relative to the search root and the name matches in that path.</summary>
public sealed record FolderSearchHit(FileEntry Entry, string RelativePath, IReadOnlyList<MatchRange> Highlights);

/// <summary>How a Folder Search walks: folders it never enters, the content size limit, hidden entries and a result cap.</summary>
public sealed record FolderSearchOptions(
    IReadOnlyCollection<string> ExcludeNames,
    long MaxContentBytes,
    bool IncludeHidden = false,
    bool IncludeSystem = false,
    int MaxResults = FolderSearch.DefaultMaxResults)
{
    /// <summary>The options settings.json asks for: search.excludeNames, search.maxContentFileMegabytes, files.showHidden/showSystem.</summary>
    public static FolderSearchOptions For(Settings.PolluxSettings settings) => new(
        settings.Search.ExcludeNames,
        Math.Max(0, settings.Search.MaxContentFileMegabytes) * 1024L * 1024L,
        settings.Files.ShowHidden,
        settings.Files.ShowSystem);
}

/// <summary>
/// Folder Search: walks everything below a root, shallow folders first, and yields what matches a <see cref="FolderSearchQuery"/>.
/// Excluded names are neither listed nor entered; links (junctions, symlinks) are listed but not entered.
/// Content is only read for local files up to the size limit, so cloud-only files are never downloaded.
/// </summary>
public static class FolderSearch
{
    public const int DefaultMaxResults = 10_000;

    public static IEnumerable<FolderSearchHit> Enumerate(string root, FolderSearchQuery query, FolderSearchOptions options, CancellationToken cancellationToken)
    {
        var exclude = new HashSet<string>(options.ExcludeNames, StringComparer.OrdinalIgnoreCase);
        // "C:\dev" and "C:\dev\" cut 7 chars; a drive root ("C:\") keeps its separator when trimmed.
        var trimmed = Path.TrimEndingDirectorySeparator(root);
        var prefix = trimmed.Length + (Path.EndsInDirectorySeparator(trimmed) ? 0 : 1);
        var found = 0;
        var folders = new Queue<string>();
        folders.Enqueue(root);
        while (folders.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var folder = folders.Dequeue();
            List<FileEntry> entries;
            try
            {
                entries = [.. DirectoryReader.Enumerate(folder)];
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                continue;
            }

            foreach (var e in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (exclude.Contains(e.Name) || !IsListed(e, options)) continue;
                if (e.IsDirectory && !IsLink(e)) folders.Enqueue(e.FullPath);
                if (!query.Matches(e)) continue;
                if (query.Content is { } text && !ContentMatches(e, text, options.MaxContentBytes, cancellationToken)) continue;

                var relative = e.FullPath.Length > prefix ? e.FullPath[prefix..] : e.Name;
                var offset = relative.Length - e.Name.Length;
                var highlights = query.Highlights(e.Name).Select(r => r with { Start = r.Start + offset }).ToList();
                yield return new FolderSearchHit(e, relative, highlights);
                if (++found >= options.MaxResults) yield break;
            }
        }
    }

    /// <summary>
    /// Runs <see cref="Enumerate"/> on a background thread and streams its hits in batches: the first hit right away,
    /// then whatever arrived, at most once per <paramref name="interval"/>, so the UI is not flooded.
    /// </summary>
    public static async IAsyncEnumerable<IReadOnlyList<FolderSearchHit>> SearchAsync(
        string root,
        FolderSearchQuery query,
        FolderSearchOptions options,
        TimeSpan interval,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Also stops the walk when the caller leaves the loop early.
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var channel = Channel.CreateUnbounded<FolderSearchHit>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        var producer = Task.Run(() =>
        {
            try
            {
                foreach (var hit in Enumerate(root, query, options, stop.Token))
                {
                    channel.Writer.TryWrite(hit);
                }

                channel.Writer.Complete();
            }
            catch (Exception ex)
            {
                channel.Writer.Complete(ex);
            }
        }, stop.Token);

        try
        {
            while (await channel.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var batch = new List<FolderSearchHit>();
                while (channel.Reader.TryRead(out var hit)) batch.Add(hit);
                yield return batch;
                await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
            }

            await producer.ConfigureAwait(false);
        }
        finally
        {
            await stop.CancelAsync().ConfigureAwait(false);
        }
    }

    /// <summary>The Pane's visibility rules: in-flight Transfer files never, hidden and system entries per settings.</summary>
    private static bool IsListed(FileEntry e, FolderSearchOptions options)
    {
        if (e.Name.EndsWith(FolderProjection.PartSuffix, StringComparison.OrdinalIgnoreCase)) return false;
        if (!options.IncludeHidden && e.IsHidden) return false;
        return options.IncludeSystem || !(e.IsSystem && e.IsHidden);
    }

    /// <summary>
    /// A junction or symbolic link (entering it could loop or walk a tree twice). Other reparse points, such as
    /// OneDrive folders, are ordinary folders.
    /// </summary>
    private static bool IsLink(FileEntry e)
    {
        if (!e.Attributes.HasFlag(FileAttributes.ReparsePoint)) return false;
        try
        {
            return new DirectoryInfo(e.FullPath).LinkTarget is not null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static bool ContentMatches(FileEntry e, string text, long maxBytes, CancellationToken cancellationToken)
    {
        if (e.IsDirectory || e.Size == 0 || e.Size > maxBytes || e.Cloud == CloudState.CloudOnly) return false;
        try
        {
            return FileContent.Contains(e.FullPath, text, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
