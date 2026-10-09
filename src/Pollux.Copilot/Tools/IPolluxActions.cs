namespace Pollux.Copilot.Tools;

/// <summary>One entry of a Pane's listing.</summary>
public sealed record PaneEntry(string Name, bool IsFolder);

/// <summary>
/// What one Pane shows. <paramref name="Pane"/> is "left" or "right"; <paramref name="Folder"/> is null on Home;
/// <paramref name="Tabs"/> are the locations of the Pane's Tabs; <paramref name="Selection"/> holds full paths.
/// </summary>
public sealed record PaneSnapshot(
    string Pane,
    bool Active,
    string? Folder,
    IReadOnlyList<string> Tabs,
    int ActiveTab,
    IReadOnlyList<string> Selection,
    IReadOnlyList<PaneEntry> Entries);

/// <summary>The outcome of opening a location in a Pane.</summary>
public sealed record NavigateResult(bool Ok, string? Location, int EntryCount, string? Error);

/// <summary>
/// What Pollux does for Copilot's Pollux tools. Implemented by the app over the window's Panes; every member runs on
/// the UI thread inside the implementation, so callers may use any thread. <c>pane</c> is one of
/// <see cref="PaneTargets"/> (already validated by the tools).
/// </summary>
public interface IPolluxActions
{
    /// <summary>Location, Tabs, selection and listing of every Pane (both when the window is dual).</summary>
    Task<IReadOnlyList<PaneSnapshot>> ReadPanesAsync(CancellationToken cancellationToken);

    /// <summary>Opens <paramref name="location"/> (a folder path or "home") in the Pane, in its current Tab or in a new one, and waits until it is listed.</summary>
    Task<NavigateResult> NavigateAsync(string pane, string location, bool newTab, CancellationToken cancellationToken);

    /// <summary>Selects exactly the entries with these names in the Pane (replacing its selection); returns how many were selected.</summary>
    Task<int> SelectAsync(string pane, IReadOnlyList<string> names, CancellationToken cancellationToken);
}

/// <summary>The ways a tool can name a Pane.</summary>
public static class PaneTargets
{
    public const string Active = "active";
    public const string Other = "other";
    public const string Left = "left";
    public const string Right = "right";

    public const string Names = "left, right, active or other";

    /// <summary>The canonical target for a user-given name (case-insensitive; null or empty means the active Pane), or null when unknown.</summary>
    public static string? Normalize(string? name) => name?.Trim().ToLowerInvariant() switch
    {
        null or "" => Active,
        Active => Active,
        Other => Other,
        Left => Left,
        Right => Right,
        _ => null,
    };

    /// <summary>The snapshot a target names, or null when there is none (e.g. "other" in a single-Pane window).</summary>
    public static PaneSnapshot? Resolve(IReadOnlyList<PaneSnapshot> panes, string target) => target switch
    {
        Active => panes.FirstOrDefault(p => p.Active),
        Other => panes.FirstOrDefault(p => !p.Active),
        _ => panes.FirstOrDefault(p => p.Pane == target),
    };
}
