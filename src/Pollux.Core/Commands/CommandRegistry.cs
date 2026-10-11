using System.Globalization;
using Pollux.Core.Copilot;
using Pollux.Core.Settings;

namespace Pollux.Core.Commands;

/// <summary>What a command id asks for: most ids are plain, a few carry a parameter in the id itself.</summary>
public enum CommandKind
{
    /// <summary>A fixed command, run by its id (also unknown ids: the caller reports them).</summary>
    Plain,

    /// <summary><c>workspace.open:&lt;name&gt;</c> (--workspace, tray): <see cref="ParsedCommand.Workspace"/>.</summary>
    OpenWorkspace,

    /// <summary><c>bookmark.go&lt;n&gt;</c>, 1-based: <see cref="ParsedCommand.Bookmark"/>.</summary>
    GoToBookmark,

    /// <summary>A Copilot selection action or prompt template (<see cref="SelectionActions"/>).</summary>
    SelectionAction,
}

/// <summary>A command id taken apart by <see cref="CommandRegistry.Parse"/>.</summary>
public readonly record struct ParsedCommand(CommandKind Kind, string Id, string Workspace = "", int Bookmark = 0);

/// <summary>
/// The one list of commands for keybindings, the command palette and the Settings page, and the rules around command ids:
/// the Copilot gate, parameterised ids and how a new shortcut replaces the old one in keybindings.json.
/// </summary>
public static class CommandRegistry
{
    private const string WorkspacePrefix = "workspace.open:";
    private const string BookmarkPrefix = "bookmark.go";

    /// <summary>
    /// Every command, in catalog order, then the selection actions (titled "Copilot: …").
    /// While Copilot is unavailable the Copilot commands and the selection actions are left out.
    /// </summary>
    public static IReadOnlyList<CommandDefinition> Commands(CopilotSettings copilot, bool copilotAvailable)
    {
        ArgumentNullException.ThrowIfNull(copilot);
        var list = CommandCatalog.All.Where(c => CopilotAvailability.AllowsCommand(c.Id, copilotAvailable)).ToList();
        if (copilotAvailable)
        {
            list.AddRange(SelectionActions.For(copilot).Select(a => new CommandDefinition(a.CommandId, "Copilot: " + a.Title)));
        }

        return list;
    }

    /// <summary>
    /// Every command's default key with <paramref name="user"/> (keybindings.json) on top. Copilot commands are bound even while
    /// Copilot is unavailable: availability changes without a keybindings reload, and running a command checks the gate anyway.
    /// </summary>
    public static KeybindingMap Keybindings(CopilotSettings copilot, IEnumerable<KeybindingEntry> user) =>
        KeybindingMap.Build(Commands(copilot, copilotAvailable: true), user);

    /// <summary>Takes a command id apart. Never fails: anything that is not a well-formed parameterised id is <see cref="CommandKind.Plain"/>.</summary>
    public static ParsedCommand Parse(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        if (SelectionActions.IsSelectionAction(id))
        {
            return new(CommandKind.SelectionAction, id);
        }

        if (id.StartsWith(WorkspacePrefix, StringComparison.Ordinal) && id.Length > WorkspacePrefix.Length)
        {
            return new(CommandKind.OpenWorkspace, id, Workspace: id[WorkspacePrefix.Length..]);
        }

        if (id.StartsWith(BookmarkPrefix, StringComparison.Ordinal)
            && int.TryParse(id.AsSpan(BookmarkPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var n)
            && n >= 1)
        {
            return new(CommandKind.GoToBookmark, id, Bookmark: n);
        }

        return new(CommandKind.Plain, id);
    }

    /// <summary>
    /// The user keybindings after giving <paramref name="command"/> the shortcut <paramref name="chord"/> (null clears it).
    /// <paramref name="current"/> is its shortcut now; it is removed so the command keeps exactly one.
    /// </summary>
    public static List<KeybindingEntry> Rebind(IEnumerable<KeybindingEntry> user, CommandDefinition command, KeyChord? current, KeyChord? chord)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(command);
        var list = user.ToList();
        if (current is { } old)
        {
            list.Add(new KeybindingEntry(Text(old), "-" + command.Id));
        }

        if (chord is { } added)
        {
            list.Add(new KeybindingEntry(Text(added), command.Id, command.When));
        }

        return list;
    }

    private static string Text(KeyChord chord) => chord.ToString().ToLowerInvariant();
}

/// <summary>The commands last run from the command palette, most recent first; the palette lists them on top. Not persisted.</summary>
public sealed class RecentCommands
{
    public const int Capacity = 10;

    private readonly List<string> _ids = [];

    public IReadOnlyList<string> Ids => _ids;

    public void Note(string id)
    {
        _ids.Remove(id);
        _ids.Insert(0, id);
        if (_ids.Count > Capacity) _ids.RemoveAt(Capacity);
    }

    /// <summary>The commands the palette shows: recently run ones first, the rest in their given order.</summary>
    public List<CommandDefinition> Palette(IEnumerable<CommandDefinition> commands) =>
        commands
            .Where(c => c.ShowInPalette)
            .OrderBy(c => { var i = _ids.IndexOf(c.Id); return i < 0 ? int.MaxValue : i; })
            .ToList();
}
