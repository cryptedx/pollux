using System.IO.Enumeration;
using Pollux.Core.Files;

namespace Pollux.Core.Compare;

/// <summary>
/// Compares two folders by name, size and modified time. Names are matched case-insensitively (Windows). Nothing is
/// ever changed; the result only says which side has what.
/// </summary>
public static class FolderComparer
{
    /// <summary>Reads both folders (a folder that cannot be read counts as empty) and compares them; blocking, so call it off the UI thread.</summary>
    public static FolderComparison Compare(string leftFolder, string rightFolder, CompareOptions options, CancellationToken cancellationToken = default)
    {
        var left = Scan(leftFolder, options, cancellationToken);
        var right = Scan(rightFolder, options, cancellationToken);
        return Compare(leftFolder, rightFolder, left, right, options, FileContent.AreEqual, cancellationToken);
    }

    /// <summary>
    /// Compares ready-made item lists. <paramref name="sameContent"/> decides same-size files when
    /// <see cref="CompareOptions.CompareContent"/> is on.
    /// </summary>
    public static FolderComparison Compare(
        string leftFolder,
        string rightFolder,
        IEnumerable<CompareItem> left,
        IEnumerable<CompareItem> right,
        CompareOptions options,
        Func<FileEntry, FileEntry, bool>? sameContent = null,
        CancellationToken cancellationToken = default)
    {
        var l = Index(left);
        var r = Index(right);
        var entries = new Dictionary<string, CompareEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in l.Keys.Union(r.Keys, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            l.TryGetValue(path, out var le);
            r.TryGetValue(path, out var re);
            entries[path] = new CompareEntry(path, le, re, StatusOf(le, re, options, sameContent));
        }

        if (options.Recursive) SpreadDifferencesToFolders(entries);

        var ordered = entries.Values.OrderBy(e => e.RelativePath, StringComparer.OrdinalIgnoreCase).ToList();
        return new FolderComparison(leftFolder, rightFolder, options, ordered);
    }

    // A folder where two names differ only by case (possible on NTFS) keeps the first of them.
    private static Dictionary<string, FileEntry> Index(IEnumerable<CompareItem> items)
    {
        var map = new Dictionary<string, FileEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var i in items) map.TryAdd(i.RelativePath, i.Entry);
        return map;
    }

    /// <summary>Every entry below <paramref name="folder"/> (only the direct ones unless recursive), skipping in-flight Transfer files.</summary>
    public static List<CompareItem> Scan(string folder, CompareOptions options, CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var enumeration = new EnumerationOptions
        {
            AttributesToSkip = 0,
            IgnoreInaccessible = true,
            RecurseSubdirectories = options.Recursive,
            ReturnSpecialDirectories = false,
        };
        try
        {
            var items = new FileSystemEnumerable<CompareItem>(folder, (ref FileSystemEntry e) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relative = e.ToFullPath()[root.Length..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return new CompareItem(relative, DirectoryReader.Transform(ref e));
            }, enumeration)
            {
                ShouldIncludePredicate = (ref FileSystemEntry e) => IsListed(ref e, options),
                // Junctions and symlinks can loop back; they are compared as entries but not entered.
                ShouldRecursePredicate = (ref FileSystemEntry e) => IsListed(ref e, options) && !e.Attributes.HasFlag(FileAttributes.ReparsePoint),
            };
            return [.. items];
        }
        catch (Exception ex) when (ex is DirectoryNotFoundException or UnauthorizedAccessException or IOException)
        {
            return [];
        }
    }

    private static bool IsListed(ref FileSystemEntry e, CompareOptions options) =>
        (options.IncludeHidden || !e.Attributes.HasFlag(FileAttributes.Hidden))
        && !e.FileName.EndsWith(FolderProjection.PartSuffix, StringComparison.OrdinalIgnoreCase);

    private static CompareStatus StatusOf(FileEntry? left, FileEntry? right, CompareOptions options, Func<FileEntry, FileEntry, bool>? sameContent)
    {
        if (left is null) return CompareStatus.OnlyRight;
        if (right is null) return CompareStatus.OnlyLeft;
        if (left.IsDirectory != right.IsDirectory) return CompareStatus.Different;
        // Folders in both: nothing to see in a flat comparison; a recursive one revises this from what is below.
        if (left.IsDirectory) return CompareStatus.Same;

        var delta = left.Modified - right.Modified;
        var sameTime = delta.Duration() <= options.Tolerance;
        if (left.Size == right.Size)
        {
            // A cloud-only placeholder is never read just to compare (that would download it): size and time decide.
            if (options.CompareContent && sameContent is not null && left.Cloud != CloudState.CloudOnly && right.Cloud != CloudState.CloudOnly)
            {
                if (sameContent(left, right)) return CompareStatus.Same;
            }
            else if (sameTime)
            {
                return CompareStatus.Same;
            }
        }

        if (sameTime) return CompareStatus.Different;
        return delta > TimeSpan.Zero ? CompareStatus.LeftNewer : CompareStatus.RightNewer;
    }

    /// <summary>A folder in both counts as different as soon as anything below it is.</summary>
    private static void SpreadDifferencesToFolders(Dictionary<string, CompareEntry> entries)
    {
        foreach (var e in entries.Values.Where(e => e.Status != CompareStatus.Same).ToList())
        {
            var path = e.RelativePath;
            while (Path.GetDirectoryName(path) is { Length: > 0 } parent)
            {
                path = parent;
                if (entries.TryGetValue(parent, out var folder) && folder.Status == CompareStatus.Same)
                {
                    entries[parent] = folder with { Status = CompareStatus.Different };
                }
            }
        }
    }
}

/// <summary>Reading two files side by side.</summary>
public static class FileContent
{
    private const int BufferSize = 81920;

    /// <summary>True when both files have the same bytes; unreadable files count as different. Stops at the first difference.</summary>
    public static bool AreEqual(FileEntry left, FileEntry right) => AreEqual(left.FullPath, right.FullPath);

    public static bool AreEqual(string leftPath, string rightPath)
    {
        try
        {
            using var a = new FileStream(leftPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, BufferSize);
            using var b = new FileStream(rightPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, BufferSize);
            if (a.Length != b.Length) return false;
            var bufferA = new byte[BufferSize];
            var bufferB = new byte[BufferSize];
            while (true)
            {
                var n = a.ReadAtLeast(bufferA, bufferA.Length, throwOnEndOfStream: false);
                var m = b.ReadAtLeast(bufferB, bufferB.Length, throwOnEndOfStream: false);
                if (n != m || !bufferA.AsSpan(0, n).SequenceEqual(bufferB.AsSpan(0, m))) return false;
                if (n < bufferA.Length) return true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
