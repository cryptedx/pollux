namespace Pollux.Core.Updates;

public sealed record WingetPackage(string Name, string Id, string Version, string Available, string Source);

/// <summary>Parses the table printed by "winget upgrade" (column positions come from the header, so it works in any UI language).</summary>
public static class WingetParser
{
    public static IReadOnlyList<WingetPackage> ParseUpgrades(string output)
    {
        // winget redraws a spinner with carriage returns; keep only what is left after the last \r on each line.
        var lines = output.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')
            .Select(l => l.Contains('\r') ? l[(l.LastIndexOf('\r') + 1)..] : l)
            .ToList();

        var sep = lines.FindIndex(l => l.Trim().Length > 10 && l.Trim().All(c => c == '-'));
        if (sep < 1) return [];

        var header = lines[sep - 1];
        var starts = ColumnStarts(header);
        if (starts.Count < 4) return [];

        var result = new List<WingetPackage>();
        for (var i = sep + 1; i < lines.Count; i++)
        {
            var line = lines[i];
            if (line.Trim().Length == 0 || line.Length < starts[1] + 1 || !char.IsWhiteSpace(line[starts[1] - 1])) break;
            var cols = new string[starts.Count];
            for (var c = 0; c < starts.Count; c++)
            {
                var start = starts[c];
                var end = c + 1 < starts.Count ? starts[c + 1] : line.Length;
                cols[c] = start < line.Length ? line[start..Math.Min(end, line.Length)].Trim() : "";
            }

            if (cols[1].Length == 0 || cols[2].Length == 0 || cols[3].Length == 0) break;
            result.Add(new WingetPackage(cols[0], cols[1], cols[2], cols[3], starts.Count > 4 ? cols[4] : ""));
        }

        return result;
    }

    private static List<int> ColumnStarts(string header)
    {
        var starts = new List<int>();
        for (var i = 0; i < header.Length; i++)
        {
            if (!char.IsWhiteSpace(header[i]) && (i == 0 || char.IsWhiteSpace(header[i - 1]))) starts.Add(i);
        }

        return starts;
    }
}
