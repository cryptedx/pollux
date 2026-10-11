using System.Globalization;
using System.IO.Enumeration;
using System.Text;
using Pollux.Core.Files;

namespace Pollux.Core.Search;

/// <summary>A parsed query, or why the text is not one.</summary>
public readonly record struct FolderSearchParse(FolderSearchQuery? Query, string? Error);

/// <summary>
/// What a Folder Search looks for. Every part that is set must match: all name terms (substring, or the whole name
/// when the term has * or ?), the modified range, the size range and the content text.
/// </summary>
public sealed record FolderSearchQuery
{
    /// <summary>The query syntax in one line, for placeholders and tooltips.</summary>
    public const string Syntax = "name or *.ext · text:\"…\" · after:2026-01-31 / after:7d · before:… · size>1mb · size<10k";

    public IReadOnlyList<string> NameTerms { get; init; } = [];

    /// <summary>Plain text the file content must contain (case-insensitive), or null.</summary>
    public string? Content { get; init; }

    /// <summary>Modified at or after this local time.</summary>
    public DateTime? ModifiedAfter { get; init; }

    /// <summary>Modified before this local time (exclusive).</summary>
    public DateTime? ModifiedBefore { get; init; }

    /// <summary>At least this many bytes (inclusive).</summary>
    public long? MinSize { get; init; }

    /// <summary>At most this many bytes (inclusive).</summary>
    public long? MaxSize { get; init; }

    /// <summary>Content and size filters only make sense for files, so folders are then left out.</summary>
    public bool FilesOnly => Content is not null || MinSize is not null || MaxSize is not null;

    /// <summary>
    /// Parses the name box (name terms plus filters) and the optional content box, which wins over a text: filter.
    /// Relative dates (7d, 3h, 2w, today, yesterday) count back from <paramref name="now"/>.
    /// </summary>
    public static FolderSearchParse Parse(string text, string? content, DateTime now)
    {
        var terms = new List<string>();
        string? tokenContent = null;
        DateTime? after = null, before = null;
        long? min = null, max = null;

        foreach (var token in Tokenize(text))
        {
            if (token.Length == 0) continue;
            if (TryFilter(token, "size", out var sizeExpr))
            {
                if (!TryParseSize(sizeExpr, out var op, out var bytes)) return Fail($"Invalid size filter '{token}' (e.g. size>1mb, size<=500k).");
                switch (op)
                {
                    case ">": min = bytes + 1; break;
                    case ">=": min = bytes; break;
                    case "<": max = bytes - 1; break;
                    case "<=": max = bytes; break;
                }
            }
            else if (TryKey(token, out var key, out var value))
            {
                switch (key)
                {
                    case "after":
                        if (!TryParseDate(value, now, out var a, out _)) return Fail($"Invalid date in '{token}' (e.g. after:2026-01-31 or after:7d).");
                        after = a;
                        break;
                    case "before":
                        if (!TryParseDate(value, now, out var b, out var bIsDay)) return Fail($"Invalid date in '{token}' (e.g. before:2026-01-31 or before:30d).");
                        // A calendar day includes the whole day; a relative time is an instant.
                        before = bIsDay ? b.AddDays(1) : b;
                        break;
                    case "text" or "content":
                        if (value.Length > 0) tokenContent = value;
                        break;
                    default:
                        return Fail($"Unknown filter '{key}:'. Use after:, before:, size or text:.");
                }
            }
            else
            {
                terms.Add(token);
            }
        }

        var c = string.IsNullOrWhiteSpace(content) ? tokenContent : content.Trim();
        if (min is { } lo && max is { } hi && lo > hi) return Fail("The size range is empty (min is above max).");
        if (after is { } from && before is { } to && from >= to) return Fail("The date range is empty (after: is not before before:).");
        if (terms.Count == 0 && c is null && after is null && before is null && min is null && max is null)
        {
            return Fail("Type a name, *.ext, text or a filter.");
        }

        return new FolderSearchParse(new FolderSearchQuery
        {
            NameTerms = terms,
            Content = c,
            ModifiedAfter = after,
            ModifiedBefore = before,
            MinSize = min,
            MaxSize = max,
        }, null);

        static FolderSearchParse Fail(string error) => new(null, error);
    }

    /// <summary>Whether the entry matches everything but the content (that needs the file read).</summary>
    public bool Matches(FileEntry entry)
    {
        if (entry.IsDirectory && FilesOnly) return false;
        foreach (var term in NameTerms)
        {
            if (!TermMatches(term, entry.Name)) return false;
        }

        if (ModifiedAfter is { } after && entry.Modified < after) return false;
        if (ModifiedBefore is { } before && entry.Modified >= before) return false;
        if (MinSize is { } min && entry.Size < min) return false;
        if (MaxSize is { } max && entry.Size > max) return false;
        return true;
    }

