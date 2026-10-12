using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Pollux.Core.Files;

namespace Pollux.Core.Archives;

/// <summary>
/// A .zip browsed as a read-only folder ("C:\a.zip\docs"): lists one level, opens a file by extracting it to a temp
/// folder, copies entries out by extracting them. Writing into an archive is not supported. Entry names that could
/// escape a folder (zip slip: "..", rooted, drive-qualified) are never listed. Keeps the entry lists of the last few
/// archives (checked against size and time), so walking around in a big archive does not reread it. Thread-safe.
/// </summary>
public sealed class ZipFolder
{
    private const int CachedArchives = 4;
    private readonly object _gate = new();
    private readonly LinkedList<(string Archive, long Length, DateTime Written, IReadOnlyList<ZipEntryInfo> Entries)> _cache = new();

    /// <summary>
    /// Splits a location at its first segment ending in ".zip" into the archive and the folder inside it ("" = its root,
    /// forward slashes). Unlike <see cref="ZipPath.TryParse"/> the archive keeps the separators as written.
    /// </summary>
    public static bool TryParse(string location, out string archive, out string inner)
    {
        archive = inner = "";
        var start = 0;
        while (start < location.Length)
        {
            var end = location.IndexOfAny(['\\', '/'], start);
            if (end < 0) end = location.Length;
            if (end - start > 4 && location.AsSpan(start, end - start).EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                archive = location[..end];
                inner = string.Join('/', location[Math.Min(end + 1, location.Length)..].Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries));
                return true;
            }

            start = end + 1;
        }

        return false;
    }

    /// <summary>True for a relative entry name whose every part is a plain name: no "..", ".", empty part, drive or root.</summary>
    public static bool IsSafeEntryName(string name)
    {
        var n = name.Replace('\\', '/');
        if (n.Length == 0 || n[0] == '/' || n.Contains(':', StringComparison.Ordinal)) return false;
        var parts = n.TrimEnd('/').Split('/');
        return parts.All(p => p.Length > 0 && p != "." && p != "..");
    }

    /// <summary>The folder at <paramref name="location"/> (a path through a .zip). Throws <see cref="DirectoryNotFoundException"/> when the archive has no such folder.</summary>
    public IReadOnlyList<FileEntry> Children(string location)
    {
        if (!TryParse(location, out var archive, out var inner)) throw new DirectoryNotFoundException("Not a path inside a .zip: " + location);
        return Children(archive, inner);
    }

    public IReadOnlyList<FileEntry> Children(string archive, string inner)
    {
        var entries = Entries(archive);
        inner = inner.Trim('/');
        if (inner.Length > 0 && !entries.Any(e => e.FullName.StartsWith(inner + "/", StringComparison.OrdinalIgnoreCase)))
        {
            throw new DirectoryNotFoundException($"\"{inner}\" is not a folder in {Path.GetFileName(archive)}.");
        }

        return ZipListing.Children(archive, entries, inner);
    }

    /// <summary>The archive's safe entries (names with forward slashes), from the cache when the file is unchanged.</summary>
    public IReadOnlyList<ZipEntryInfo> Entries(string archive)
    {
        var info = new FileInfo(archive);
        if (!info.Exists) throw new FileNotFoundException("Archive not found.", archive);
        var key = info.FullName;
        lock (_gate)
        {
            for (var node = _cache.First; node is not null; node = node.Next)
            {
                if (node.Value.Archive.Equals(key, StringComparison.OrdinalIgnoreCase) && node.Value.Length == info.Length && node.Value.Written == info.LastWriteTimeUtc)
                {
                    _cache.Remove(node);
                    _cache.AddFirst(node);
                    return node.Value.Entries;
                }
            }
        }

        IReadOnlyList<ZipEntryInfo> entries;
        try
        {
            entries = [.. ZipListing.ReadEntries(archive)
                .Select(e => e with { FullName = e.FullName.Replace('\\', '/') })
                .Where(e => IsSafeEntryName(e.FullName))];
        }
        catch (InvalidDataException ex)
        {
            throw new IOException($"{Path.GetFileName(archive)} is not a readable .zip archive: {ex.Message}", ex);
        }

        lock (_gate)
        {
            _cache.AddFirst((key, info.Length, info.LastWriteTimeUtc, entries));
            while (_cache.Count > CachedArchives) _cache.RemoveLast();
        }

        return entries;
    }

    /// <summary>
    /// Extracts the file at <paramref name="entryLocation"/> into a folder of its own below <paramref name="tempFolder"/>
    /// (see <see cref="ZipTemp"/>) and returns its path, marked read-only: changes to it do not go back into the archive.
    /// The same entry of an unchanged archive is extracted once. Limit breaches go to <paramref name="confirm"/> as in
    /// <see cref="ZipArchiver.Extract"/>.
    /// </summary>
    public string ExtractForOpen(string entryLocation, string tempFolder, Func<ExtractLimitBreach, bool>? confirm = null, CancellationToken ct = default)
    {
        if (!TryParse(entryLocation, out var archive, out var inner) || inner.Length == 0) throw new FileNotFoundException("Not a file inside a .zip.", entryLocation);
        var entry = Entries(archive).FirstOrDefault(e => e.FullName.Equals(inner, StringComparison.OrdinalIgnoreCase))
            ?? throw new FileNotFoundException($"\"{inner}\" is not a file in {Path.GetFileName(archive)}.", entryLocation);
        var info = new FileInfo(archive);
        var stamp = string.Create(CultureInfo.InvariantCulture, $"{info.FullName.ToUpperInvariant()}|{entry.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}");
        var folder = Path.Combine(tempFolder, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(stamp)))[..16]);
        var target = Path.Combine(folder, entry.FullName[(entry.FullName.LastIndexOf('/') + 1)..]);
        if (File.Exists(target)) return target;

        var result = ZipArchiver.ExtractSelection(archive, [entry.FullName], folder, confirm: confirm, ct: ct);
        if (result.Extracted == 0 || !File.Exists(target)) throw new IOException($"Could not extract \"{inner}\".");
        File.SetAttributes(target, File.GetAttributes(target) | FileAttributes.ReadOnly);
        return target;
    }

    /// <summary>
    /// Copies entries (files or folders, as locations inside one archive) into <paramref name="targetDirectory"/>, each
    /// keeping its name. Existing files are not overwritten (counted in the result).
    /// </summary>
    public static ExtractResult CopyOut(IReadOnlyList<string> entryLocations, string targetDirectory, IProgress<ZipProgress>? progress = null,
        Func<ExtractLimitBreach, bool>? confirm = null, CancellationToken ct = default)
    {
        string? archive = null;
        var inner = new List<string>(entryLocations.Count);
        foreach (var location in entryLocations)
        {
            if (!TryParse(location, out var a, out var i) || i.Length == 0) throw new ArgumentException("Not an entry inside a .zip: " + location, nameof(entryLocations));
            if (archive is not null && !archive.Equals(a, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Entries from more than one archive.", nameof(entryLocations));
            archive = a;
            inner.Add(i);
        }

        if (archive is null) return new ExtractResult(targetDirectory, 0, 0, 0);
        return ZipArchiver.ExtractSelection(archive, inner, targetDirectory, progress, confirm, ct: ct);
    }
}
