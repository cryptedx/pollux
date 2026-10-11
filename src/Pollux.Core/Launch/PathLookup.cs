using Pollux.Core.Navigation;

namespace Pollux.Core.Launch;

/// <summary>
/// Finds a tool the way Windows would on PATH, minus the places a planted file could stand in for it: the current
/// directory and Pollux's own folder are never searched, not even when PATH lists them, and neither is any relative
/// PATH entry (S-02, S-08).
/// </summary>
public static class PathLookup
{
    public const string DefaultPathExt = ".COM;.EXE;.BAT;.CMD";

    /// <summary>
    /// The absolute path of <paramref name="name"/>, or null. A bare name is searched in the absolute PATH entries;
    /// without an extension from PATHEXT each extension is tried in order. An absolute path is used when it exists;
    /// a relative path with a folder in it is never resolved.
    /// </summary>
    /// <param name="excludedDirectories">Folders never searched (the current directory, Pollux's own folder).</param>
    public static string? Find(string name, string? pathVariable, string? pathExt, IReadOnlyCollection<string> excludedDirectories, Func<string, bool> fileExists)
    {
        name = name.Trim().Trim('"').Trim();
        if (name.Length == 0) return null;
        var extensions = (string.IsNullOrWhiteSpace(pathExt) ? DefaultPathExt : pathExt)
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var candidates = HasExtension(name, extensions) ? [name] : extensions.Select(e => name + e.ToLowerInvariant()).ToArray();

        if (name.Contains('\\') || name.Contains('/'))
        {
            if (!IsAbsolute(name)) return null;
            return candidates.Select(WinPath.Normalize).FirstOrDefault(fileExists);
        }

        foreach (var raw in SplitPath(pathVariable ?? ""))
        {
            var dir = raw.Trim();
            if (!IsAbsolute(dir)) continue;
            if (excludedDirectories.Any(x => x.Length > 0 && WinPath.Normalize(x).Equals(WinPath.Normalize(dir), StringComparison.OrdinalIgnoreCase))) continue;
            foreach (var candidate in candidates)
            {
                var full = WinPath.Combine(dir, candidate);
                if (fileExists(full)) return full;
            }
        }

        return null;
    }

    /// <summary>A drive-rooted ("C:\…") or UNC path; "C:foo", "\foo", "." and "%VAR%\x" are not.</summary>
    public static bool IsAbsolute(string path) =>
        WinPath.IsDriveRooted(path) || (WinPath.IsUnc(path) && path.Length > 2);

    /// <summary>PATH entries split on ';' outside double quotes, quotes removed.</summary>
    private static List<string> SplitPath(string path)
    {
        List<string> entries = [];
        var current = new System.Text.StringBuilder();
        var quoted = false;
        foreach (var c in path)
        {
            if (c == '"') quoted = !quoted;
            else if (c == ';' && !quoted) { entries.Add(current.ToString()); current.Clear(); }
            else current.Append(c);
        }

        entries.Add(current.ToString());
        return entries;
    }

    private static bool HasExtension(string name, string[] extensions) =>
        extensions.Any(e => name.EndsWith(e, StringComparison.OrdinalIgnoreCase));
}
