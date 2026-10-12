using System.IO.Compression;
using Pollux.Core.Files;

namespace Pollux.Core.Archives;

/// <summary>One entry of an archive as stored (name with forward slashes, directories end in '/').</summary>
public sealed record ZipEntryInfo(string FullName, long Length, DateTime Modified);

/// <summary>Turns the flat entry list of a .zip into folder views, so an archive can be browsed read-only like a directory.
/// <see cref="ZipFolder"/> lists zip folders in a Tab through it.</summary>
public static class ZipListing
{
    public static IReadOnlyList<ZipEntryInfo> ReadEntries(string zipPath)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        return zip.Entries.Select(e => new ZipEntryInfo(e.FullName, e.Length, e.LastWriteTime.LocalDateTime)).ToList();
    }

    /// <summary>
    /// The direct children of <paramref name="inner"/> ("" = archive root). Folders that only exist implicitly
    /// (an entry "a/b.txt" without an "a/" entry) are synthesized. Ordering is left to the caller's sorter.
    /// </summary>
    public static IReadOnlyList<FileEntry> Children(string archivePath, IEnumerable<ZipEntryInfo> entries, string inner)
    {
        inner = inner.Trim('/');
        var prefix = inner.Length == 0 ? "" : inner + "/";
        var folders = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        var files = new Dictionary<string, FileEntry>(StringComparer.OrdinalIgnoreCase);

        foreach (var e in entries)
        {
            var name = e.FullName.Replace('\\', '/').TrimStart('/');
            if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || name.Length == prefix.Length) continue;
            var rest = name[prefix.Length..];
            var slash = rest.IndexOf('/');
            if (slash >= 0)
            {
                var folder = rest[..slash];
                if (folder.Length == 0) continue;
                if (!folders.TryGetValue(folder, out var seen) || e.Modified > seen) folders[folder] = e.Modified;
            }
            else
            {
                files[rest] = new FileEntry(rest, ZipPath.Combine(archivePath, prefix + rest), false, e.Length, e.Modified, FileAttributes.Normal | FileAttributes.ReadOnly);
            }
        }

        var result = new List<FileEntry>(folders.Count + files.Count);
        foreach (var (name, modified) in folders)
        {
            result.Add(new FileEntry(name, ZipPath.Combine(archivePath, prefix + name), true, 0, modified, FileAttributes.Directory | FileAttributes.ReadOnly));
        }

        result.AddRange(files.Values.Where(f => !folders.ContainsKey(f.Name)));
        return result;
    }
}
