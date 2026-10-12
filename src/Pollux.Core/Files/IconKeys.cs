using Pollux.Core.Navigation;

namespace Pollux.Core.Files;

/// <summary>
/// Identifies one shell icon. Ordinary files share the icon of their type, so their key is the extension and asking
/// for it reads nothing from disk. Entries that carry an icon of their own (programs, icon files, shortcuts, drive roots,
/// customized and special folders) are keyed by path and date. On a network or cloud-only location every entry gets
/// its type's icon: reading the file there can hang the icon thread or download it.
/// </summary>
/// <param name="Name">Lower-case extension with its dot (".txt", "" for none), or the lower-case full path when <see cref="PerPath"/>.</param>
/// <param name="LinkOverlay">Draw the shortcut arrow over the icon.</param>
/// <param name="Pixels">The icon edge in physical pixels.</param>
public readonly record struct IconKey(string Name, bool PerPath, bool IsDirectory, DateTime Modified, bool LinkOverlay, int Pixels)
{
    /// <param name="entry">The file or folder.</param>
    /// <param name="pixels">The icon edge in physical pixels (<see cref="IconSizes.Pixels"/>).</param>
    /// <param name="onNetworkDrive">The entry lies on a mapped network drive (Z:\ looks like a local path, so the caller says it).</param>
    /// <param name="ownIconType">Optional: true for a lower-case extension (with dot) whose files each have their own icon by registration.</param>
    public static IconKey For(FileEntry entry, int pixels, bool onNetworkDrive = false, Func<string, bool>? ownIconType = null)
    {
        var remote = onNetworkDrive || WinPath.IsUnc(entry.FullPath) || entry.Cloud == CloudState.CloudOnly;
        if (entry.IsDirectory)
        {
            var own = !remote && (IconKeys.IsDriveRoot(entry.FullPath)
                || (entry.Attributes & (FileAttributes.ReadOnly | FileAttributes.System)) != 0);
            return own
                ? new(entry.FullPath.ToLowerInvariant(), true, true, entry.Modified, false, pixels)
                : new("", false, true, default, false, pixels);
        }

        var ext = entry.Extension.Length == 0 ? "" : "." + entry.Extension.ToLowerInvariant();
        var link = IconKeys.LinkExtensions.Contains(ext);
        var perPath = !remote && ext.Length > 0 && (IconKeys.OwnIconExtensions.Contains(ext) || ownIconType?.Invoke(ext) == true);
        return perPath
            ? new(entry.FullPath.ToLowerInvariant(), true, false, entry.Modified, link, pixels)
            : new(ext, false, false, default, link, pixels);
    }

    /// <summary>
    /// The name to ask the shell for the type's icon without touching the disk (SHGFI_USEFILEATTRIBUTES): a made-up
    /// file with the extension, or a made-up folder. A per-path key falls back to this when its own file cannot be read.
    /// </summary>
    public string TypeName => IsDirectory ? "folder" : "file" + (PerPath ? PathExtension(Name) : Name);

    private static string PathExtension(string path)
    {
        var slash = path.LastIndexOf('\\');
        var dot = path.LastIndexOf('.');
        return dot > slash + 1 ? path[dot..] : "";
    }
}

/// <summary>Which files carry their own icon, and whether an icon location they name is safe to read.</summary>
public static class IconKeys
{
    /// <summary>Extensions whose files each show their own icon (lower case, with dot).</summary>
    public static readonly IReadOnlySet<string> OwnIconExtensions = new HashSet<string>(StringComparer.Ordinal)
    {
        ".exe", ".ico", ".cur", ".ani", ".lnk", ".url", ".pif", ".scr", ".cpl", ".msi", ".appref-ms", ".website",
    };

    /// <summary>Extensions Explorer draws with the shortcut arrow.</summary>
    public static readonly IReadOnlySet<string> LinkExtensions = new HashSet<string>(StringComparer.Ordinal)
    {
        ".lnk", ".url", ".pif",
    };

    public static bool IsDriveRoot(string path) =>
        path.Length is 2 or 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && (path.Length == 2 || path[2] == '\\');

    /// <summary>
    /// True when a type's registered icon is per file: its DefaultIcon is the file itself ("%1") or an icon handler
    /// picks it. <paramref name="defaultIcon"/> is the DefaultIcon value of the type's ProgID, null when absent.
    /// </summary>
    public static bool IsPerFileType(string? defaultIcon, bool hasIconHandler) =>
        hasIconHandler || defaultIcon?.Trim().Trim('"') is "%1" or "%1,0";

    /// <summary>
    /// The icon file named by an INI-style file (desktop.ini's IconResource or IconFile, a .url's IconFile), without
    /// quotes or icon index; null when it names none.
    /// </summary>
    public static string? IniIconLocation(string text)
    {
        string? file = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var name = line[..eq].Trim();
            var value = StripIndex(line[(eq + 1)..].Trim().Trim('"'));
            if (value.Length == 0) continue;
            if (name.Equals("IconResource", StringComparison.OrdinalIgnoreCase)) return value;
            if (name.Equals("IconFile", StringComparison.OrdinalIgnoreCase)) file ??= value;
        }

