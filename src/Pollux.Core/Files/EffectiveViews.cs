using Pollux.Core.Navigation;
using Pollux.Core.Settings;

namespace Pollux.Core.Files;

/// <summary>
/// Which view a Pane shows a location with, and where a user change of it goes: with files.rememberFolderViews the
/// location's entry in <see cref="FolderViews"/> (the global settings are the defaults), otherwise the Tab's own mode
/// and the global sort, grouping and columns.
/// </summary>
public static class EffectiveViews
{
    public static FolderViewDefaults Defaults(FilesSettings s) => new(s.ViewMode, s.SortBy, s.SortDescending, s.GroupBy);

    /// <summary>True when a view change in <paramref name="location"/> is stored for that location; Home has no view of its own.</summary>
    public static bool Remembers(FilesSettings s, string location) => s.RememberFolderViews && location != WinPath.Home;

    /// <summary>The view <paramref name="location"/> is shown with; <paramref name="tabMode"/> counts only without remembered views.</summary>
    public static ResolvedFolderView For(FolderViews views, string location, ViewMode? tabMode, FilesSettings s) =>
        Remembers(s, location)
            ? views.Resolve(location, Defaults(s))
            : new ResolvedFolderView(ViewModes.Effective(tabMode, s.ViewMode), s.SortBy, s.SortDescending, s.GroupBy, null, null);

    /// <summary>Same view, wherever it came from.</summary>
    public static bool Same(ResolvedFolderView? a, ResolvedFolderView? b) => a is not null && b is not null && a with { Source = null } == b with { Source = null };

    /// <summary>
    /// Sorting by <paramref name="column"/>: the sorted column flips its direction; another starts descending for dates and
    /// sizes (newest, largest first) and ascending otherwise.
    /// </summary>
    public static (SortColumn SortBy, bool Descending) NextSort(SortColumn current, bool descending, SortColumn column) =>
        current == column ? (column, !descending) : (column, column is SortColumn.Modified or SortColumn.Size or SortColumn.DateTaken);

    /// <summary>Locations whose subfolders can inherit their view: folders, paths in zips and servers (their shares).</summary>
    public static bool CanApplyToSubfolders(LocationKind kind) => kind is LocationKind.Folder or LocationKind.Zip or LocationKind.Server;
}
