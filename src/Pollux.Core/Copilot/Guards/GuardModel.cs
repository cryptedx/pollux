namespace Pollux.Core.Copilot.Guards;

/// <summary>What kind of action Copilot wants to take.</summary>
public enum ToolKind
{
    /// <summary>A shell command (PowerShell, cmd or POSIX shell).</summary>
    Shell,
    /// <summary>Reading files or folders.</summary>
    Read,
    /// <summary>Creating, changing, moving or deleting files or folders.</summary>
    Write,
    /// <summary>Fetching a URL (e.g. the CLI's web_fetch tool).</summary>
    Url,
    /// <summary>Anything else (e.g. Pollux tools that only navigate or select).</summary>
    Other,
}

public enum GuardDecision { Allow, Ask, Deny }

/// <summary>The Guard Rules' answer to a tool request, with the reason shown to the user and written to the action log.</summary>
public sealed record GuardVerdict(GuardDecision Decision, string Reason)
{
    public static GuardVerdict Allowed { get; } = new(GuardDecision.Allow, "");

    /// <summary>Every single reason (<see cref="Reason"/> joins them for display).</summary>
    public IReadOnlyList<string> Reasons { get; init; } = Reason.Length == 0 ? [] : [Reason];

    /// <summary>A verdict with several reasons.</summary>
    public static GuardVerdict Of(GuardDecision decision, IReadOnlyList<string> reasons) =>
        new(decision, string.Join("; ", reasons)) { Reasons = [.. reasons] };
}

/// <summary>A tool request as seen by the Guard Rules: kind, full command text, paths and URLs.</summary>
public sealed record ToolRequest(ToolKind Kind, string? Command, IReadOnlyList<string> Paths, IReadOnlyList<string> Urls)
{
    /// <summary>The request reads everything below its folders (a recursive search such as the CLI's grep and glob tools).</summary>
    public bool Recursive { get; init; }

    /// <summary>
    /// The request opens its location in a Pollux Pane (pollux_navigate): Pollux itself lists the folder, so a network
    /// location asks (the listing would authenticate to the host, SECURITY.md S-06).
    /// </summary>
    public bool Navigates { get; init; }

    public static ToolRequest Shell(string command) => new(ToolKind.Shell, command, [], []);
    public static ToolRequest Read(params string[] paths) => new(ToolKind.Read, null, paths, []);
    public static ToolRequest Write(params string[] paths) => new(ToolKind.Write, null, paths, []);
    public static ToolRequest Url(params string[] urls) => new(ToolKind.Url, null, [], urls);
    /// <summary>Opening <paramref name="location"/> (a folder path) in a Pollux Pane: a read of it.</summary>
    public static ToolRequest Navigate(string location) => new(ToolKind.Read, null, [location], []) { Navigates = true };

    public static ToolRequest Other() => new(ToolKind.Other, null, [], []);
}

/// <summary>
/// The Chat state a request is judged in. <paramref name="Environment"/> resolves USERPROFILE, APPDATA and LOCALAPPDATA
/// (and any %VAR% in paths); relative paths are resolved against the Working Folder.
/// </summary>
public sealed record GuardContext(string WorkingFolder, bool Tainted, Func<string, string?> Environment);
