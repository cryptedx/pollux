namespace Pollux.Core.Files;

public readonly record struct MatchRange(int Start, int Length);

public readonly record struct FilterMatch(bool IsMatch, IReadOnlyList<MatchRange> Ranges)
{
    public static readonly FilterMatch All = new(true, []);
    public static readonly FilterMatch None = new(false, []);
}

/// <summary>Filter matching: case-insensitive substring first, then word initials ("dbp" → Directory.Build.props).</summary>
public static class FilterMatcher
{
    public static FilterMatch Match(string name, string query, bool matchInitials = true)
    {
        if (query.Length == 0)
        {
            return FilterMatch.All;
        }

        var i = name.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (i >= 0)
        {
            return new FilterMatch(true, [new MatchRange(i, query.Length)]);
        }

        return matchInitials ? MatchInitials(name, query) : FilterMatch.None;
    }

    /// <summary>True if <paramref name="query"/> matches the word initials of <paramref name="text"/> (no substring check).</summary>
    public static bool MatchesInitials(ReadOnlySpan<char> text, string query)
    {
        var q = 0;
        for (var i = 0; i < text.Length && q < query.Length; i++)
        {
            if (IsWordStart(text, i) && char.ToLowerInvariant(text[i]) == char.ToLowerInvariant(query[q]))
            {
                q++;
            }
        }

        return q == query.Length;
    }

    private static FilterMatch MatchInitials(string name, string query)
    {
        Span<int> hits = query.Length <= 64 ? stackalloc int[query.Length] : new int[query.Length];
        var q = 0;
        for (var i = 0; i < name.Length && q < query.Length; i++)
        {
            if (IsWordStart(name, i) && char.ToLowerInvariant(name[i]) == char.ToLowerInvariant(query[q]))
            {
                hits[q++] = i;
            }
        }

        if (q < query.Length)
        {
            return FilterMatch.None;
        }

        var ranges = new MatchRange[query.Length];
        for (var k = 0; k < ranges.Length; k++)
        {
            ranges[k] = new MatchRange(hits[k], 1);
        }

        return new FilterMatch(true, ranges);
    }

    private static bool IsWordStart(ReadOnlySpan<char> s, int i)
    {
        if (i == 0)
        {
            return true;
        }

        var c = s[i];
        var prev = s[i - 1];
        if (!char.IsLetterOrDigit(c))
        {
            return false;
        }

        return !char.IsLetterOrDigit(prev)
            || (char.IsUpper(c) && char.IsLower(prev))
            || (char.IsDigit(c) && !char.IsDigit(prev));
    }
}
