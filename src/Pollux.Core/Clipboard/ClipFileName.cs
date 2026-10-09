using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Pollux.Core.Clipboard;

/// <summary>Derives the file name when a Clip is pasted into a folder as a file (Ctrl+V with text or an image).</summary>
public static partial class ClipFileName
{
    private const int MaxStemLength = 60;

    [GeneratedRegex(@"^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])(\..*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex ReservedName();

    /// <summary>Text becomes a .txt named after its first non-blank line, or the timestamp; never an existing name.</summary>
    public static string ForText(string text, DateTime now, Func<string, bool> exists) =>
        Unique(Stem(text) ?? Timestamp(now), ".txt", exists);

    /// <summary>An image becomes a .png named after the timestamp; never an existing name.</summary>
    public static string ForImage(DateTime now, Func<string, bool> exists) =>
        Unique(Timestamp(now), ".png", exists);

    private static string Timestamp(DateTime now) => now.ToString("yyyy-MM-dd_HHmmss", CultureInfo.InvariantCulture);

    private static string Unique(string stem, string extension, Func<string, bool> exists)
    {
        var name = stem + extension;
        for (var i = 2; exists(name); i++) name = $"{stem} ({i}){extension}";
        return name;
    }

    private static string? Stem(string text)
    {
        var line = text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
        if (line is null) return null;

        var sb = new StringBuilder();
        foreach (var c in line)
        {
            sb.Append(c switch
            {
                '/' or '\\' => '-',
                ':' or '*' or '?' or '"' or '<' or '>' or '|' => ' ',
                _ when char.IsControl(c) => ' ',
                _ => c,
            });
        }

        var stem = Regex.Replace(sb.ToString(), @"\s+", " ").Trim().TrimEnd('.');
        if (stem.Length > MaxStemLength) stem = stem[..MaxStemLength].TrimEnd().TrimEnd('.');
        return stem.Length == 0 || ReservedName().IsMatch(stem) ? null : stem;
    }
}
