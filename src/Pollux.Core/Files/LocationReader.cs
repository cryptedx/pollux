using Pollux.Core.Archives;
using Pollux.Core.Navigation;

namespace Pollux.Core.Files;

/// <summary>
/// Lists any location a Tab can show, in the streaming shape of <see cref="DirectoryReader.ReadAsync"/>: folders from
/// disk, zip folders from the archive, a server's shares, shell folders through <see cref="IShellNamespace"/> on an STA
/// thread. Home and the Recycle Bin have their own views and are not listed here. Errors surface as
/// <see cref="IOException"/>, <see cref="UnauthorizedAccessException"/> or <see cref="DirectoryNotFoundException"/>.
/// </summary>
public sealed class LocationReader(IShellNamespace shell, IServerShares shares, Func<string, bool>? isFile = null)
{
    public ZipFolder Zips { get; } = new();

    public IAsyncEnumerable<IReadOnlyList<FileEntry>> ReadAsync(string location, int batchSize = 500, CancellationToken cancellationToken = default) =>
        Locations.Classify(location, isFile) switch
        {
            LocationKind.Folder => DirectoryReader.ReadAsync(location, batchSize, cancellationToken),
            LocationKind.Zip => DirectoryReader.Stream(() => Zips.Children(location), batchSize, staThread: false, cancellationToken),
            LocationKind.Server => DirectoryReader.Stream(() => ServerShares.Entries(Locations.ServerOf(location)!, shares.List(Locations.ServerOf(location)!)), batchSize, staThread: false, cancellationToken),
            LocationKind.Shell => DirectoryReader.Stream(() => shell.Enumerate(Locations.ParsingName(location), cancellationToken), batchSize, staThread: true, cancellationToken),
            var kind => throw new ArgumentException($"{kind} has no listing.", nameof(location)),
        };

    /// <summary>The breadcrumbs: from <see cref="Locations.Segments"/>, or from the shell for shell folders it alone can name.</summary>
    public Task<IReadOnlyList<LocationSegment>> SegmentsAsync(string location)
    {
        if (!Locations.IsShell(location) || Locations.Title(location) is not null) return Task.FromResult(Locations.Segments(location));
        return StaThread.Run(() => shell.Segments(location));
    }
}
