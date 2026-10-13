using System.Text;

namespace Pollux.Core.Snap;

/// <summary>Picks the language Windows text recognition reads a Snap in.</summary>
public static class SnapOcrLanguage
{
    /// <summary>
    /// The installed OCR language to use: the configured one (<paramref name="setting"/>, e.g. "de" or "de-DE") or none
    /// when it is not installed; without a setting the first Windows profile language that is installed (same language
    /// counts, e.g. de-AT → de-DE), else any installed one; null when none is installed.
    /// </summary>
    public static string? Choose(string? setting, IEnumerable<string> profileLanguages, IReadOnlyList<string> installed)
    {
        if (!string.IsNullOrWhiteSpace(setting)) return Match(setting.Trim(), installed);
        foreach (var language in profileLanguages)
        {
            if (Match(language, installed) is { } found) return found;
        }

        return installed.Count > 0 ? installed[0] : null;
    }

    private static string? Match(string language, IReadOnlyList<string> installed) =>
        installed.FirstOrDefault(i => string.Equals(i, language, StringComparison.OrdinalIgnoreCase))
        ?? installed.FirstOrDefault(i => string.Equals(Primary(i), Primary(language), StringComparison.OrdinalIgnoreCase));

    private static string Primary(string tag) => tag.Split('-')[0];
}

/// <summary>One recognised line: its text, top and height in pixels.</summary>
public sealed record SnapOcrLine(string Text, double Top, double Height);

/// <summary>Turns recognised lines into the text that is copied.</summary>
public static class SnapOcrText
{
    /// <summary>Lines in reading order, one per row; a gap of more than 1.5 line heights starts a new paragraph (blank line).</summary>
    public static string Join(IEnumerable<SnapOcrLine> lines)
    {
        var text = new StringBuilder();
        SnapOcrLine? previous = null;
        foreach (var line in lines.Where(l => !string.IsNullOrWhiteSpace(l.Text)))
        {
            if (previous is not null)
            {
                text.Append('\n');
                if (line.Top - (previous.Top + previous.Height) > previous.Height * 1.5) text.Append('\n');
            }

            text.Append(line.Text.Trim());
            previous = line;
        }

        return text.ToString();
    }
}
