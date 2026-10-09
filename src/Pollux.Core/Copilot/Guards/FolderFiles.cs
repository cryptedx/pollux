namespace Pollux.Core.Copilot.Guards;

/// <summary>An entry below a searched folder: its path relative to the folder (backslashes), and whether it is a folder or a link (junction, symbolic link).</summary>
public readonly record struct FolderEntry(string Path, bool IsFolder, bool IsLink)
{
    public static FolderEntry File(string path) => new(path, false, false);

    public static FolderEntry Folder(string path) => new(path, true, false);

    /// <summary>A junction or symbolic link (to a file or folder); it is not entered.</summary>
    public static FolderEntry Link(string path) => new(path, false, true);
}

/// <summary>
/// The Guard Rules' view of the file system (tests pass a fake): the files below a folder, for the check of a recursive
/// search (does it reach protected files?), and where a path really points (links resolved), so a junction such as
/// C:\work\keys -> ~\.ssh does not lead around the protected-path checks.
/// </summary>
public interface IFolderFiles
{
    /// <summary>
    /// Every entry below <paramref name="folder"/> (an absolute path), lazily (the caller stops early). Links are listed
    /// but not entered; .git folders are skipped. Nothing when the folder does not exist or is not a folder.
    /// </summary>
    IEnumerable<FolderEntry> Below(string folder);

    /// <summary>
    /// <paramref name="path"/> (absolute) with every junction and symbolic link in it resolved; for a path that does not
    /// exist (yet), its nearest existing parent is resolved and the rest appended. Null when the links cannot be resolved
    /// (loop, access denied, I/O error): the caller fails closed.
    /// </summary>
    string? Canonical(string path);
}

/// <summary>The real file system: hidden and system files included, inaccessible folders skipped, links listed but not entered (no loops).</summary>
public sealed class FileSystemFolderFiles : IFolderFiles
{
    /// <summary>At most this many links are followed while resolving one path (beyond: a loop, fail closed).</summary>
    private const int MaxLinks = 40;

    public static FileSystemFolderFiles Instance { get; } = new();

    private static readonly EnumerationOptions Options = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = true,
        AttributesToSkip = 0,
        ReturnSpecialDirectories = false,
    };

    public IEnumerable<FolderEntry> Below(string folder)
    {
        if (!Directory.Exists(folder))
        {
            yield break;
        }

        var root = Path.GetFullPath(folder);
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out var current))
        {
            IEnumerator<FileSystemInfo>? entries = null;
            try
            {
                entries = new DirectoryInfo(current).EnumerateFileSystemInfos("*", Options).GetEnumerator();
            }
            catch (Exception e) when (IsAccessError(e))
            {
            }

            using (entries)
            {
                while (entries is not null && Next(entries) is { } entry)
                {
                    var relative = Path.GetRelativePath(root, entry.FullName).Replace('/', '\\');
                    if (entry is DirectoryInfo { Name: var name } && name.Equals(".git", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (IsLink(entry))
                    {
                        yield return FolderEntry.Link(relative);
                    }
                    else if (entry is DirectoryInfo dir)
                    {
                        if (!dir.Attributes.HasFlag(FileAttributes.ReparsePoint))
                        {
                            pending.Push(dir.FullName);
                        }

                        yield return FolderEntry.Folder(relative);
                    }
                    else
                    {
                        yield return FolderEntry.File(relative);
                    }
                }
            }
        }
    }

    public string? Canonical(string path)
    {
        // A path this machine cannot have (a Windows path on macOS in tests) has no links to resolve.
        if (!Path.IsPathFullyQualified(path))
        {
            return path;
        }

        try
        {
            // GetFullPath also expands 8.3 short names (C:\Users\me\SSH~1) of existing paths on Windows.
            var current = Path.GetFullPath(path);
            for (var links = 0; links <= MaxLinks; links++)
            {
                if (FirstLink(current) is not { } resolved)
                {
                    return current;
                }

                current = resolved;
            }

            return null;
        }
        catch (Exception e) when (IsAccessError(e) || e is ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>The path with its first link (from the root down) replaced by the link's target; null when no existing part is a link.</summary>
    private static string? FirstLink(string path)
    {
        var root = Path.GetPathRoot(path)!;
        var names = path[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        var current = root;
        for (var i = 0; i < names.Length; i++)
        {
            var next = Path.Combine(current, names[i]);
            FileSystemInfo info = new FileInfo(next);
            if ((int)info.Attributes == -1)
            {
                return null; // Does not exist: the rest cannot contain links.
            }

            if (info.Attributes.HasFlag(FileAttributes.Directory))
            {
                info = new DirectoryInfo(next);
            }

            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint) && info.LinkTarget is { } target)
            {
                var resolved = Path.GetFullPath(StripDevicePrefix(target), current);
                return Path.Combine([resolved, .. names[(i + 1)..]]);
            }

            current = next;
        }

        return null;
    }

    /// <summary>Junction targets may come as \\?\C:\x or \??\C:\x (and \\?\UNC\server\share).</summary>
    private static string StripDevicePrefix(string target)
    {
        foreach (var unc in (string[])[@"\\?\UNC\", @"\??\UNC\"])
        {
            if (target.StartsWith(unc, StringComparison.OrdinalIgnoreCase)) return @"\\" + target[unc.Length..];
        }

        foreach (var prefix in (string[])[@"\\?\", @"\??\"])
        {
            if (target.StartsWith(prefix, StringComparison.Ordinal)) return target[prefix.Length..];
        }

        return target;
    }

    /// <summary>A junction or symbolic link (other reparse points, such as cloud placeholders, are not links).</summary>
    private static bool IsLink(FileSystemInfo info) => info.Attributes.HasFlag(FileAttributes.ReparsePoint) && info.LinkTarget is not null;

    private static FileSystemInfo? Next(IEnumerator<FileSystemInfo> entries)
    {
        try
        {
            return entries.MoveNext() ? entries.Current : null;
        }
        catch (Exception e) when (IsAccessError(e))
        {
            return null;
        }
    }

    private static bool IsAccessError(Exception e) => e is IOException or UnauthorizedAccessException or System.Security.SecurityException;
}
