namespace Pollux.Copilot.Tools;

/// <summary>Which entries a selection considers.</summary>
public enum EntryKind
{
    Any,
    File,
    Folder,
}

/// <summary>Matches entry names against simple wildcard patterns (<c>*</c> and <c>?</c>, case-insensitive, like Explorer).</summary>
public static class EntryPatterns
{
    public static EntryKind ParseKind(string? kind) => kind?.Trim().ToLowerInvariant() switch
    {
        "file" or "files" => EntryKind.File,
        "folder" or "folders" or "directory" or "directories" => EntryKind.Folder,
        _ => EntryKind.Any,
    };

    /// <summary>The entries of <paramref name="kind"/> whose name matches at least one pattern, in listing order.</summary>
    public static IEnumerable<PaneEntry> Match(IEnumerable<PaneEntry> entries, IReadOnlyList<string> patterns, EntryKind kind) =>
        entries.Where(e => kind switch
            {
                EntryKind.File => !e.IsFolder,
                EntryKind.Folder => e.IsFolder,
                _ => true,
            }
            && patterns.Any(p => !string.IsNullOrEmpty(p) && Glob(p, e.Name)));

    private static bool Glob(string pattern, string name)
    {
        int p = 0, n = 0, star = -1, mark = 0;
        while (n < name.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || char.ToUpperInvariant(pattern[p]) == char.ToUpperInvariant(name[n])) && pattern[p] != '*')
            {
                p++;
                n++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                star = p++;
                mark = n;
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

        while (p < pattern.Length && pattern[p] == '*') p++;
        return p == pattern.Length;
    }
}
