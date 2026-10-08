using System.Text;
using System.Text.RegularExpressions;

namespace Pollux.Core.Notes;

/// <summary>Reads Tags and titles from a Note's Markdown and derives its file name.</summary>
public static partial class NoteText
{
    [GeneratedRegex(@"(?<![\w#&/`])#([\p{L}_][\p{L}\p{N}_\-/]*)")]
    private static partial Regex InlineTag();

    [GeneratedRegex(@"`[^`\n]*`")]
    private static partial Regex InlineCode();

    public static IReadOnlyList<string> Tags(string text)
    {
        var tags = new List<string>();
        var (front, body) = SplitFrontMatter(text);
        if (front is not null) tags.AddRange(FrontMatterTags(front));

        var inFence = false;
        foreach (var raw in body.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal)) { inFence = !inFence; continue; }
            if (inFence || line.TrimStart().StartsWith("# ", StringComparison.Ordinal) || Regex.IsMatch(line, @"^\s*#{1,6}\s")) continue;
            foreach (Match m in InlineTag().Matches(InlineCode().Replace(line, ""))) tags.Add(m.Groups[1].Value);
        }

        return tags.Select(t => t.ToLowerInvariant()).Distinct().ToList();
    }

    public static string Title(string text)
    {
        var (_, body) = SplitFrontMatter(text);
        foreach (var raw in body.Split('\n'))
        {
            var line = raw.Trim().TrimStart('#', '>', '-', '*', ' ').Trim();
            if (line.Length > 0) return line;
        }

        return "";
    }

    public static string FileName(string text, DateTime created)
    {
        var title = Title(text);
        if (title.Length == 0) return created.ToString("yyyy-MM-dd_HHmm", System.Globalization.CultureInfo.InvariantCulture) + ".md";
        var sb = new StringBuilder();
        foreach (var c in title)
        {
            sb.Append(c switch
            {
                '/' or '\\' => '-',
                ':' or '*' or '?' or '"' or '<' or '>' or '|' => ' ',
                _ when char.IsControl(c) => ' ',
                _ => c,
            });
        }

        var name = Regex.Replace(sb.ToString(), @"\s+", " ").Trim().TrimEnd('.');
        if (name.Length > 60) name = name[..60].TrimEnd();
        return (name.Length == 0 ? created.ToString("yyyy-MM-dd_HHmm", System.Globalization.CultureInfo.InvariantCulture) : name) + ".md";
    }

    /// <summary>Replaces (or adds) the tags line in the YAML front matter.</summary>
    public static string SetTags(string text, IEnumerable<string> tags)
    {
        var line = "tags: [" + string.Join(", ", tags) + "]";
        var (front, body) = SplitFrontMatter(text);
        if (front is null) return $"---\n{line}\n---\n{text}";
        var lines = front.Length == 0 ? [] : front.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        var i = lines.FindIndex(l => l.StartsWith("tags:", StringComparison.Ordinal));
        if (i >= 0)
        {
            var end = i + 1;
            while (end < lines.Count && lines[end].TrimStart().StartsWith("- ", StringComparison.Ordinal)) end++;
            lines.RemoveRange(i, end - i);
            lines.Insert(i, line);
        }
        else
        {
            lines.Add(line);
        }

        return "---\n" + string.Join('\n', lines) + "\n---\n" + body;
    }

    private static (string? Front, string Body) SplitFrontMatter(string text)
    {
        var t = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (!t.StartsWith("---\n", StringComparison.Ordinal)) return (null, t);
        if (t.StartsWith("---\n---", StringComparison.Ordinal))
        {
            var rest = t[7..];
            return ("", rest.StartsWith('\n') ? rest[1..] : rest);
        }

        var end = t.IndexOf("\n---", 4, StringComparison.Ordinal);
        if (end < 0) return (null, t);
        var after = end + 4;
        if (after < t.Length && t[after] == '\n') after++;
        return (t[4..end], t[after..]);
    }

    private static IEnumerable<string> FrontMatterTags(string front)
    {
        var lines = front.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var l = lines[i].Trim();
            if (!l.StartsWith("tags:", StringComparison.Ordinal)) continue;
            var value = l[5..].Trim();
            if (value.StartsWith('['))
            {
                foreach (var t in value.Trim('[', ']').Split(',')) if (t.Trim().Trim('"', '\'') is { Length: > 0 } s) yield return s;
            }
            else if (value.Length > 0)
            {
                foreach (var t in value.Split(',', ' ')) if (t.Trim() is { Length: > 0 } s) yield return s;
            }
            else
            {
                for (var j = i + 1; j < lines.Length && lines[j].TrimStart().StartsWith("- ", StringComparison.Ordinal); j++)
                {
                    yield return lines[j].TrimStart()[2..].Trim().Trim('"', '\'');
                }
            }
        }
    }
}
