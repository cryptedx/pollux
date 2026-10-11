using System.Globalization;
using Pollux.Core.Copilot;
using Pollux.Core.Files;

namespace Pollux.Core.Recycle;

/// <summary>
/// Which commands run while the active Tab shows the Recycle Bin. Its entries can only be looked at, restored or deleted
/// for good: Delete means "delete permanently" there, and commands that would open, copy, move, rename or hand a
/// recycled file to another tool are ignored. Selecting is allowed, it is how entries to restore or delete are picked.
/// </summary>
public static class RecycleBinCommands
{
    public const string Restore = "recycle.restore";
    public const string DeletePermanent = "recycle.deletePermanent";
    public const string Empty = "recycle.empty";

    /// <summary>The command to run for <paramref name="commandId"/>, or null when it does nothing here.</summary>
    public static string? Route(string commandId, bool inRecycleBin)
    {
        ArgumentNullException.ThrowIfNull(commandId);
        if (!inRecycleBin) return commandId is Restore or DeletePermanent ? null : commandId;
        if (commandId is "file.delete" or "file.deletePermanent") return DeletePermanent;
        return IgnoredInRecycleBin(commandId) ? null : commandId;
    }

    private static bool IgnoredInRecycleBin(string id) =>
        (id.StartsWith("file.", StringComparison.Ordinal) && id is not ("file.preview" or "file.selectAll" or "file.selectNone" or "file.invertSelection" or "file.selectByPattern" or "file.deselectByPattern"))
        || id is "bookmark.addSelection" or "integration.vscode" or "integration.copilot" or "tab.moveToNewWindow"
        || SelectionActions.IsSelectionAction(id);

    /// <summary>The confirmation before deleting the selected entries (by name) permanently.</summary>
    public static string DeleteQuestion(IReadOnlyList<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        var what = names.Count == 1 ? $"\"{names[0]}\"" : string.Create(CultureInfo.InvariantCulture, $"these {names.Count} items");
        return $"Permanently delete {what}? This cannot be undone.";
    }

    /// <summary>The confirmation before emptying the Recycle Bin; <paramref name="count"/> is null when Windows could not tell.</summary>
    public static string EmptyQuestion(long? count, long bytes) => count is { } n
        ? $"Permanently delete {Items(n)} ({Format.Size(bytes)}) in the Recycle Bin? This cannot be undone."
        : "Permanently delete everything in the Recycle Bin? This cannot be undone.";

    /// <summary>"1 item", "3 items".</summary>
    public static string Items(long count) => string.Create(CultureInfo.InvariantCulture, $"{count} {(count == 1 ? "item" : "items")}");
}
