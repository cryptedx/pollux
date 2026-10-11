namespace Pollux.Core.Files;

/// <summary>
/// Wildcard patterns for selecting entries by name: "*" matches any run of characters, "?" exactly one, ";" separates
/// alternatives (<c>*.log;*.tmp</c>). A pattern must match the whole name. Case-insensitive, like Windows file names.
/// </summary>
public static class WildcardMatcher
{
    /// <summary>True if <paramref name="name"/> matches any alternative in <paramref name="patterns"/>. Blank alternatives are ignored.</summary>
    public static bool IsMatch(string name, string patterns)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(patterns);
        foreach (var part in patterns.Split(';'))
        {
            var pattern = part.Trim();
            if (pattern.Length > 0 && Glob(name, pattern))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The positions in <paramref name="names"/> that <see cref="IsMatch"/> accepts, in order.</summary>
    public static IReadOnlyList<int> Indexes(IReadOnlyList<string> names, string patterns)
    {
        ArgumentNullException.ThrowIfNull(names);
        var hits = new List<int>();
        for (var i = 0; i < names.Count; i++)
        {
            if (IsMatch(names[i], patterns))
            {
                hits.Add(i);
            }
        }

        return hits;
    }

    private static bool Glob(string name, string pattern)
    {
        var n = 0;
        var p = 0;
        var star = -1;
        var mark = 0;
        while (n < name.Length)
        {
            if (p < pattern.Length && pattern[p] == '*')
            {
                star = p++;
                mark = n;
            }
            else if (p < pattern.Length && (pattern[p] == '?' || SameChar(pattern[p], name[n])))
            {
                p++;
                n++;
            }
            else if (star >= 0)
            {
                p = star + 1;
                n = ++mark;
            }
            else
            {
                return false;
            }
        }

        while (p < pattern.Length && pattern[p] == '*')
        {
            p++;
        }

        return p == pattern.Length;
    }

    private static bool SameChar(char a, char b) => char.ToUpperInvariant(a) == char.ToUpperInvariant(b);
}
