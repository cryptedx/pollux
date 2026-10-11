using Pollux.Core.Settings;

namespace Pollux.Core.Git;

/// <summary>
/// Seeds Trusted Folders from the Bookmarks once, on the first start after the update that introduced them
/// (ADR 0006), so git status does not silently disappear. A Bookmark is seeded when it is a repository root or one of
/// its immediate subfolders is. Never seeded: network locations, the places attacks arrive from (Downloads, Desktop,
/// Temp; passed in as <c>neverTrust</c>), anything inside them, and any folder containing them.
/// </summary>
public static class TrustedFolderSeeding
{
    /// <summary>
    /// Adds the qualifying <paramref name="bookmarks"/> to <c>files.trustedFolders</c> and records the run in
    /// <c>files.trustedFoldersSeeded</c>. Returns whether the settings changed (and should be saved); false when
    /// seeding already ran.
    /// </summary>
    public static bool RunOnce(PolluxSettings settings, IEnumerable<string> bookmarks, IEnumerable<string> neverTrust, Func<string, bool>? isNetworkDrive = null)
    {
        if (settings.Files.TrustedFoldersSeeded) return false;
        var isNetwork = isNetworkDrive ?? GitGate.IsOnNetworkDrive;
        var excluded = neverTrust.Select(p => GitGate.Canonical(p, _ => false)).OfType<string>().ToList();
        var trusted = GitGate.Sanitize(settings.Files.TrustedFolders, isNetwork);

        foreach (var bookmark in bookmarks)
        {
            if (GitGate.Canonical(bookmark, isNetwork) is not { } folder) continue;
            if (excluded.Exists(x => GitGate.IsSameOrUnder(folder, x) || GitGate.IsSameOrUnder(x, folder))) continue;
            if (trusted.Exists(t => GitGate.IsSameOrUnder(folder, t))) continue;
            if (!IsOrContainsRepository(folder, isNetwork)) continue;
            trusted.Add(folder);
        }

        settings.Files.TrustedFolders = trusted;
        settings.Files.TrustedFoldersSeeded = true;
        return true;
    }

    /// <summary>The folder or one of its immediate subfolders has a ".git"; links are not followed, so nothing remote is touched.</summary>
    private static bool IsOrContainsRepository(string folder, Func<string, bool> isNetwork)
    {
        try
        {
            var info = new DirectoryInfo(folder);
            if (!info.Exists || IsRemoteLink(info, isNetwork)) return false;
            if (HasDotGit(folder)) return true;
            var options = new EnumerationOptions { AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true, RecurseSubdirectories = false };
            return info.EnumerateDirectories("*", options).Any(child => HasDotGit(child.FullName));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException)
        {
            return false;
        }
    }

    private static bool HasDotGit(string folder)
    {
        var dotGit = Path.Combine(folder, ".git");
        return Directory.Exists(dotGit) || File.Exists(dotGit);
    }

    /// <summary>A Bookmark that is a link (junction, symbolic link) to a network location is treated as that location.</summary>
    private static bool IsRemoteLink(DirectoryInfo info, Func<string, bool> isNetwork)
    {
        if (info.LinkTarget is not { } target) return false;
        var full = Path.IsPathRooted(target) ? target : Path.Combine(info.Parent?.FullName ?? "", target);
        return GitGate.Canonical(full, isNetwork) is null;
    }
}
