using System.Globalization;

namespace Pollux.Core.Archives;

/// <summary>
/// Where files opened from inside a .zip are extracted: %LocalAppData%\Pollux\temp\&lt;session&gt;, one folder per
/// Pollux process. A process removes its own folder when it exits and, at start, folders of earlier sessions that
/// have not been touched for a day (another instance, such as the Admin Instance, may still use a newer one).
/// </summary>
public static class ZipTemp
{
    private static readonly Lazy<string> SessionId = new(() =>
        string.Create(CultureInfo.InvariantCulture, $"{Environment.ProcessId}-{DateTime.UtcNow.Ticks:x}"));

    /// <summary>The parent of all session folders.</summary>
    public static string Root(PolluxPaths paths) => Path.Combine(paths.DataDirectory, "temp");

    /// <summary>This process's folder (not created yet).</summary>
    public static string SessionFolder(PolluxPaths paths) => Path.Combine(Root(paths), SessionId.Value);

    /// <summary>Deletes the session folders below <paramref name="root"/> other than <paramref name="keep"/> last written before <paramref name="olderThan"/> (UTC). Returns how many went.</summary>
    public static int CleanUp(string root, string? keep, DateTime olderThan)
    {
        if (!Directory.Exists(root)) return 0;
        var removed = 0;
        foreach (var folder in Directory.EnumerateDirectories(root))
        {
            if (keep is not null && Path.GetFullPath(folder).Equals(Path.GetFullPath(keep), StringComparison.OrdinalIgnoreCase)) continue;
            if (Directory.GetLastWriteTimeUtc(folder) >= olderThan) continue;
            if (Delete(folder)) removed++;
        }

        return removed;
    }

    /// <summary>Deletes a session folder with everything in it (the extracted files are read-only); false when something is still open.</summary>
    public static bool Delete(string folder)
    {
        try
        {
            if (!Directory.Exists(folder)) return true;
            foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(folder, recursive: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
