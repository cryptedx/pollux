namespace Pollux.Core.Session;

/// <summary>
/// What a command line asks Pollux to do. One interpretation shared by a cold start and by a second
/// launch redirected to the resident process; how the flags combine (precedence) is up to the caller.
/// </summary>
public sealed record LaunchRequest
{
    public bool Quit { get; init; }
    public bool Background { get; init; }
    public bool Downloads { get; init; }
    public bool Clipboard { get; init; }
    public bool Notes { get; init; }

    /// <summary>Name after "--workspace".</summary>
    public string? Workspace { get; init; }

    /// <summary>"--command id[,id…]": command ids, trimmed, empty entries dropped.</summary>
    public IReadOnlyList<string> Commands { get; init; } = [];

    /// <summary>Full path of the first positional argument if it is an existing folder or file.</summary>
    public string? Target { get; init; }

    /// <summary>True for flags that make a cold start run without showing a window (unless a target is given).</summary>
    public bool WantsNoWindow => Background || Notes || Downloads || Clipboard;

    public static LaunchRequest Parse(string[] args, Func<string, bool> dirExists, Func<string, bool> fileExists)
    {
        // A redirected command line starts with the program path.
        if (args.Length > 0 && args[0].EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) args = args[1..];

        return new LaunchRequest
        {
            Quit = args.Contains("--quit"),
            Background = args.Contains("--background"),
            Downloads = args.Contains("--downloads"),
            Clipboard = args.Contains("--clipboard"),
            Notes = args.Contains("--notes"),
            Workspace = ValueAfter(args, "--workspace"),
            Commands = ValueAfter(args, "--command") is { } list
                ? list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                : [],
            Target = TargetFrom(args, dirExists, fileExists),
        };
    }

    private static string? ValueAfter(string[] args, string flag)
    {
        var i = Array.IndexOf(args, flag);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static string? TargetFrom(string[] args, Func<string, bool> dirExists, Func<string, bool> fileExists)
    {
        var path = args.Where((a, i) => !a.StartsWith("--", StringComparison.Ordinal) && (i == 0 || (args[i - 1] != "--command" && args[i - 1] != "--workspace"))).FirstOrDefault();
        return path is not null && (dirExists(path) || fileExists(path)) ? Path.GetFullPath(path) : null;
    }
}
