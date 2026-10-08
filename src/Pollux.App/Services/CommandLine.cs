using System.Text;

namespace Pollux.App.Services;

internal static class CommandLine
{
    /// <summary>Splits a Windows command line, honouring double quotes.</summary>
    public static string[] Split(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine)) return [];
        var result = new List<string>();
        var sb = new StringBuilder();
        var quoted = false;
        foreach (var c in commandLine)
        {
            if (c == '"') { quoted = !quoted; continue; }
            if (char.IsWhiteSpace(c) && !quoted)
            {
                if (sb.Length > 0) { result.Add(sb.ToString()); sb.Clear(); }
                continue;
            }

            sb.Append(c);
        }

        if (sb.Length > 0) result.Add(sb.ToString());
        return [.. result];
    }
}
