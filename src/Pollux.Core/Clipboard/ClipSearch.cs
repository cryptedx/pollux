using Pollux.Core.Files;

namespace Pollux.Core.Clipboard;

/// <summary>
/// Finds Clips for the Clipboard Stack. Every search term must match the Clip text, a file name or a Kept Clip title,
/// each by substring or fuzzily (word initials); <c>@app</c> narrows to Clips copied from an app whose name contains "app".
/// Clips matching every term by substring come first, then the fuzzy-only matches.
/// </summary>
public static class ClipSearch
{
    /// <summary>
    /// The Clips matching <paramref name="query"/>: those matching every term by substring first, then those that need
    /// a fuzzy match for some term; each group in the order given (Stack order).
    /// </summary>
    public static IReadOnlyList<Clip> Find(IEnumerable<Clip> clips, string query)
    {
        var tokens = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        // A lone "@" is an app filter still being typed: ignore it.
        var apps = tokens.Where(t => t.Length > 1 && t[0] == '@').Select(t => t[1..]).ToArray();
        var terms = tokens.Where(t => t[0] != '@').ToArray();
        var substring = new List<Clip>();
        var fuzzy = new List<Clip>();
        foreach (var clip in clips)
        {
            if (!FromApp(clip, apps)) continue;
            var onlyFuzzy = false;
            var all = true;
            foreach (var term in terms)
            {
                if (MatchesSubstring(clip, term)) continue;
                if (!MatchesFuzzily(clip, term))
                {
                    all = false;
                    break;
                }

                onlyFuzzy = true;
            }

            if (all) (onlyFuzzy ? fuzzy : substring).Add(clip);
        }

        substring.AddRange(fuzzy);
        return substring;
    }

    /// <summary>The last segment of a Windows path (independent of the OS the library runs on).</summary>
    public static string FileName(string path)
    {
        var trimmed = path.TrimEnd('\\', '/');
        var i = trimmed.LastIndexOfAny(['\\', '/']);
        return i < 0 ? trimmed : trimmed[(i + 1)..];
    }

    private static bool FromApp(Clip clip, string[] apps) =>
        apps.Length == 0
        || (clip.SourceApp is { } app && Array.Exists(apps, a => app.Contains(a, StringComparison.OrdinalIgnoreCase)));

    private static bool MatchesSubstring(Clip clip, string term) =>
        (clip.Text?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false)
        || (clip.Title?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false)
        || clip.Files.Exists(f => FileName(f).Contains(term, StringComparison.OrdinalIgnoreCase));

    /// <summary>Only the start of a long Clip text is matched fuzzily: further in, word initials match almost anything.</summary>
    public const int FuzzyTextLimit = 2000;

    // Word initials match much long text, which is why these hits rank after the substring hits.
    // Called only after the substring check failed, so it checks initials alone.
    private static bool MatchesFuzzily(Clip clip, string term) =>
        (clip.Text is { } text && FilterMatcher.MatchesInitials(text.AsSpan(0, Math.Min(text.Length, FuzzyTextLimit)), term))
        || (clip.Title is { } title && FilterMatcher.MatchesInitials(title, term))
        || clip.Files.Exists(f => FilterMatcher.MatchesInitials(FileName(f), term));
}