    /// <summary>Where the plain (non-wildcard) name terms occur in <paramref name="name"/>, ordered by position.</summary>
    public IReadOnlyList<MatchRange> Highlights(string name)
    {
        var ranges = new List<MatchRange>();
        foreach (var term in NameTerms)
        {
            if (IsWildcard(term)) continue;
            var i = name.IndexOf(term, StringComparison.OrdinalIgnoreCase);
            if (i >= 0) ranges.Add(new MatchRange(i, term.Length));
        }

        ranges.Sort((x, y) => x.Start.CompareTo(y.Start));
        return ranges;
    }

    private static bool IsWildcard(string term) => term.AsSpan().IndexOfAny('*', '?') >= 0;

    private static bool TermMatches(string term, string name) =>
        IsWildcard(term)
            ? FileSystemName.MatchesSimpleExpression(term, name, ignoreCase: true)
            : name.Contains(term, StringComparison.OrdinalIgnoreCase);

    /// <summary>Splits on whitespace; double quotes keep spaces (also inside a token, as in text:"two words").</summary>
    private static List<string> Tokenize(string text)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        var hadQuotes = false;
        foreach (var ch in text)
        {
            if (ch == '"')
            {
                quoted = !quoted;
                hadQuotes = true;
            }
            else if (char.IsWhiteSpace(ch) && !quoted)
            {
                Flush();
            }
            else
            {
                current.Append(ch);
            }
        }

        Flush();
        return tokens;

        void Flush()
        {
            if (current.Length > 0 || hadQuotes) tokens.Add(current.ToString());
            current.Clear();
            hadQuotes = false;
        }
    }

    /// <summary>"size>1mb" and "size:>1mb" → ">1mb".</summary>
    private static bool TryFilter(string token, string name, out string expression)
    {
        expression = "";
        if (!token.StartsWith(name, StringComparison.OrdinalIgnoreCase)) return false;
        var rest = token[name.Length..];
        if (rest.StartsWith(':')) rest = rest[1..];
        if (!rest.StartsWith('<') && !rest.StartsWith('>')) return false;
        expression = rest;
        return true;
    }

    /// <summary>A "key:value" token. Windows names cannot contain ':', so a word before one is always meant as a filter.</summary>
    private static bool TryKey(string token, out string key, out string value)
    {
        var colon = token.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0 || !IsAsciiWord(token.AsSpan(0, colon)))
        {
            key = value = "";
            return false;
        }

        key = token[..colon].ToLowerInvariant();
        value = token[(colon + 1)..];
        return true;
    }

    private static bool IsAsciiWord(ReadOnlySpan<char> s)
    {
        foreach (var ch in s)
        {
            if (!char.IsAsciiLetter(ch)) return false;
        }

        return true;
    }

    private static bool TryParseSize(string expression, out string op, out long bytes)
    {
        op = expression.StartsWith(">=", StringComparison.Ordinal) || expression.StartsWith("<=", StringComparison.Ordinal) ? expression[..2] : expression[..1];
        bytes = 0;
        var number = expression[op.Length..].Trim().ToLowerInvariant();
        long unit = 1;
        foreach (var (suffix, factor) in Units)
        {
            if (number.EndsWith(suffix, StringComparison.Ordinal))
            {
                unit = factor;
                number = number[..^suffix.Length];
                break;
            }
        }

        if (!double.TryParse(number, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value)) return false;
        bytes = (long)Math.Round(value * unit);
        return true;
    }

    // Longest suffix first so "mb" is not read as "b".
    private static readonly (string Suffix, long Factor)[] Units =
    [
        ("kb", 1L << 10), ("mb", 1L << 20), ("gb", 1L << 30), ("tb", 1L << 40),
        ("k", 1L << 10), ("m", 1L << 20), ("g", 1L << 30), ("t", 1L << 40), ("b", 1),
    ];

    /// <summary>A calendar day (yyyy-MM-dd, today, yesterday; <paramref name="isDay"/>) or a time ago (7d, 3h, 2w).</summary>
    private static bool TryParseDate(string value, DateTime now, out DateTime date, out bool isDay)
    {
        isDay = true;
        var v = value.ToLowerInvariant();
        switch (v)
        {
            case "today": date = now.Date; return true;
            case "yesterday": date = now.Date.AddDays(-1); return true;
        }

        if (DateTime.TryParseExact(v, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date)) return true;

        isDay = false;
        date = default;
        if (v.Length < 2 || !int.TryParse(v.AsSpan(0, v.Length - 1), NumberStyles.None, CultureInfo.InvariantCulture, out var n)) return false;
        switch (v[^1])
        {
            case 'h': date = now.AddHours(-n); return true;
            case 'd': date = now.AddDays(-n); return true;
            case 'w': date = now.AddDays(-7 * n); return true;
            default: return false;
        }
    }
}
