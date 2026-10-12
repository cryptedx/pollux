namespace Pollux.Core.Navigation;

/// <summary>
/// Which commands a virtual location (a zip, a server's share list, a shell folder) refuses: everything that would
/// change it or its items, and the features that need a real folder. Copying items out is allowed where the location
/// can copy out; Home and the Recycle Bin have their own rules and are never refused here.
/// </summary>
public static class LocationCommands
{
    public const string ReadOnly = "Read-only location";
    public const string Unavailable = "Not available in this location";
    public const string WholeVolume = "Drives and shares are not copied or moved whole; create a shortcut instead";

    /// <summary>The toast that explains why <paramref name="commandId"/> does not run in a <paramref name="kind"/> location; null when it runs.</summary>
    public static string? Refusal(string commandId, LocationKind kind)
    {
        ArgumentNullException.ThrowIfNull(commandId);
        if (kind is not (LocationKind.Zip or LocationKind.Server or LocationKind.Shell)) return null;
        if (Modifies(commandId)) return ReadOnly;
        if (commandId is "file.copy" or "file.copyToOther" && !Locations.Access(kind).CanCopyOut) return ReadOnly;
        return NeedsFolder(commandId) ? Unavailable : null;
    }

    /// <summary>
    /// Copy, cut and transfer commands refuse a selection with a drive or share root (This PC, a server's share list):
    /// one key press must never queue a whole volume, the same rule drag and drop follows.
    /// </summary>
    public static string? VolumeRefusal(string commandId, IEnumerable<string> selected)
    {
        ArgumentNullException.ThrowIfNull(commandId);
        return commandId is "file.copy" or "file.cut" or "file.copyToOther" or "file.moveToOther"
            && selected.Any(Operations.DropDecision.IsVolumeRoot) ? WholeVolume : null;
    }

    private static bool Modifies(string id) => id is "file.rename" or "file.batchRename" or "file.delete" or "file.deletePermanent"
        or "file.cut" or "file.paste" or "file.moveToOther" or "file.newFolder" or "file.newFile" or "file.newFromTemplate" or "file.createShortcut"
        or "file.compress" or "file.extract" or "file.extractHere";

    private static bool NeedsFolder(string id) => id is "search.inFolder" or "view.calculateFolderSizes" or "file.createShortcutInOther"
        or "file.createSymlink" or "file.createJunction" or "integration.vscode" or "integration.copilot" or "pane.syncToOther"
        || Copilot.SelectionActions.IsSelectionAction(id);
}
