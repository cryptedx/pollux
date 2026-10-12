using Pollux.Core.Archives;
using Pollux.Core.Files;

namespace Pollux.Core.Navigation;

/// <summary>What opening (Enter, double-click) an entry of any location does.</summary>
public abstract record OpenAction
{
    private OpenAction() { }

    /// <summary>Show this location in the Tab: a folder, a zip or a folder in one, a share, a shell folder.</summary>
    public sealed record Navigate(string Location) : OpenAction;

    /// <summary>Open a file on disk with its app (Shell.Open).</summary>
    public sealed record Launch(string Path) : OpenAction;

    /// <summary>Extract a file from a zip to the session temp folder (<see cref="ZipFolder.ExtractForOpen"/>), then launch it.</summary>
    public sealed record ExtractAndLaunch(string EntryLocation) : OpenAction;

    /// <summary>Run the shell item's default verb (a Control Panel applet, a file on a phone).</summary>
    public sealed record InvokeShell(string ParsingName) : OpenAction;
}

public static class ItemOpen
{
    /// <summary>
    /// The action for <paramref name="entry"/>. A .zip on disk is entered like a folder when <paramref name="browseZips"/>
    /// (Explorer's default and Pollux's); otherwise, and for a zip inside a zip, it is launched.
    /// </summary>
    public static OpenAction For(FileEntry entry, bool browseZips = true, Func<string, bool>? isFile = null)
    {
        var path = entry.FullPath;
        if (entry.IsDirectory) return new OpenAction.Navigate(path);
        return Locations.Classify(path, isFile) switch
        {
            LocationKind.Zip when ZipFolder.TryParse(path, out _, out var inner) && inner.Length > 0 => new OpenAction.ExtractAndLaunch(path),
            LocationKind.Zip when browseZips => new OpenAction.Navigate(path),
            LocationKind.Shell => new OpenAction.InvokeShell(Locations.ParsingName(path)),
            LocationKind.Server => new OpenAction.Navigate(path),
            _ => new OpenAction.Launch(path),
        };
    }
}
