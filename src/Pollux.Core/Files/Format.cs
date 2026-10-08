using System.Globalization;

namespace Pollux.Core.Files;

public static class Format
{
    private static readonly string[] Units = ["B", "K", "M", "G", "T"];

    /// <summary>Compact binary size: "612 B", "1.2 K", "30.0 M".</summary>
    public static string Size(long bytes)
    {
        if (bytes < 1024)
        {
            return bytes.ToString(CultureInfo.InvariantCulture) + " B";
        }

        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return value.ToString("0.0", CultureInfo.InvariantCulture) + " " + Units[unit];
    }

    /// <summary>Short type label used as file badge and in the Type column: "MD", "PDF", "EXE"; folders are "Folder".</summary>
    public static string TypeLabel(FileEntry entry) =>
        entry.IsDirectory ? "Folder" : entry.Extension.Length == 0 ? "File" : entry.Extension.ToUpperInvariant();

    public static string Badge(FileEntry entry)
    {
        if (entry.IsDirectory) return "";
        if (entry.Name.StartsWith('.') && entry.Extension.Length == 0) return "··";
        var ext = entry.Extension;
        return ext.Length == 0 ? "" : ext.Length <= 2 ? ext.ToUpperInvariant() : ext[..2].ToUpperInvariant();
    }

    public static string Relative(DateTime then, DateTime now)
    {
        var minutes = (now - then).TotalMinutes;
        return minutes switch
        {
            < 1 => "just now",
            < 60 => $"{(int)minutes}m ago",
            < 1440 => $"{(int)(minutes / 60)}h ago",
            _ => $"{(int)(minutes / 1440)}d ago",
        };
    }
}
