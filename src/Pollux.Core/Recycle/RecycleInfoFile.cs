using System.Buffers.Binary;
using System.Text;

namespace Pollux.Core.Recycle;

/// <summary>
/// Reads the "$I…" file Windows writes next to every recycled "$R…" entry: original path, size and deletion time.
/// Version 1 (Vista–8.1) holds the path in a fixed 260-char buffer, version 2 (Windows 10+) prefixes it with its length.
/// </summary>
public static class RecycleInfoFile
{
    private const int HeaderSize = 24;
    private const int Version1PathChars = 260;

    /// <summary>"…\$RAB12CD.txt" → "…\$IAB12CD.txt"; null when the name is not one of a recycled entry.</summary>
    public static string? InfoPathFor(string recycledPath)
    {
        ArgumentNullException.ThrowIfNull(recycledPath);
        var slash = recycledPath.LastIndexOf('\\');
        var name = recycledPath[(slash + 1)..];
        if (name.Length <= 2 || !name.StartsWith("$R", StringComparison.Ordinal)) return null;
        return recycledPath[..(slash + 1)] + "$I" + name[2..];
    }

    /// <summary>The entry described by <paramref name="data"/> (the content of its $I file), or null when it is not a valid one.</summary>
    public static RecycledItem? Parse(ReadOnlySpan<byte> data, string recycledPath, bool isDirectory)
    {
        if (data.Length < HeaderSize) return null;
        var version = BinaryPrimitives.ReadInt64LittleEndian(data);
        var size = BinaryPrimitives.ReadInt64LittleEndian(data[8..]);
        var fileTime = BinaryPrimitives.ReadInt64LittleEndian(data[16..]);
        if (fileTime < 0 || fileTime > DateTime.MaxValue.ToFileTimeUtc()) return null;

        ReadOnlySpan<byte> path;
        if (version == 1)
        {
            path = data[HeaderSize..Math.Min(data.Length, HeaderSize + (Version1PathChars * 2))];
        }
        else if (version == 2)
        {
            if (data.Length < HeaderSize + 4) return null;
            var chars = BinaryPrimitives.ReadInt32LittleEndian(data[HeaderSize..]);
            if (chars < 0) return null;
            var start = HeaderSize + 4;
            path = data[start..(int)Math.Min(data.Length, start + (2L * chars))];
        }
        else
        {
            return null;
        }

        var text = Encoding.Unicode.GetString(path[..(path.Length & ~1)]);
        var end = text.IndexOf('\0', StringComparison.Ordinal);
        if (end >= 0) text = text[..end];
        if (text.Length == 0) return null;

        return new RecycledItem(text, DateTime.FromFileTimeUtc(fileTime), Math.Max(0, size), isDirectory, recycledPath);
    }
}