        return file;
    }

    private static string StripIndex(string location)
    {
        var comma = location.LastIndexOf(',');
        if (comma < 0) return location;
        var index = location.AsSpan(comma + 1).Trim();
        return int.TryParse(index, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out _)
            ? location[..comma].Trim().Trim('"')
            : location;
    }

    /// <summary>
    /// True when reading an icon from <paramref name="location"/> stays on this machine: no UNC path, no URL, no
    /// mapped network drive. Empty and relative locations (next to a local file) are local. Expand environment
    /// variables first.
    /// </summary>
    public static bool IsLocalLocation(string location, Func<char, bool> isNetworkDrive)
    {
        var l = location.Trim().Trim('"');
        if (l.StartsWith(@"\\?\", StringComparison.Ordinal) && l.Length > 5 && l[5] == ':') l = l[4..];
        if (l.StartsWith(@"\\", StringComparison.Ordinal) || l.StartsWith("//", StringComparison.Ordinal)) return false;
        if (l.Contains("://", StringComparison.Ordinal) || l.StartsWith("file:", StringComparison.OrdinalIgnoreCase)) return false;
        if (l.Length >= 2 && l[1] == ':' && char.IsAsciiLetter(l[0])) return !isNetworkDrive(char.ToUpperInvariant(l[0]));
        return true;
    }
}

/// <summary>The shell's system image lists and how a requested icon size maps onto them.</summary>
public enum ShellImageList { Large = 0, Small = 1, ExtraLarge = 2 }

public static class IconSizes
{
    /// <summary>Icon edge in physical pixels for an edge in device-independent pixels at a display scale (1.0 = 96 dpi).</summary>
    public static int Pixels(int dips, double scale) => (int)Math.Round(dips * Math.Max(1.0, scale));

    /// <summary>
    /// The image list to draw an icon of <paramref name="pixels"/> from: the smallest whose icons are at least that
    /// big (the Image scales down, which stays crisp), else the biggest. Sizes are those the shell reports for this process.
    /// </summary>
    public static ShellImageList Pick(int pixels, int small, int large, int extraLarge)
    {
        (ShellImageList List, int Size)[] lists = [(ShellImageList.Small, small), (ShellImageList.Large, large), (ShellImageList.ExtraLarge, extraLarge)];
        var fitting = lists.Where(l => l.Size >= pixels).OrderBy(l => l.Size).ToList();
        return fitting.Count > 0 ? fitting[0].List : lists.MaxBy(l => l.Size).List;
    }
}

/// <summary>Pixel fix-ups for icons read from an HICON.</summary>
public static class IconPixels
{
    /// <summary>
    /// Turns an icon's color bitmap (top-down BGRA) into straight-alpha BGRA. A color bitmap with alpha keeps it; one
    /// without (an old 24-bit icon) takes it from the AND mask read as 32-bit, where white means transparent.
    /// </summary>
    public static void ApplyMask(Span<byte> color, ReadOnlySpan<byte> mask)
    {
        for (var i = 3; i < color.Length; i += 4)
        {
            if (color[i] != 0) return;
        }

        for (var i = 0; i + 3 < color.Length && i + 3 < mask.Length; i += 4)
        {
            var transparent = mask[i] != 0 || mask[i + 1] != 0 || mask[i + 2] != 0;
            if (transparent) color[i] = color[i + 1] = color[i + 2] = color[i + 3] = 0;
            else color[i + 3] = 255;
        }
    }

    /// <summary>Straight to premultiplied alpha, the layout a WriteableBitmap's pixel buffer expects.</summary>
    public static void Premultiply(Span<byte> bgra)
    {
        for (var i = 0; i + 3 < bgra.Length; i += 4)
        {
            var a = bgra[i + 3];
            if (a == 255) continue;
            bgra[i] = (byte)((bgra[i] * a + 127) / 255);
            bgra[i + 1] = (byte)((bgra[i + 1] * a + 127) / 255);
            bgra[i + 2] = (byte)((bgra[i + 2] * a + 127) / 255);
        }
    }
}

/// <summary>Least-recently-used cache; a null value records a known miss. Not thread-safe.</summary>
public sealed class IconCache<TKey, TValue>(int capacity) where TKey : notnull where TValue : class
{
    private readonly Dictionary<TKey, LinkedListNode<(TKey Key, TValue? Value)>> _map = [];
    private readonly LinkedList<(TKey Key, TValue? Value)> _order = new();

    public int Count => _map.Count;

    public bool TryGet(TKey key, out TValue? value)
    {
        if (!_map.TryGetValue(key, out var node))
        {
            value = null;
            return false;
        }

        _order.Remove(node);
        _order.AddFirst(node);
        value = node.Value.Value;
        return true;
    }

    public void Add(TKey key, TValue? value)
    {
        if (_map.Remove(key, out var old)) _order.Remove(old);
        _map[key] = _order.AddFirst((key, value));
        while (_map.Count > Math.Max(1, capacity))
        {
            var last = _order.Last!;
            _order.RemoveLast();
            _map.Remove(last.Value.Key);
        }
    }
}
