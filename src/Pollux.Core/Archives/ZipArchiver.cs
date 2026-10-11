using System.IO.Compression;
using System.IO.Enumeration;
using Pollux.Core.Operations;

namespace Pollux.Core.Archives;

public sealed record ZipProgress(int DoneFiles, int TotalFiles, string CurrentFile);

/// <summary>Result of an extraction: where it went, how much, and what was left out (unsafe names, files that already existed).</summary>
public sealed record ExtractResult(string Folder, int Extracted, int SkippedUnsafe, int SkippedExisting);

/// <summary>Creates and extracts .zip archives with System.IO.Compression. Synchronous and cancellable; callers run it on a worker thread.</summary>
public static class ZipArchiver
{
    /// <summary>The archive name for a selection: the item's name for one item, the parent folder's name for several.</summary>
    public static string ArchiveNameFor(IReadOnlyList<string> sources)
    {
        var first = sources[0].TrimEnd('\\', '/');
        if (sources.Count == 1)
        {
            var name = Path.GetFileName(first);
            return (Directory.Exists(first) ? name : Path.GetFileNameWithoutExtension(name)) + ".zip";
        }

        var parent = Path.GetFileName((Path.GetDirectoryName(first) ?? "").TrimEnd('\\', '/'));
        return (parent.Length > 0 ? parent : "Archive") + ".zip";
    }

