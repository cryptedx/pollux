using Pollux.App.Interop;
using Pollux.Core;
using Pollux.Core.Archives;
using Pollux.Core.Files;
using Pollux.Core.Navigation;

namespace Pollux.App.Services;

/// <summary>
/// The App side of locations beyond folders: one <see cref="LocationReader"/> for every Pane, and opening or copying out
/// items of zips and shell folders. Opening a file inside a zip extracts it read-only to this session's temp folder.
/// </summary>
internal static class VirtualLocations
{
    public static LocationReader Reader { get; } = new(ShellNamespace.Instance, NetShares.Instance);

    /// <summary>This process's folder for files opened from inside zips (%LocalAppData%\Pollux\temp\&lt;session&gt;).</summary>
    public static string TempFolder { get; } = ZipTemp.SessionFolder(PolluxPaths.Current());

    /// <summary>
    /// Opens <paramref name="entry"/> as Enter does in any location; returns the location to navigate to, or null when the
    /// item was launched (or failed: <paramref name="error"/> is set). <paramref name="confirm"/> answers a zip bomb
    /// limit breach and runs on a worker thread.
    /// </summary>
    public static async Task<(string? Navigate, string? Error)> OpenAsync(nint owner, FileEntry entry, Func<ExtractLimitBreach, bool>? confirm, bool browseZips = true)
    {
        switch (ItemOpen.For(entry, browseZips))
        {
            case OpenAction.Navigate n:
                return (n.Location, null);
            case OpenAction.Launch l:
                Shell.Open(l.Path);
                return (null, null);
            case OpenAction.ExtractAndLaunch x:
                try
                {
                    var path = await Task.Run(() => Reader.Zips.ExtractForOpen(x.EntryLocation, TempFolder, confirm));
                    Shell.Open(path);
                    return (null, null);
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ExtractLimitExceededException)
                {
                    return (null, ex.Message);
                }

            case OpenAction.InvokeShell s:
                return (null, await ShellNamespace.InvokeDefaultAsync(owner, s.ParsingName) ? null : "Windows could not open " + entry.Name);
            default:
                return (null, null);
        }
    }

    /// <summary>
    /// Copies items of a read-only location into <paramref name="targetFolder"/>: extracting from a zip (one archive at a
    /// time, as Pollux lists them), the shell's own copy with its progress dialog for anything else. Null, or a message.
    /// </summary>
    public static async Task<string?> CopyOutAsync(nint owner, IReadOnlyList<string> locations, string targetFolder, Func<ExtractLimitBreach, bool>? confirm, CancellationToken ct = default)
    {
        if (locations.Count == 0) return null;
        if (Locations.Classify(locations[0]) == LocationKind.Zip)
        {
            try
            {
                var result = await Task.Run(() => ZipFolder.CopyOut(locations, targetFolder, confirm: confirm, ct: ct), ct);
                var skipped = result.SkippedExisting + result.SkippedUnsafe;
                return skipped > 0 ? $"{skipped} item(s) skipped (already there or unsafe names)" : null;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or ExtractLimitExceededException)
            {
                return ex.Message;
            }
        }

        return await ShellNamespace.CopyToFolderAsync(owner, locations, targetFolder) ? null : "Copy did not complete";
    }

    /// <summary>
    /// Copies items of a zip or shell folder into a new folder under <see cref="TempFolder"/> for the clipboard or a drag to
    /// another app, and returns the copies' paths; empty when nothing was copied.
    /// </summary>
    public static async Task<IReadOnlyList<string>> MaterializeAsync(nint owner, IReadOnlyList<string> locations, Func<ExtractLimitBreach, bool>? confirm)
    {
        var folder = Path.Combine(TempFolder, "out-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            await Task.Run(() => Directory.CreateDirectory(folder));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        if (await CopyOutAsync(owner, locations, folder, confirm) is { } error) CrashLog.Trace("copy out to temp: " + error);
        return await Task.Run(() => Directory.Exists(folder) ? Directory.GetFileSystemEntries(folder) : []);
    }

    /// <summary>Removes this session's temp folder (on exit) and earlier sessions' folders untouched for a day (at start).</summary>
    public static void CleanUpTemp(bool exiting)
    {
        if (exiting) _ = ZipTemp.Delete(TempFolder);
        else _ = ZipTemp.CleanUp(ZipTemp.Root(PolluxPaths.Current()), TempFolder, DateTime.UtcNow.AddDays(-1));
    }
}
