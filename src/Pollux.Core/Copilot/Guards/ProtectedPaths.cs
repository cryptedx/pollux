using System.Text.RegularExpressions;
using Pollux.Core.Navigation;

namespace Pollux.Core.Copilot.Guards;

/// <summary>
/// Protected secrets (spec list 3): paths Copilot may never read or write. A pattern with a backslash or slash is a
/// location (with ~, %VARS% and * / ? wildcards) that protects itself and everything below it; a pattern without one
/// is a file or folder name matched against every segment of a path (e.g. ".env*", "*.pfx").
/// </summary>
public sealed class ProtectedPaths
{
    public static IReadOnlyList<string> Defaults { get; } =
    [
        @"~\.ssh",
        ".env*",
        @"%APPDATA%\Microsoft\Credentials",
        @"%LOCALAPPDATA%\Microsoft\Credentials",
        @"%LOCALAPPDATA%\Google\Chrome\User Data",
        @"%LOCALAPPDATA%\Microsoft\Edge\User Data",
        @"%LOCALAPPDATA%\BraveSoftware\Brave-Browser\User Data",
        @"%LOCALAPPDATA%\Chromium\User Data",
        @"%LOCALAPPDATA%\Vivaldi\User Data",
        @"%APPDATA%\Opera Software",
        @"%APPDATA%\Mozilla\Firefox\Profiles",
        @"~\.aws",
        @"~\.azure",
        "*.pfx",
        "*.key",
        @"%APPDATA%\Pollux",
        @"%LOCALAPPDATA%\Pollux",
    ];

    private readonly PathPatterns _patterns;

    public ProtectedPaths(IEnumerable<string>? extra = null)
    {
        _patterns = new([.. Defaults, .. extra ?? []]);
    }

    /// <summary>The pattern protecting <paramref name="path"/> (already resolved to an absolute path), or null.</summary>
    public string? Match(string path, GuardContext context) => _patterns.Match(path, context);

    /// <summary>The pattern of a protected location at or below <paramref name="folder"/> (resolved), or null.</summary>
    public string? Below(string folder, GuardContext context) => _patterns.Below(folder, context);

    /// <summary>
    /// The name pattern (".env*", "*.key", ...) matching a name of <paramref name="relativePath"/> (no wildcards), or null.
    /// Location patterns are not looked at: for a path below a searched folder, <see cref="Below"/> covers them.
    /// </summary>
    public string? MatchName(string relativePath) => _patterns.MatchName(relativePath);
}

/// <summary>
/// A list of path patterns: a pattern with a backslash or slash is a location (with ~, %VARS% and * / ? wildcards)
/// that covers itself and everything below it; a pattern without one is a file or folder name matched against every
/// segment of a path (e.g. ".env*", "*.zip"). The path itself may contain wildcards (*, ?, [abc]): it then matches when
/// any name it could stand for would (fail closed).
/// </summary>
public sealed class PathPatterns
{
    private readonly List<string> _patterns;

    public PathPatterns(IEnumerable<string> patterns)
    {
        _patterns = [.. patterns.Where(p => !string.IsNullOrWhiteSpace(p))];
        _names = [.. _patterns.Where(p => !IsLocation(p))];
    }

    private readonly List<string> _names;

    /// <summary>The first pattern covering <paramref name="path"/> (already resolved to an absolute path), or null.</summary>
    public string? Match(string path, GuardContext context)
    {
        var segments = Split(path);
        foreach (var pattern in _patterns)
        {
            if (IsLocation(pattern))
            {
                var root = Split(GuardPaths.Resolve(pattern, context));
                if (segments.Length >= root.Length && root.Select((r, i) => Glob.Intersect(r, segments[i])).All(hit => hit))
                {
                    return pattern;
                }
            }
            else if (segments.Skip(1).Any(s => NameMatches(pattern, s)))
            {
                return pattern;
            }
        }

        return null;
    }

    /// <summary>The first name pattern matching a name of <paramref name="relativePath"/>, or null (location patterns are skipped).</summary>
    public string? MatchName(string relativePath)
    {
        var segments = relativePath.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        foreach (var pattern in _names)
        {
            foreach (var segment in segments)
            {
                if (NameMatches(pattern, segment))
                {
                    return pattern;
                }
            }
        }

        return null;
    }

    /// <summary>The first location pattern at or below <paramref name="folder"/> (already resolved), or null: a recursive read of the folder reaches it.</summary>
    public string? Below(string folder, GuardContext context)
    {
        var segments = Split(folder);
        foreach (var pattern in _patterns.Where(IsLocation))
        {
            var root = Split(GuardPaths.Resolve(pattern, context));
            if (segments.Length <= root.Length && segments.Select((s, i) => Glob.Intersect(root[i], s)).All(hit => hit))
            {
                return pattern;
            }
        }

        return null;
    }

    /// <summary>A name pattern against one name of the path; a wildcard at the start of the name does not stand for a leading dot (as in POSIX shells), so *.cs is not taken for .env.cs.</summary>
    private static bool NameMatches(string pattern, string name) =>
        !(pattern.StartsWith('.') && !name.StartsWith('.')) && Glob.Intersect(pattern, name);

