using System.IO.Enumeration;
using System.Runtime.CompilerServices;

namespace Pollux.Core.Files;

/// <summary>Enumerates a folder off the UI thread and streams entries in batches so the first rows show immediately.</summary>
public static class DirectoryReader
{
    private static readonly EnumerationOptions Options = new()
    {
        AttributesToSkip = 0,
        IgnoreInaccessible = true,
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false,
    };

    public static async IAsyncEnumerable<IReadOnlyList<FileEntry>> ReadAsync(
        string directory,
        int batchSize = 500,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var channel = System.Threading.Channels.Channel.CreateBounded<IReadOnlyList<FileEntry>>(4);
        var producer = Task.Run(() =>
        {
            try
            {
                var batch = new List<FileEntry>(batchSize);
                foreach (var e in Enumerate(directory))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    batch.Add(e);
                    if (batch.Count == batchSize)
                    {
                        channel.Writer.WriteAsync(batch, cancellationToken).AsTask().GetAwaiter().GetResult();
                        // Later batches can be bigger: the first one is what makes the folder feel instant.
                        batch = new List<FileEntry>(batchSize * 4);
                        batchSize *= 4;
                    }
                }

                if (batch.Count > 0)
                {
                    channel.Writer.WriteAsync(batch, cancellationToken).AsTask().GetAwaiter().GetResult();
                }

                channel.Writer.Complete();
            }
            catch (Exception ex)
            {
                channel.Writer.Complete(ex);
            }
        }, cancellationToken);

        await foreach (var batch in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return batch;
        }

        await producer.ConfigureAwait(false);
    }

    public static IEnumerable<FileEntry> Enumerate(string directory) =>
        new FileSystemEnumerable<FileEntry>(directory, Transform, Options);

    public static FileEntry? TryGet(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Exists)
            {
                return new FileEntry(info.Name, info.FullName, false, info.Length, info.LastWriteTime, info.Attributes);
            }

            var dir = new DirectoryInfo(path);
            return dir.Exists ? new FileEntry(dir.Name, dir.FullName, true, 0, dir.LastWriteTime, dir.Attributes) : null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static FileEntry Transform(ref FileSystemEntry e) => new(
        e.FileName.ToString(),
        e.ToFullPath(),
        e.IsDirectory,
        e.IsDirectory ? 0 : e.Length,
        e.LastWriteTimeUtc.LocalDateTime,
        e.Attributes);
}
