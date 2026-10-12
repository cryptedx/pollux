using Pollux.Core.Files;

namespace Pollux.Core.Navigation;

/// <summary>
/// The Windows shell namespace (This PC, Network, Control Panel, Libraries, phones), implemented in the App.
/// Every member blocks and must run on an STA thread (<see cref="DirectoryReader.Stream"/> with staThread, <see cref="StaThread.Run{T}"/>).
/// </summary>
public interface IShellNamespace
{
    /// <summary>
    /// The items of the folder with this parsing name, hidden ones included (marked <see cref="FileAttributes.Hidden"/>).
    /// <see cref="FileEntry.FullPath"/> is <see cref="Locations.FromShell"/> of the item: its filesystem path when it has one.
    /// Throws <see cref="IOException"/> when the folder cannot be opened.
    /// </summary>
    IEnumerable<FileEntry> Enumerate(string parsingName, CancellationToken cancellationToken);

    /// <summary>The breadcrumbs of a shell location, root first, as the shell names them; ends with the location itself.</summary>
    IReadOnlyList<LocationSegment> Segments(string location);

    /// <summary>The filesystem path of a shell location ("shell:Downloads"), or null when it has none.</summary>
    string? FileSystemPath(string location);
}
