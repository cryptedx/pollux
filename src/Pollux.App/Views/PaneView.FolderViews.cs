using Pollux.Core.Files;
using Pollux.Core.Session;
using Pollux.Core.Settings;

namespace Pollux.App.Views;

/// <summary>
/// The view a Pane shows its location with (mode, sort, grouping, Details columns): remembered per folder in
/// <see cref="FolderViews"/> with files.rememberFolderViews, else the Tab's mode and the global settings. User changes go
/// to the location's entry; every Pane showing an affected location follows through FolderViewsChanged.
/// </summary>
public sealed partial class PaneView
{
    /// <summary>The view of the last presentation.</summary>
    private ResolvedFolderView? _view;

    private bool RemembersView => EffectiveViews.Remembers(Settings.Files, Tab.Location);

    private ResolvedFolderView CurrentView(TabModel tab) =>
        EffectiveViews.For(_host.Services.FolderViews, tab.Location, tab.ViewMode, Settings.Files);

    private void ChangeFolderView(Action<FolderViews> change) => _host.Services.ChangeFolderViews(change);

    /// <summary>The view mode of the active Tab: its folder's, or the Tab's own or files.viewMode without remembered views.</summary>
    internal ViewMode ViewMode => CurrentView(Tab).Mode;

    /// <summary>Shows the active Tab in a view mode, keeping the selection; remembered for the folder, and as the Tab's own mode.</summary>
    internal void SetViewMode(ViewMode mode)
    {
        Tab.ViewMode = mode;
        if (RemembersView)
        {
            ChangeFolderView(v => v.Update(Tab.Location, mode: mode));
            return;
        }

        RememberSelection();
        if (!Tab.IsHome) Present(Tab);
    }

    /// <summary>Sorts by a column; the sorted column flips direction.</summary>
    internal void SetSort(SortColumn column)
    {
        if (RemembersView)
        {
            var view = CurrentView(Tab);
            var (sortBy, descending) = EffectiveViews.NextSort(view.SortBy, view.SortDescending, column);
            ChangeFolderView(v => v.Update(Tab.Location, sortBy: sortBy, sortDescending: descending));
            return;
        }

        var s = Settings.Files;
        (s.SortBy, s.SortDescending) = EffectiveViews.NextSort(s.SortBy, s.SortDescending, column);
        _host.Services.SaveSettings();
    }

    /// <summary>Next grouping (None, Type, Modified date); returns the one now shown.</summary>
    internal GroupBy CycleGrouping()
    {
        if (RemembersView)
        {
            var next = EntryGrouping.Next(CurrentView(Tab).GroupBy);
            ChangeFolderView(v => v.Update(Tab.Location, groupBy: next));
            return next;
        }

        var s = Settings.Files;
        s.GroupBy = EntryGrouping.Next(s.GroupBy);
        _host.Services.SaveSettings();
        return s.GroupBy;
    }

    /// <summary>View: Reset View for This Folder. The folder follows an ancestor applied to subfolders or the settings again.</summary>
    internal void ResetFolderView()
    {
        if (!RemembersView)
        {
            _host.ShowToast(Tab.IsHome ? "Not available on Home" : "Folders don't remember views (files.rememberFolderViews is off)");
            return;
        }

        var location = Tab.Location;
        var removed = false;
        ChangeFolderView(v => removed = v.Reset(location));
        _host.ShowToast(removed ? "View reset for this folder" : "This folder uses the default view");
    }

    /// <summary>View: Apply View to Subfolders. Subfolders drop their own views and use this one (like Explorer's Apply to Folders).</summary>
    internal void ApplyViewToSubfolders()
    {
        if (!RemembersView)
        {
            _host.ShowToast(Tab.IsHome ? "Not available on Home" : "Folders don't remember views (files.rememberFolderViews is off)");
            return;
        }

        if (!EffectiveViews.CanApplyToSubfolders(Tab.Kind))
        {
            _host.ShowToast(Core.Navigation.LocationCommands.Unavailable);
            return;
        }

        var location = Tab.Location;
        var view = CurrentView(Tab);
        ChangeFolderView(v => v.ApplyToSubfolders(location, view));
        _host.ShowToast("Subfolders use this view");
    }

    /// <summary>
    /// The remembered views changed: shows the location again when its view did. A change of the columns alone keeps the
    /// rows (scroll position, selection), as with the column settings.
    /// </summary>
    internal void ApplyFolderView()
    {
        if (Tab.IsHome || _view is null) return;
        var view = CurrentView(Tab);
        if (EffectiveViews.Same(view, _view)) return;
        if (EffectiveViews.Same(view with { Columns = null }, _view with { Columns = null }))
        {
            _view = view;
            ApplyColumnSettings();
            return;
        }

        RememberSelection();
        Present(Tab);
    }
}