    /// <summary>Writes the sources into a new, uniquely named zip in <paramref name="targetDirectory"/> and returns its path. A cancelled run leaves nothing behind.</summary>
    public static string Compress(IReadOnlyList<string> sources, string targetDirectory, IProgress<ZipProgress>? progress = null, CancellationToken ct = default)
    {
        if (sources.Count == 0) throw new ArgumentException("Nothing to compress.", nameof(sources));
        var target = Path.Combine(targetDirectory, TransferRunner.UniqueName(targetDirectory, ArchiveNameFor(sources)));
        var partial = target + ".part";

        var items = new List<(string Path, string Name, bool IsDirectory)>();
        foreach (var source in sources)
        {
            var root = source.TrimEnd('\\', '/');
            var baseName = Path.GetFileName(root);
            if (Directory.Exists(root))
            {
                items.Add((root, baseName + "/", true));
                foreach (var (f, isDir) in Walk(root, ct))
                {
                    var rel = Path.GetRelativePath(root, f).Replace('\\', '/');
                    items.Add((f, baseName + "/" + rel + (isDir ? "/" : ""), isDir));
                }
            }
            else
            {
                items.Add((root, baseName, false));
            }
        }

        var total = items.Count(i => !i.IsDirectory);
        var done = 0;
        try
        {
            using (var stream = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                foreach (var (path, name, isDirectory) in items)
                {
                    ct.ThrowIfCancellationRequested();
                    if (isDirectory)
                    {
                        var dir = zip.CreateEntry(name);
                        dir.LastWriteTime = Clamp(Directory.GetLastWriteTime(path));
                        continue;
                    }

                    progress?.Report(new ZipProgress(done, total, Path.GetFileName(path)));
                    var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
                    entry.LastWriteTime = Clamp(File.GetLastWriteTime(path));
                    using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var output = entry.Open();
                    CopyCancellable(input, output, ct);
                    done++;
                }
            }

            File.Move(partial, target);
            progress?.Report(new ZipProgress(total, total, ""));
            return target;
        }
        catch
        {
            try { File.Delete(partial); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    /// <summary>
    /// Everything below <paramref name="root"/> that goes into an archive. Junctions and directory symlinks are neither
    /// entered nor stored (they can loop or point to another drive, like everywhere else in Pollux); what cannot be read is left out.
    /// </summary>
    internal static List<(string Path, bool IsDirectory)> Walk(string root, CancellationToken ct = default)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = 0,
            ReturnSpecialDirectories = false,
        };
        var entries = new FileSystemEnumerable<(string Path, bool IsDirectory)>(root, (ref FileSystemEntry e) =>
        {
            ct.ThrowIfCancellationRequested();
            return (e.ToFullPath(), e.IsDirectory);
        }, options)
        {
            ShouldIncludePredicate = static (ref FileSystemEntry e) => !IsDirectoryLink(ref e),
            ShouldRecursePredicate = static (ref FileSystemEntry e) => !IsDirectoryLink(ref e),
        };
        return [.. entries];
    }

    private static bool IsDirectoryLink(ref FileSystemEntry e) => e.IsDirectory && e.Attributes.HasFlag(FileAttributes.ReparsePoint);

    /// <summary>The folder an archive is extracted into: a new sibling folder named after it, or the archive's own folder for "here".</summary>
    public static string ExtractTargetFor(string zipPath, bool here)
    {
        var folder = Path.GetDirectoryName(zipPath) ?? "";
        if (here) return folder;
        return Path.Combine(folder, TransferRunner.UniqueName(folder, Path.GetFileNameWithoutExtension(zipPath), isDirectory: true));
    }

    /// <summary>
    /// Extracts every entry into <paramref name="targetDirectory"/>. Entries that would land outside it, or pass through a
    /// junction/symlink below it, are skipped; existing files are never overwritten. Each extracted file gets the archive's
    /// zone mark. Before writing anything the archive is checked against <paramref name="limits"/> (default
    /// <see cref="ExtractLimits.Default"/>); a breach goes to <paramref name="confirm"/>, and without a yes nothing is
    /// written and <see cref="ExtractLimitExceededException"/> is thrown. <paramref name="confirm"/> runs on the calling thread.
    /// </summary>
    public static ExtractResult Extract(string zipPath, string targetDirectory, IProgress<ZipProgress>? progress = null,
        Func<ExtractLimitBreach, bool>? confirm = null, ExtractLimits? limits = null, CancellationToken ct = default) =>
        ExtractCore(zipPath, targetDirectory, static name => name, progress, confirm, limits, ct);

    /// <summary>
    /// Copies selected entries (files or whole folders, by their path inside the archive) out of the archive.
    /// Each selection lands directly in the target, keeping its own name. Not shipped yet: the app has no zip browsing to pick entries from.
    /// </summary>
    public static ExtractResult ExtractSelection(string zipPath, IReadOnlyList<string> innerPaths, string targetDirectory, IProgress<ZipProgress>? progress = null,
        Func<ExtractLimitBreach, bool>? confirm = null, ExtractLimits? limits = null, CancellationToken ct = default)
    {
        var roots = innerPaths.Select(p => p.Trim('/', '\\').Replace('\\', '/')).Where(p => p.Length > 0).ToList();
        return ExtractCore(zipPath, targetDirectory, name =>
        {
            foreach (var r in roots)
            {
                if (name.Equals(r, StringComparison.OrdinalIgnoreCase) || name.StartsWith(r + "/", StringComparison.OrdinalIgnoreCase))
                {
                    var slash = r.LastIndexOf('/');
                    return slash < 0 ? name : name[(slash + 1)..];
                }
            }

            return null;
        }, progress, confirm, limits, ct);
    }

    /// <summary>True when <paramref name="candidate"/> is <paramref name="root"/> itself or lies below it.</summary>
    public static bool IsInside(string root, string candidate)
    {
        var r = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var c = Path.GetFullPath(candidate);
        return c.Equals(r, StringComparison.OrdinalIgnoreCase) || c.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static ExtractResult ExtractCore(string zipPath, string targetDirectory, Func<string, string?> map, IProgress<ZipProgress>? progress,
        Func<ExtractLimitBreach, bool>? confirm, ExtractLimits? limits, CancellationToken ct)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var wanted = new List<(ZipArchiveEntry Entry, string Relative)>();
        foreach (var e in zip.Entries)
        {
            // Normalize before mapping so a backslash name cannot hide a ".." on Windows.
            var name = e.FullName.Replace('\\', '/');
            if (map(name) is { } relative) wanted.Add((e, relative));
        }

        var files = wanted.Where(w => !w.Relative.EndsWith('/')).Select(w => w.Entry).ToList();
        if (CheckLimits(zipPath, targetDirectory, files, limits ?? ExtractLimits.Default) is { } breach && confirm?.Invoke(breach) != true)
        {
            throw new ExtractLimitExceededException(breach);
        }

        // Read once: every extracted file carries the archive's mark (S-10), so extracting doesn't launder a download.
        var zoneMark = ZoneMark.Read(zipPath);
        Directory.CreateDirectory(targetDirectory);
        var total = files.Count;
        var done = 0;
        var extracted = 0;
        var unsafeCount = 0;
        var existing = 0;
        foreach (var (entry, relative) in wanted)
        {
            ct.ThrowIfCancellationRequested();
            var isDirectory = relative.EndsWith('/');
            var destination = Path.GetFullPath(Path.Combine(targetDirectory, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (relative.StartsWith('/') || Path.IsPathRooted(relative) || !IsInside(targetDirectory, destination)
                || PassesThroughLink(targetDirectory, destination))
            {
                unsafeCount++;
                continue;
            }

            if (isDirectory)
            {
                Directory.CreateDirectory(destination);
                continue;
            }

            progress?.Report(new ZipProgress(done, total, entry.Name));
            if (File.Exists(destination))
            {
                existing++;
                done++;
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            try
            {
                using (var input = entry.Open())
                using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    // The limits were checked against the declared size; never write more than that.
                    CopyCancellable(input, output, ct, entry.Length);
                }

                if (zoneMark is not null) ZoneMark.Write(destination, zoneMark);
                File.SetLastWriteTime(destination, entry.LastWriteTime.LocalDateTime);
            }
            catch
            {
                try { File.Delete(destination); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                throw;
            }

            extracted++;
            done++;
        }

        progress?.Report(new ZipProgress(total, total, ""));
        return new ExtractResult(targetDirectory, extracted, unsafeCount, existing);
    }

    private static void CopyCancellable(Stream input, Stream output, CancellationToken ct, long maxBytes = long.MaxValue)
    {
        var buffer = new byte[81920];
        long written = 0;
        int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            written += read;
            if (written > maxBytes) throw new InvalidDataException("A zip entry is larger than its declared size.");
            output.Write(buffer, 0, read);
        }
    }

    /// <summary>The broken limits for extracting <paramref name="files"/> into <paramref name="targetDirectory"/>, or null when all hold.</summary>
    private static ExtractLimitBreach? CheckLimits(string zipPath, string targetDirectory, List<ZipArchiveEntry> files, ExtractLimits limits)
    {
        long totalBytes = 0;
        string? worstEntry = null;
        double worstRatio = 0;
        foreach (var e in files)
        {
            totalBytes = SaturatingAdd(totalBytes, e.Length);
            if (e.Length < limits.RatioMinBytes) continue;
            var ratio = (double)e.Length / Math.Max(e.CompressedLength, 1);
            if (ratio > limits.MaxRatio && ratio > worstRatio)
            {
                worstRatio = ratio;
                worstEntry = e.FullName;
            }
        }

        var free = FreeSpace(targetDirectory);
        var overCeiling = totalBytes > limits.MaxTotalBytes;
        var overFree = free is { } f && SaturatingAdd(totalBytes, limits.FreeSpaceMargin) > f;
        return overCeiling || overFree || worstEntry is not null
            ? new ExtractLimitBreach(zipPath, totalBytes, free, overCeiling, overFree, worstEntry, worstRatio)
            : null;
    }

    private static long SaturatingAdd(long a, long b) => a > long.MaxValue - b ? long.MaxValue : a + b;

    /// <summary>Free bytes on the drive that holds <paramref name="directory"/> (or its nearest existing parent); null when unknown.</summary>
    private static long? FreeSpace(string directory)
    {
        var dir = Path.GetFullPath(directory);
        while (!Directory.Exists(dir))
        {
            var parent = Path.GetDirectoryName(dir);
            if (parent is null) return null;
            dir = parent;
        }

        try
        {
            return new DriveInfo(dir).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// True when a folder between <paramref name="root"/> (not included: the user picked it) and <paramref name="path"/>
    /// (included) already exists as a junction or symlink, so writing there would land wherever the link points (S-19).
    /// </summary>
    private static bool PassesThroughLink(string root, string path)
    {
        var current = Path.GetFullPath(root);
        var relative = Path.GetRelativePath(current, path);
        if (relative == ".") return false;
        foreach (var part in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(current);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                return false; // nothing below a missing folder exists yet
            }

            if (attributes.HasFlag(FileAttributes.ReparsePoint)) return true;
        }

        return false;
    }

    // Zip stores dates from 1980 to 2107; anything else makes the entry throw.
    private static DateTimeOffset Clamp(DateTime t) =>
        t < new DateTime(1980, 1, 1) ? new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero)
        : t > new DateTime(2107, 1, 1) ? new DateTimeOffset(2107, 1, 1, 0, 0, 0, TimeSpan.Zero)
        : new DateTimeOffset(t);
}
