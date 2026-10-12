using System.Text;

namespace Pollux.Core.Navigation;

/// <summary>
/// Windows path helpers implemented without System.IO.Path, so behaviour is identical on every OS the tests run on.
/// Paths are normalized: backslashes, no trailing separator except on drive roots ("C:\").
/// </summary>
public static class WinPath
{
    public const string Home = "pollux:home";

    /// <summary>The Windows Recycle Bin, shown read-only in a Tab (it spans all drives, so it is no folder).</summary>
    public const string RecycleBin = "pollux:recycle-bin";

    /// <summary>This PC: drives, phones and cameras (a shell namespace folder, see <see cref="Locations"/>).</summary>
    public const string ThisPc = "pollux:this-pc";

    /// <summary>The computers Windows discovered on the network; only listed when the user goes there (ADR 0010).</summary>
    public const string Network = "pollux:network";

    /// <summary>Control Panel, by its shell parsing name.</summary>
    public const string ControlPanel = "::{26EE0668-A00A-44D7-9371-BEB064C98683}";

    /// <summary>The Libraries folder, by its shell moniker.</summary>
    public const string Libraries = "shell:Libraries";

    /// <summary>
    /// The location a typed name stands for ("home", "recycle bin", "this pc", "network", "control panel", "libraries" and
    /// their shell: monikers), or null for anything else.
    /// </summary>
    public static string? WellKnown(string text) => text.Trim().ToLowerInvariant() switch
    {
        "home" or Home => Home,
        "recycle bin" or "recyclebin" or "shell:recyclebinfolder" or RecycleBin => RecycleBin,
        "this pc" or "thispc" or "computer" or "my computer" or "shell:mycomputerfolder" or ThisPc => ThisPc,
        "network" or "shell:networkplacesfolder" or Network => Network,
        "control panel" or "controlpanel" or "shell:controlpanelfolder" or "::{26ee0668-a00a-44d7-9371-beb064c98683}" => ControlPanel,
        "libraries" or "shell:libraries" => Libraries,
        _ => null,
    };

    public static bool IsUnc(string path) => path.StartsWith(@"\\", StringComparison.Ordinal);

    /// <summary>
    /// A location that means the same from every current folder, as written: a drive path (<c>C:\x</c>), a UNC or device
    /// path (<c>\\host\x</c>, <c>//host/x</c>) or a file: URI. Not <c>src</c>, <c>~\x</c>, <c>%VAR%\x</c>, <c>C:x</c> or <c>\x</c>.
    /// </summary>
    public static bool IsAbsoluteLocation(string location)
    {
        var t = location.Trim().Trim('"');
        return IsDriveRooted(t) || t.StartsWith(@"\\", StringComparison.Ordinal) || t.StartsWith("//", StringComparison.Ordinal)
            || t.StartsWith("file:", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A path rooted at a drive letter ("C:\x", "C:/x"); not "C:x" (relative to the drive's current folder).</summary>
    public static bool IsDriveRooted(string path) =>
        path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] is '\\' or '/';

    public static string Normalize(string path)
    {
        var p = path.Trim().Trim('"').Replace('/', '\\');
        if (p.Length >= 2 && p[1] == ':' && char.IsLetter(p[0]))
        {
            p = char.ToUpperInvariant(p[0]) + p[1..];
            if (p.Length == 2)
            {
                return p + "\\";
            }
        }

        var unc = IsUnc(p);
        var body = unc ? p[2..] : p;
        while (body.Contains(@"\\", StringComparison.Ordinal))
        {
            body = body.Replace(@"\\", @"\", StringComparison.Ordinal);
        }

        p = unc ? @"\\" + body : body;
        if (p.Length > 3 && p.EndsWith('\\'))
        {
            p = p.TrimEnd('\\');
        }

        return p;
    }

    /// <summary>Root first, then every ancestor down to the path itself.</summary>
    public static IReadOnlyList<string> Segments(string path)
    {
        var p = Normalize(path);
        var result = new List<string>();
        string root;
        string rest;
        if (IsUnc(p))
        {
            var parts = p[2..].Split('\\');
            if (parts.Length < 2)
            {
                return [p];
            }

            root = $@"\\{parts[0]}\{parts[1]}";
            rest = string.Join('\\', parts.Skip(2));
        }
        else if (p.Length >= 3 && p[1] == ':')
        {
            root = p[..3];
            rest = p[3..];
        }
        else
        {
            return [p];
        }

        result.Add(root);
        var acc = root;
        foreach (var part in rest.Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            acc = acc.EndsWith('\\') ? acc + part : acc + "\\" + part;
            result.Add(acc);
        }

        return result;
    }

    public static string? Parent(string path)
    {
        var segments = Segments(path);
        return segments.Count >= 2 ? segments[^2] : null;
    }

    public static string Name(string path)
    {
        var p = Normalize(path);
        if (p.Length == 3 && p[1] == ':')
        {
            return p[..2];
        }

        var i = p.LastIndexOf('\\');
        return i < 0 ? p : p[(i + 1)..];
    }

    public static string Combine(string directory, string name)
    {
        var d = Normalize(directory);
        return d.EndsWith('\\') ? d + name : d + "\\" + name;
    }

    public static bool IsSameOrUnder(string path, string root)
    {
        var p = Normalize(path);
        var r = Normalize(root);
        if (p.Equals(r, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var prefix = r.EndsWith('\\') ? r : r + "\\";
        return p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Turns what the user typed into an absolute path: quotes, ~, %VARS%, forward slashes, relative paths.</summary>
    public static string Resolve(string input, string currentDirectory, Func<string, string?> getEnvironmentVariable)
    {
        var t = input.Trim().Trim('"').Trim();
        if (t == "~" || t.StartsWith("~\\", StringComparison.Ordinal) || t.StartsWith("~/", StringComparison.Ordinal))
        {
            t = (getEnvironmentVariable("USERPROFILE") ?? "~") + t[1..];
        }

        t = ExpandVariables(t, getEnvironmentVariable).Replace('/', '\\');

        var absolute = IsUnc(t) || (t.Length >= 2 && t[1] == ':' && char.IsLetter(t[0]));
        var combined = absolute ? t : Combine(currentDirectory, t);
        return Collapse(Normalize(combined));
    }

    private static string ExpandVariables(string text, Func<string, string?> getEnvironmentVariable)
    {
        var sb = new StringBuilder();
        var i = 0;
        while (i < text.Length)
        {
            var start = text.IndexOf('%', i);
            var end = start < 0 ? -1 : text.IndexOf('%', start + 1);
            if (start < 0 || end < 0)
            {
                sb.Append(text, i, text.Length - i);
                break;
            }

            sb.Append(text, i, start - i);
            var name = text[(start + 1)..end];
            var value = name.Length > 0 ? getEnvironmentVariable(name) : null;
            sb.Append(value ?? text[start..(end + 1)]);
            i = end + 1;
        }

        return sb.ToString();
    }

    /// <summary>Resolves "." and ".." segments.</summary>
    private static string Collapse(string path)
    {
        var segments = Segments(path);
        if (segments.Count == 0)
        {
            return path;
        }

        var root = segments[0];
        var parts = new List<string>();
        foreach (var part in path[root.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".")
            {
                continue;
            }

            if (part == "..")
            {
                if (parts.Count > 0)
                {
                    parts.RemoveAt(parts.Count - 1);
                }

                continue;
            }

            parts.Add(part);
        }

        return parts.Aggregate(root, Combine);
    }
}
