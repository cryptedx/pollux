using System.Buffers.Binary;
using System.Text;

namespace Pollux.Core.Operations;

/// <summary>One entry of a Recycle Bin folder: its "$I" info file, its "$R" data (file or folder) and where it came from.</summary>
public sealed record RecycledEntry(string InfoFile, string DataFile, string OriginalPath, DateTime DeletedUtc);

/// <summary>
/// Reads and restores entries of a Windows Recycle Bin folder ("X:\$Recycle.Bin\&lt;user SID&gt;"), the way Explorer's
/// "Restore" does: the data "$R…" goes back to its original path and the info "$I…" is removed. Which folder belongs to
/// which drive is the caller's business, so this works on any folder (and is tested on any OS).
/// </summary>
public static class RecycleBin
{
    /// <summary>Clock slack between the caller's "since" and the deletion time Windows writes.</summary>
    private static readonly TimeSpan Slack = TimeSpan.FromSeconds(2);

    private static readonly EnumerationOptions InfoFiles = new() { AttributesToSkip = 0, IgnoreInaccessible = true, MatchType = MatchType.Simple };

    /// <summary>
    /// The original path and deletion time from a "$I" file: version 1 (Vista to 8: path in 520 bytes) or version 2
    /// (Windows 10+: path length, then the path); null for anything else.
    /// </summary>
    public static (string OriginalPath, DateTime DeletedUtc)? ParseInfo(ReadOnlySpan<byte> info)
    {
        if (info.Length < 24) return null;
        var version = BinaryPrimitives.ReadInt64LittleEndian(info);
        ReadOnlySpan<byte> name;
        if (version == 1)
        {
            name = info[24..];
        }
        else if (version == 2 && info.Length >= 28)
        {
            var chars = BinaryPrimitives.ReadInt32LittleEndian(info[24..]);
            if (chars <= 0 || 28 + (long)chars * 2 > info.Length) return null;
            name = info.Slice(28, chars * 2);
        }
        else
        {
            return null;
        }

        var path = Encoding.Unicode.GetString(name);
        var end = path.IndexOf('\0', StringComparison.Ordinal);
        if (end >= 0) path = path[..end];
        var fileTime = BinaryPrimitives.ReadInt64LittleEndian(info[16..]);
        if (path.Length == 0 || fileTime < 0) return null;
        return (path, DateTime.FromFileTimeUtc(fileTime));
    }

    /// <summary>The entries of <paramref name="folder"/> deleted at or after <paramref name="sinceUtc"/> (older info files are not read).</summary>
    public static IReadOnlyList<RecycledEntry> Read(string folder, DateTime sinceUtc)
    {
        var entries = new List<RecycledEntry>();
        if (!Directory.Exists(folder)) return entries;
        var after = sinceUtc == DateTime.MinValue ? DateTime.MinValue : sinceUtc - Slack;
        try
        {
            foreach (var info in new DirectoryInfo(folder).EnumerateFiles("$I*", InfoFiles))
            {
                if (info.LastWriteTimeUtc < after) continue;
                byte[] bytes;
                try
                {
                    bytes = File.ReadAllBytes(info.FullName);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                if (ParseInfo(bytes) is not { } parsed || parsed.DeletedUtc < after) continue;
                var data = Path.Combine(folder, "$R" + info.Name[2..]);
                entries.Add(new RecycledEntry(info.FullName, data, parsed.OriginalPath, parsed.DeletedUtc));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return entries;
    }

    /// <summary>
    /// The entry that was <paramref name="originalPath"/> and deleted first at or after <paramref name="sinceUtc"/> (the
    /// deletion Pollux made, not a later one of the same path); null when there is none.
    /// </summary>
    public static RecycledEntry? Find(IEnumerable<RecycledEntry> entries, string originalPath, DateTime sinceUtc)
    {
        var path = Trim(originalPath);
        return entries
            .Where(e => e.DeletedUtc >= sinceUtc - Slack && string.Equals(Trim(e.OriginalPath), path, StringComparison.OrdinalIgnoreCase))
            .Where(e => File.Exists(e.DataFile) || Directory.Exists(e.DataFile))
            .MinBy(e => e.DeletedUtc);
    }

    /// <summary>Moves the entry back to its original path (creating missing parent folders) and removes its info file.</summary>
    public static void Restore(RecycledEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (Path.GetDirectoryName(entry.OriginalPath) is { Length: > 0 } parent) Directory.CreateDirectory(parent);
        if (Directory.Exists(entry.DataFile)) Directory.Move(entry.DataFile, entry.OriginalPath);
        else File.Move(entry.DataFile, entry.OriginalPath);
        File.Delete(entry.InfoFile);
    }

    private static string Trim(string path) => path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
