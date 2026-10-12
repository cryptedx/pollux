using Pollux.Core.Files;

namespace Pollux.Core.Navigation;

/// <summary>A suggestion: <see cref="Text"/> replaces the whole typed text, <see cref="Display"/> is what the list shows.</summary>
public sealed record PathCandidate(string Text, string Display, bool IsDirectory, bool IsWellKnown = false);

/// <summary>
/// Suggestions for typed text. <see cref="Candidates"/> is capped, <see cref="More"/> counts the ones left out;
/// <see cref="CommonPrefix"/> (over all matches) is what Tab inserts first.
/// </summary>
public sealed record PathCompletionResult(IReadOnlyList<PathCandidate> Candidates, int More, string CommonPrefix);

/// <summary>
/// Path bar autocomplete like Explorer's: folders and files, case-insensitive prefix match on the last segment, folders first.
/// Keeps the typed spelling of everything before the last segment (quotes, ~, %VAR%, slash style).
/// </summary>
public static class PathCompletion
{
    public const int DefaultMax = 8;

    private static readonly string[] WellKnownNames = ["Home", "Recycle Bin"];

    public static PathCompletionResult Complete(
        string text,
        string currentDirectory,
        bool showHidden,
        IDirectoryLister lister,
        Func<string, string?> getEnvironmentVariable,
        int max = DefaultMax,
        CancellationToken cancellationToken = default)
    {
        var typed = text.TrimStart();
        var quote = typed.StartsWith('"') ? "\"" : "";
        var body = typed[quote.Length..];
        if (body.Trim().Length == 0 || body.EndsWith('"') || body.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            return Empty(typed);
        }

        string dirPart;
        string namePart;
        var cut = body.LastIndexOfAny(['\\', '/']);
        if (cut >= 0)
        {
            dirPart = body[..(cut + 1)];
            namePart = body[(cut + 1)..];
        }
        else if (body.Length >= 2 && char.IsAsciiLetter(body[0]) && body[1] == ':')
        {
            if (body.Length == 2)
            {
                return Single(quote + body + "\\", body + "\\");
            }

            dirPart = body[..2] + "\\";
            namePart = body[2..];
        }
        else if (body == "~")
        {
            return Single(quote + "~\\", "~\\");
        }
        else if (body.Contains('%') || body.Contains(':'))
        {
            var closedVariable = body.Length > 2 && body[0] == '%' && body.IndexOf('%', 1) == body.Length - 1
                && getEnvironmentVariable(body[1..^1]) is not null;
            return closedVariable ? Single(quote + body + "\\", body + "\\") : Empty(typed);
        }
        else
        {
            dirPart = "";
            namePart = body;
        }

        var directory = ResolveDirectory(dirPart, currentDirectory, getEnvironmentVariable);
        var separator = dirPart.Contains('/') && !dirPart.Contains('\\') ? "/" : "\\";
        var prefix = quote + dirPart;

        var folders = new List<PathCandidate>();
        var files = new List<PathCandidate>();
        if (directory is not null)
        {
            var items = SafeList(lister, directory, cancellationToken)
                .Where(i => (showHidden || !i.IsHidden) && i.Name.StartsWith(namePart, StringComparison.OrdinalIgnoreCase))
                .OrderBy(i => i.Name, Comparer<string>.Create(EntryComparer.NaturalCompare));
            foreach (var item in items)
            {
                if (item.IsDirectory)
                {
                    folders.Add(new PathCandidate(prefix + item.Name + separator, item.Name, true));
                }
                else
                {
                    files.Add(new PathCandidate(prefix + item.Name, item.Name, false));
                }
            }
        }

        var wellKnown = new List<PathCandidate>();
        if (dirPart.Length == 0)
        {
            foreach (var name in WellKnownNames)
            {
                if (name.StartsWith(namePart, StringComparison.OrdinalIgnoreCase))
                {
                    wellKnown.Add(new PathCandidate(quote + name, name, true, IsWellKnown: true));
                }
            }
        }

        var all = folders.Concat(wellKnown).Concat(files).ToList();
        if (all.Count == 0)
        {
            return Empty(typed);
        }

        var shown = all.Count > max ? all.Take(max).ToList() : all;
        return new PathCompletionResult(shown, all.Count - shown.Count, CommonPrefix(all.Select(c => c.Text)));
    }

    /// <summary>The longest case-insensitive common start of the texts, spelled like the first one.</summary>
    public static string CommonPrefix(IEnumerable<string> texts)
    {
        string? first = null;
        var length = 0;
        foreach (var t in texts)
        {
            if (first is null)
            {
                first = t;
                length = t.Length;
                continue;
            }

            var n = 0;
            while (n < length && n < t.Length && char.ToUpperInvariant(first[n]) == char.ToUpperInvariant(t[n]))
            {
                n++;
            }

            length = n;
        }

        return first is null ? "" : first[..length];
    }

    private static PathCompletionResult Empty(string typed) => new([], 0, typed);

    private static PathCompletionResult Single(string text, string display) =>
        new([new PathCandidate(text, display, true)], 0, text);

    private static IReadOnlyList<DirectoryItem> SafeList(IDirectoryLister lister, string directory, CancellationToken token)
    {
        try
        {
            return lister.List(directory, token);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>The absolute folder the typed directory part stands for, or null when it names no listable location.</summary>
    private static string? ResolveDirectory(string dirPart, string currentDirectory, Func<string, string?> getEnvironmentVariable)
    {
        var current = WinPath.Normalize(currentDirectory);
        var currentIsReal = IsListable(current);
        if (dirPart.Length == 0)
        {
            return currentIsReal ? current : null;
        }

        var d = dirPart;
        var uncStart = d.Length >= 2 && d[0] is '\\' or '/' && d[1] is '\\' or '/';
        if (!uncStart && d[0] is '\\' or '/')
        {
            if (!currentIsReal)
            {
                return null;
            }

            d = WinPath.Segments(current)[0].TrimEnd('\\') + d;
        }

        var resolved = WinPath.Resolve(d, currentIsReal ? current : @"\", getEnvironmentVariable);
        return IsListable(resolved) ? resolved : null;
    }

    /// <summary>A drive path or a UNC path that includes the share.</summary>
    private static bool IsListable(string path) =>
        WinPath.IsDriveRooted(path) || (WinPath.IsUnc(path) && path[2..].Contains('\\'));
}
