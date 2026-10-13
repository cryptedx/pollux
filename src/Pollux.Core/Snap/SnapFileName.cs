using System.Globalization;
using System.Text.RegularExpressions;

namespace Pollux.Core.Snap;

/// <summary>Names the PNG a Snap is saved as: "Snap" plus the time it was taken, never an existing name.</summary>
public static partial class SnapFileName
{
    [GeneratedRegex(@"^Snap (?<taken>\d{4}-\d{2}-\d{2} \d{2}-\d{2}-\d{2})( \(\d+\))?\.png$", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();

    /// <summary>True for a name this class gives out ("Snap 2026-10-12 14-03-22.png", optionally " (2)").</summary>
    public static bool IsSnap(string name) => TakenAt(name) is not null;

    /// <summary>When the Snap named <paramref name="name"/> was taken; null when it is not a Snap's name.</summary>
    public static DateTime? TakenAt(string name)
    {
        var m = Pattern().Match(name);
        return m.Success && DateTime.TryParseExact(m.Groups["taken"].Value, "yyyy-MM-dd HH-mm-ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var taken)
            ? taken
            : null;
    }

    public static string For(DateTime taken, Func<string, bool> exists)
    {
        var stem = "Snap " + taken.ToString("yyyy-MM-dd HH-mm-ss", CultureInfo.InvariantCulture);
        var name = stem + ".png";
        for (var i = 2; exists(name); i++) name = $"{stem} ({i}).png";
        return name;
    }
}
