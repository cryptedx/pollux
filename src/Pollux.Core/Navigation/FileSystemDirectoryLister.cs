using System.IO.Enumeration;

namespace Pollux.Core.Navigation;

/// <summary>
/// <see cref="IDirectoryLister"/> on the real file system. Never throws on access denied or missing folders and gives up
/// (empty list) after the timeout, so a dead network share cannot stall the path bar.
/// </summary>
public sealed class FileSystemDirectoryLister : IDirectoryLister
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(2);
    public const int DefaultMaxEntries = 20000;

    private readonly TimeSpan _timeout;
    private readonly Func<string, CancellationToken, IEnumerable<DirectoryItem>> _enumerate;

    public FileSystemDirectoryLister(TimeSpan? timeout = null, int maxEntries = DefaultMaxEntries)
        : this(timeout, (directory, token) => Enumerate(directory, maxEntries, token))
    {
    }

    /// <summary>Takes the enumeration as a function so tests can simulate slow or failing folders.</summary>
    public FileSystemDirectoryLister(TimeSpan? timeout, Func<string, CancellationToken, IEnumerable<DirectoryItem>> enumerate)
    {
        _timeout = timeout ?? DefaultTimeout;
        _enumerate = enumerate;
    }

    public IReadOnlyList<DirectoryItem> List(string directory, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = cts.Token;
        var work = Task.Run(() => (IReadOnlyList<DirectoryItem>)_enumerate(directory, token).ToList(), token);
        try
        {
            if (work.Wait(_timeout, cancellationToken))
            {
                return work.Result;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            cts.Cancel();
            throw;
        }
        catch (Exception)
        {
            return [];
        }

        cts.Cancel();
        _ = work.ContinueWith(static t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
        return [];
    }

    private static IEnumerable<DirectoryItem> Enumerate(string directory, int maxEntries, CancellationToken token)
    {
        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            AttributesToSkip = 0,
            RecurseSubdirectories = false,
            ReturnSpecialDirectories = false,
        };
        var count = 0;
        foreach (var item in new FileSystemEnumerable<DirectoryItem>(directory, Transform, options))
        {
            token.ThrowIfCancellationRequested();
            yield return item;
            if (++count >= maxEntries)
            {
                yield break;
            }
        }
    }

    private static DirectoryItem Transform(ref FileSystemEntry e) => new(
        e.FileName.ToString(),
        e.IsDirectory,
        (e.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0);
}
