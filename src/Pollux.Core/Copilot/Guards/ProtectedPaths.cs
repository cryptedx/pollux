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

        // Developer credential stores (SECURITY.md S-13).
        @"%APPDATA%\GitHub CLI\hosts.yml",
        @"~\.config\gh\hosts.yml",
        ".git-credentials",
        @"~\.config\git\credentials",
        ".npmrc",
        @"~\.docker\config.json",
        @"~\.kube\config",
        @"%APPDATA%\NuGet\NuGet.Config",
        "*.kdbx",

        // SSH private keys (id_rsa, id_ed25519_work, ...) wherever they were copied to. The trailing dot means "no
        // extension" as in cmd's dir *.: id_rsa.pub and src\id_generator.cs stay readable, and so does src\*.cs.
        "id_*.",
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

    /// <summary>
    /// A name pattern against one name of the path; a wildcard at the start of the name does not stand for a leading dot
    /// (as in POSIX shells), so *.cs is not taken for .env.cs. A pattern ending in a dot (<c>id_*.</c>) matches names
    /// without an extension only, as <c>dir *.</c> does in cmd.
    /// </summary>
    private static bool NameMatches(string pattern, string name)
    {
        if (pattern.Length > 1 && pattern.EndsWith('.'))
        {
            return !name.Contains('.', StringComparison.Ordinal) && Glob.Intersect(pattern[..^1], name);
        }

        return !(pattern.StartsWith('.') && !name.StartsWith('.')) && Glob.Intersect(pattern, name);
    }

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

        // file:////?/C:/x and file://localhost/C$/x: a device prefix or an admin share, canonicalised by Resolve.
        if (local.StartsWith("?/", StringComparison.Ordinal) || local.StartsWith("./", StringComparison.Ordinal)) return @"\\" + local.Replace('/', '\\');
        if (IsAdminShare(local.Split('/')[0])) return @"\\localhost\" + local.Replace('/', '\\');
        return rest.Replace('/', '\\');
    }

    /// <summary>Replaces $env:NAME, ${env:NAME}, $HOME and ${HOME} by their values where known; unknown ones stay.</summary>
    public static string ExpandShellVariables(string text, GuardContext context) => ShellVariable().Replace(text, m =>
    {
        var name = m.Groups["name"].Value;
        var value = name.Equals("HOME", StringComparison.OrdinalIgnoreCase) ? context.Environment("USERPROFILE") : context.Environment(name);
        return value ?? m.Value;
    });

    /// <summary>The prefix a resolved path keeps when it names a device namespace (see <see cref="IsDevicePath"/>).</summary>
    private const string DevicePrefix = @"\\?\";

    /// <summary>
    /// The absolute, canonical Windows path <paramref name="path"/> names, so that every spelling of a place is judged
    /// alike (SECURITY.md S-04, S-17): device prefixes (<c>\\?\</c>, <c>\\.\</c>, <c>\??\</c>, <c>\\?\UNC\</c>) are
    /// stripped, admin shares of the own machine (<c>\\localhost\C$</c>, <c>\\127.0.0.1\C$</c>, <c>\\&lt;COMPUTERNAME&gt;\C$</c>)
    /// become the drive, and alternate data streams (<c>name:stream</c>, <c>name::$DATA</c>) and trailing dots and spaces
    /// (which Windows drops) are removed from every name. A device namespace that is not a drive (GLOBALROOT, volumes,
    /// physical drives, pipes) stays as <c>\\?\...</c>; <see cref="IsDevicePath"/> tells it apart, and the Guard Rules refuse it.
    /// </summary>
    public static string Resolve(string path, GuardContext context)
    {
        var expanded = StripPrefixes(ExpandShellVariables(path, context).Trim().Trim('"').Trim().Replace('/', '\\'), context);
        if (IsDevicePath(expanded))
        {
            return expanded;
        }

        // "\Windows" and "/tmp/x" are rooted on the Working Folder's drive, not relative to it.
        if (expanded.StartsWith('\\') && !WinPath.IsUnc(expanded) && context.WorkingFolder.Length >= 2 && context.WorkingFolder[1] == ':')
        {
            expanded = context.WorkingFolder[..2] + expanded;
        }

        // Variables expanded by WinPath.Resolve may bring a prefix of their own.
        var resolved = StripPrefixes(WinPath.Resolve(expanded, context.WorkingFolder, context.Environment), context);
        return IsDevicePath(resolved) ? resolved : WinPath.Normalize(StripStreams(resolved));
    }

    /// <summary>Whether a path returned by <see cref="Resolve"/> names a device namespace (<c>\\?\GLOBALROOT\...</c>, <c>\\.\PhysicalDrive0</c>, ...).</summary>
    public static bool IsDevicePath(string resolved) => resolved.StartsWith(DevicePrefix, StringComparison.Ordinal);

    /// <summary>Strips device prefixes and maps own admin shares to drives until neither applies; leaves a device namespace as \\?\...</summary>
    private static string StripPrefixes(string path, GuardContext context)
    {
        var p = path;
        while (true)
        {
            if (p.StartsWith(@"\\?\", StringComparison.Ordinal) || p.StartsWith(@"\\.\", StringComparison.Ordinal) || p.StartsWith(@"\??\", StringComparison.Ordinal))
            {
                var rest = p[4..].TrimStart('\\');
                if (rest.StartsWith(@"UNC\", StringComparison.OrdinalIgnoreCase))
                {
                    p = @"\\" + rest[4..].TrimStart('\\');
                    continue;
                }

                if (rest.Length >= 2 && char.IsAsciiLetter(rest[0]) && rest[1] == ':' && (rest.Length == 2 || rest[2] == '\\'))
                {
                    p = rest;
                    continue;
                }

                return DevicePrefix + rest;
            }

            if (OwnAdminShare(p, context) is { } local)
            {
                p = local;
                continue;
            }

            return p;
        }
    }

    /// <summary>\\localhost\C$\x, \\127.0.0.1\C$\x or \\&lt;own host&gt;\C$\x as C:\x; null for anything else.</summary>
    private static string? OwnAdminShare(string path, GuardContext context)
    {
        if (!WinPath.IsUnc(path))
        {
            return null;
        }

        var parts = path[2..].Split('\\', 3);
        if (parts.Length < 2 || !IsAdminShare(parts[1]) || !IsOwnHost(parts[0], context))
        {
            return null;
        }

        return char.ToUpperInvariant(parts[1][0]) + @":\" + (parts.Length == 3 ? parts[2] : "");
    }

    private static bool IsAdminShare(string share) => share.Length == 2 && char.IsAsciiLetter(share[0]) && share[1] == '$';

    /// <summary>Loopback names and addresses, and the own computer name (also with a domain suffix, failing closed).</summary>
    private static bool IsOwnHost(string host, GuardContext context)
    {
        var h = host.Trim('[', ']');
        if (h.Equals("localhost", StringComparison.OrdinalIgnoreCase) || h.StartsWith("127.", StringComparison.Ordinal) || h is "::1" or "0:0:0:0:0:0:0:1" || h == ".")
        {
            return true;
        }

        var own = context.Environment("COMPUTERNAME");
        if (string.IsNullOrWhiteSpace(own))
        {
            return false;
        }

        var label = h.Split('.')[0];
        return label.Equals(own, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Removes alternate data streams and trailing dots and spaces from every name after the root (C: or the UNC server).</summary>
    private static string StripStreams(string path)
    {
        var unc = WinPath.IsUnc(path);
        var segments = (unc ? path[2..] : path).Split('\\');
        for (var i = 1; i < segments.Length; i++)
        {
            var name = segments[i];
            var colon = name.IndexOf(':', StringComparison.Ordinal);
            if (colon > 0)
            {
                name = name[..colon];
            }

            var trimmed = name.TrimEnd('.', ' ');
            segments[i] = trimmed.Length > 0 && name is not "." and not ".." ? trimmed : name;
        }

        return (unc ? @"\\" : "") + string.Join('\\', segments);
    }

    [GeneratedRegex(@"\$\{env:(?<name>[A-Za-z_][A-Za-z0-9_]*)\}|\$env:(?<name>[A-Za-z_][A-Za-z0-9_]*)|\$\{(?<name>HOME)\}|\$(?<name>HOME)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ShellVariable();
}