    private static bool IsLocation(string pattern) =>
        pattern.Contains('\\', StringComparison.Ordinal) || pattern.Contains('/', StringComparison.Ordinal);

    /// <summary>The names of a path; a [set] wildcard becomes ? (it stands for one character).</summary>
    private static string[] Split(string path) =>
        [.. WinPath.Normalize(path).Split('\\', StringSplitOptions.RemoveEmptyEntries).Select(Glob.SimplifySets)];
}

/// <summary>Windows-style wildcards (* and ? within one name) without regular expressions.</summary>
public static class Glob
{
    /// <summary>True when <paramref name="text"/> contains a wildcard (*, ? or a [set]).</summary>
    public static bool HasWildcard(string text) => text.AsSpan().IndexOfAny('*', '?', '[') >= 0;

    /// <summary>Replaces every [set] by ?, the one character it stands for.</summary>
    public static string SimplifySets(string name)
    {
        var open = name.IndexOf('[', StringComparison.Ordinal);
        if (open < 0) return name;
        var close = name.IndexOf(']', open + 1);
        return close < 0 ? name : name[..open] + "?" + SimplifySets(name[(close + 1)..]);
    }

    /// <summary>
    /// Whether some name matches both <paramref name="a"/> and <paramref name="b"/> (either may have * and ?; case-insensitive).
    /// For a plain name this is "the pattern matches the name".
    /// </summary>
    public static bool Intersect(string a, string b)
    {
        // can[i, j]: some string matches both a[i..] and b[j..].
        var can = new bool[a.Length + 1, b.Length + 1];
        can[a.Length, b.Length] = true;
        for (var i = a.Length; i >= 0; i--)
        {
            for (var j = b.Length; j >= 0; j--)
            {
                if (i == a.Length && j == b.Length) continue;
                var v = false;
                if (i < a.Length && a[i] == '*') v = can[i + 1, j] || (j < b.Length && can[i, j + 1]);
                if (!v && j < b.Length && b[j] == '*') v = can[i, j + 1] || (i < a.Length && can[i + 1, j]);
                if (!v && i < a.Length && j < b.Length && a[i] != '*' && b[j] != '*' && Same(a[i], b[j])) v = can[i + 1, j + 1];
                can[i, j] = v;
            }
        }

        return can[0, 0];
    }

    private static bool Same(char x, char y) => x == '?' || y == '?' || char.ToUpperInvariant(x) == char.ToUpperInvariant(y);
}

/// <summary>Turns paths as Copilot writes them (relative, ~, %VARS%, $env:VARS, $HOME) into absolute Windows paths.</summary>
public static partial class GuardPaths
{
    /// <summary>The local path a file: URI names (file:///C:/x, file://localhost/C:/x, file://server/share/x), or null for anything else.</summary>
    public static string? FromFileUri(string text)
    {
        if (!text.StartsWith("file:", StringComparison.OrdinalIgnoreCase)) return null;
        var rest = Uri.UnescapeDataString(text[5..]).Replace('\\', '/');
        if (rest.StartsWith("//", StringComparison.Ordinal))
        {
            rest = rest[2..];
            var slash = rest.IndexOf('/', StringComparison.Ordinal);
            var host = slash < 0 ? rest : rest[..slash];
            rest = slash < 0 ? "" : rest[slash..];
            if (host.Length > 0 && !host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return @"\\" + host + rest.Replace('/', '\\');
        }

        var local = rest.TrimStart('/');
        if (local.Length >= 2 && char.IsLetter(local[0]) && local[1] is ':' or '|') return local[0] + ":" + local[2..].Replace('/', '\\');
        return rest.Replace('/', '\\');
    }

    /// <summary>Replaces $env:NAME, ${env:NAME}, $HOME and ${HOME} by their values where known; unknown ones stay.</summary>
    public static string ExpandShellVariables(string text, GuardContext context) => ShellVariable().Replace(text, m =>
    {
        var name = m.Groups["name"].Value;
        var value = name.Equals("HOME", StringComparison.OrdinalIgnoreCase) ? context.Environment("USERPROFILE") : context.Environment(name);
        return value ?? m.Value;
    });

    public static string Resolve(string path, GuardContext context)
    {
        var expanded = ExpandShellVariables(path, context).Replace('/', '\\');

        // "\Windows" and "/tmp/x" are rooted on the Working Folder's drive, not relative to it.
        if (expanded.StartsWith('\\') && !WinPath.IsUnc(expanded) && context.WorkingFolder.Length >= 2 && context.WorkingFolder[1] == ':')
        {
            expanded = context.WorkingFolder[..2] + expanded;
        }

        return WinPath.Resolve(expanded, context.WorkingFolder, context.Environment);
    }

    [GeneratedRegex(@"\$\{env:(?<name>[A-Za-z_][A-Za-z0-9_]*)\}|\$env:(?<name>[A-Za-z_][A-Za-z0-9_]*)|\$\{(?<name>HOME)\}|\$(?<name>HOME)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ShellVariable();
}
