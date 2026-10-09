using System.Text.Json.Serialization;
using GitHub.Copilot;
using Microsoft.Extensions.AI;

namespace Pollux.Copilot.Tools;

/// <summary>What <c>pollux_read_panes</c> returns.</summary>
public sealed record PanesResult(IReadOnlyList<PaneInfo> Panes);

/// <summary>One Pane as Copilot sees it; the listing is capped at <see cref="PolluxTools.MaxListedEntries"/>.</summary>
public sealed record PaneInfo(
    string Pane,
    bool Active,
    string? Folder,
    IReadOnlyList<string> Tabs,
    int ActiveTab,
    IReadOnlyList<string> Selection,
    int EntryCount,
    IReadOnlyList<PaneEntry> Entries,
    bool EntriesTruncated);

/// <summary>What <c>pollux_select_entries</c> returns.</summary>
public sealed record SelectEntriesResult(bool Ok, int Selected, IReadOnlyList<string> Names, string? Error);

/// <summary>
/// The Pollux tools Copilot gets for the Panes: read location and selection, open a location, select entries.
/// They need no approval: they only change what Pollux shows, never files (file operations come with their own approval).
/// </summary>
public static class PolluxTools
{
    /// <summary>Entries listed per Pane by <c>pollux_read_panes</c>; the rest is counted.</summary>
    public const int MaxListedEntries = 200;

    private static readonly CopilotToolOptions NoApproval = new() { SkipPermission = true };

    /// <summary>
    /// The tools for a Chat. With <paramref name="readOnly"/> only the tools that change nothing are offered. A Chat can
    /// switch to Plan mode after its session opened: the tools that change something then refuse while the
    /// <paramref name="gate"/> says so (<see cref="IPolluxToolGate.ReadOnlyReason"/>).
    /// The file operation and Bookmark tools come only with a <paramref name="gate"/> (the Chat's Guard Rules) and actions
    /// that can perform them (<see cref="IPolluxFileActions"/>).
    /// </summary>
    public static IReadOnlyList<AIFunction> Create(IPolluxActions actions, bool readOnly, IPolluxToolGate? gate = null)
    {
        var tools = new List<AIFunction>
        {
            CopilotTools.Define(
                async (CancellationToken ct) => ToResult(await actions.ReadPanesAsync(ct).ConfigureAwait(false)),
                "pollux_read_panes",
                "Reads the Pollux file manager's Panes: for the left and the right Pane its folder, Tabs, selected entries (full paths) and listing. Use this to learn what the user is looking at.",
                PolluxToolJson.Default, NoApproval),
        };

        if (!readOnly)
        {
            tools.Add(CopilotTools.Define(
                async (string location, string pane = PaneTargets.Active, bool newTab = false, CancellationToken ct = default) =>
                    gate?.ReadOnlyReason is { } planMode
                        ? new NavigateResult(false, null, 0, planMode)
                        : await NavigateAsync(actions, pane, location, newTab, ct).ConfigureAwait(false),
                "pollux_navigate",
                $"Opens a folder in a Pollux Pane ({PaneTargets.Names}; default active), in its current Tab or, with newTab, in a new Tab. location is a full Windows folder path or \"home\".",
                PolluxToolJson.Default, NoApproval));
            tools.Add(CopilotTools.Define(
                async (string[] patterns, string pane = PaneTargets.Active, string kind = "any", CancellationToken ct = default) =>
                    gate?.ReadOnlyReason is { } planMode
                        ? new SelectEntriesResult(false, 0, [], planMode)
                        : await SelectAsync(actions, pane, patterns, kind, ct).ConfigureAwait(false),
                "pollux_select_entries",
                $"Selects entries in a Pollux Pane ({PaneTargets.Names}; default active), replacing its selection. patterns are entry names with * and ? wildcards, e.g. \"*.pdf\"; kind is any, file or folder.",
                PolluxToolJson.Default, NoApproval));
            if (gate is not null && actions is IPolluxFileActions files) tools.AddRange(PolluxFileTools.Create(files, gate));
        }

        return tools;
    }

    private static PanesResult ToResult(IReadOnlyList<PaneSnapshot> panes) => new([.. panes.Select(p => new PaneInfo(
        p.Pane, p.Active, p.Folder, p.Tabs, p.ActiveTab, p.Selection, p.Entries.Count,
        [.. p.Entries.Take(MaxListedEntries)], p.Entries.Count > MaxListedEntries))]);

    private static async Task<NavigateResult> NavigateAsync(IPolluxActions actions, string pane, string location, bool newTab, CancellationToken ct)
    {
        if (PaneTargets.Normalize(pane) is not { } target) return new(false, null, 0, UnknownPane(pane));
        if (string.IsNullOrWhiteSpace(location)) return new(false, null, 0, "location is empty.");
        return await actions.NavigateAsync(target, location.Trim(), newTab, ct).ConfigureAwait(false);
    }

    private static async Task<SelectEntriesResult> SelectAsync(IPolluxActions actions, string pane, string[] patterns, string kind, CancellationToken ct)
    {
        if (PaneTargets.Normalize(pane) is not { } target) return new(false, 0, [], UnknownPane(pane));
        var snapshot = PaneTargets.Resolve(await actions.ReadPanesAsync(ct).ConfigureAwait(false), target);
        if (snapshot is null) return new(false, 0, [], $"There is no {target} Pane in this window.");

        var names = EntryPatterns.Match(snapshot.Entries, patterns ?? [], EntryPatterns.ParseKind(kind)).Select(e => e.Name).ToList();
        if (names.Count == 0) return new(false, 0, [], "No entry in the Pane matches the patterns; the selection is unchanged.");

        var selected = await actions.SelectAsync(target, names, ct).ConfigureAwait(false);
        return new(true, selected, [.. names.Take(MaxListedEntries)], null);
    }

    private static string UnknownPane(string pane) => $"Unknown Pane \"{pane}\"; use {PaneTargets.Names}.";
}

/// <summary>Source-generated JSON for the Pollux tools' results (no reflection under Native AOT).</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(PanesResult))]
[JsonSerializable(typeof(NavigateResult))]
[JsonSerializable(typeof(SelectEntriesResult))]
[JsonSerializable(typeof(FileOperationResult))]
internal sealed partial class PolluxToolJson : JsonSerializerContext;
