using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Pollux.App.Services;
using Pollux.App.ViewModels;
using Pollux.Core.Files;
using Pollux.Core.Session;
using Pollux.Core.Settings;

namespace Pollux.App.Views;

/// <summary>
/// The Details columns of a Pane: the layout header and rows share (from files.columns, columnOrder, columnWidths),
/// the header's sort titles and column menu, and the shell property values. Property values are read for realized
/// rows only; sorting by a property column reads the whole folder in the background and sorts again when done. Both
/// stop when the Pane lists something else.
/// </summary>
public sealed partial class PaneView
{
    private readonly DetailsColumns _columns = new();
    private CancellationTokenSource _propertiesCts = new();
    /// <summary>The folder and property a running sort load reads, so a new presentation does not start it again.</summary>
    private (string Location, ShellProperty Property)? _sortLoad;
    /// <summary>The settings last applied, without the column layout (see ApplySettings).</summary>
    private string? _appliedSettings;

    private void InitializeColumns()
    {
        DetailsColumnsPanel.SetSource(DetailsList, _columns);
        Header.Columns = _columns;
        Header.SortClicked += column =>
        {
            _host.OnPaneActivated(this);
            if (column.Sort is { } sort) SetSort(sort);
        };
        Header.LayoutCommitted += SaveColumns;
        _columns.Changed += (_, _) => RequestRealizedProperties();
    }

    /// <summary>Takes the column layout the folder remembers, else the one from the settings; true when it changed.</summary>
    private bool ApplyColumnSettings()
    {
        var s = Settings.Files;
        var layout = ColumnLayout.Unpack(_view?.Columns) ?? ColumnLayout.Parse(s.Columns, s.ColumnOrder, s.ColumnWidths);
        if (layout.SameAs(_columns.Layout)) return false;
        _columns.Set(layout);
        return true;
    }

    /// <summary>
    /// Stores a layout the header or the column menu made: for the folder when folders remember their views, else in
    /// the settings. Every Pane follows through FolderViewsChanged or SettingsChanged.
    /// </summary>
    private void SaveColumns(ColumnLayout layout)
    {
        _columns.Set(layout);
        if (RemembersView)
        {
            var packed = layout.Pack();
            ChangeFolderView(v => v.Update(Tab.Location, columns: packed));
            return;
        }

        var cs = layout.ToSettings();
        var s = Settings.Files;
        s.Columns = cs.Columns;
        s.ColumnOrder = cs.Order;
        s.ColumnWidths = cs.Widths;
        _host.Services.SaveSettings();
    }

    /// <summary>Cell titles with the sort arrow; search results are in found order, so they show none.</summary>
    private void UpdateSortHeader()
    {
        var view = _view ?? CurrentView(Tab);
        var searching = _searchHits is not null;
        var arrow = view.SortDescending ? " ↓" : " ↑";
        Header.SetTitles(c =>
        {
            var title = c == KnownColumns.Name && searching ? "Path"
                : c == KnownColumns.Modified && Tab.IsRecycleBin && !searching ? "Deleted"
                : c.Title;
            return !searching && c.Sort == view.SortBy ? title + arrow : title;
        });
    }

    /// <summary>Right-click on the column header: show or hide any column, or go back to the default columns.</summary>
    private void Header_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        var layout = _columns.Layout;
        var menu = new MenuFlyout();
        foreach (var column in KnownColumns.All)
        {
            if (column == KnownColumns.Name) continue;
            if (column == KnownColumns.Dimensions) menu.Items.Add(new MenuFlyoutSeparator());
            var item = new ToggleMenuFlyoutItem { Text = MenuTitle(column), IsChecked = layout.IsVisible(column.Id) };
            item.Click += (_, _) => SaveColumns(_columns.Layout.Toggle(column.Id));
            menu.Items.Add(item);
        }

        menu.Items.Add(new MenuFlyoutSeparator());
        var reset = new MenuFlyoutItem { Text = "Reset columns" };
        reset.Click += (_, _) => SaveColumns(ColumnLayout.Default);
        menu.Items.Add(reset);
        menu.ShowAt(Header, e.GetPosition(Header));
        e.Handled = true;
    }

    /// <summary>The header shortens Attributes and Extension; the menu spells them out.</summary>
    private static string MenuTitle(ListColumn column) =>
        column == KnownColumns.Attributes ? "Attributes" : column == KnownColumns.Extension ? "Extension" : column.Title;

    /// <summary>Drops pending property reads (a new folder, a new search, a closed Pane).</summary>
    private void CancelPropertyReads()
    {
        _propertiesCts.Cancel();
        _propertiesCts = new CancellationTokenSource();
        _sortLoad = null;
    }

    private PropertyReadOptions PropertyOptions() =>
        new(Drives.For(_searchRoot ?? Tab.Location)?.Type == DriveType.Network, Settings.Files.DateFormat);

    /// <summary>A realized Details row: reads the values of the shown property columns unless they are known.</summary>
    private void RequestProperties(FileItem item)
    {
        var properties = _columns.Layout.Properties;
        if (properties.Count == 0 || !Tab.Access.IsFileSystem) return; // the bin, Home, zips and shell folders have no files to read
        PropertyColumns.Request(item.Entry, properties, PropertyOptions(), DispatcherQueue, _ => item.RefreshProperties(), _propertiesCts.Token);
    }

    /// <summary>A property column was shown: the rows already on screen read it too.</summary>
    private void RequestRealizedProperties()
    {
        if (_shownMode != ViewMode.Details || _columns.Layout.Properties.Count == 0 || DetailsList.ItemsPanelRoot is not { } panel) return;
        foreach (var child in panel.Children)
        {
            if (child is ListViewItem { Content: FileItem item }) RequestProperties(item);
        }
    }

    /// <summary>
    /// Sorting by a property column: reads the values of every file of the folder that lacks them, then lists the
    /// folder again (until then, files without a value sort last).
    /// </summary>
    private void LoadSortProperties(TabModel tab)
    {
        if (_view is null || KnownColumns.For(_view.SortBy).Property is not { } property || !tab.Access.IsFileSystem || _searchHits is not null) return;
        if (_sortLoad is { } running && running.Location == tab.Location && running.Property == property) return;
        var options = PropertyOptions();
        var missing = _entries.Where(e => PropertyColumns.Readable(e, options) && !PropertyColumns.Values.TryGet(e, property, out _)).ToList();
        if (missing.Count == 0) return;
        _sortLoad = (tab.Location, property);
        _ = LoadSortPropertiesAsync(tab, missing, property, options, _propertiesCts.Token);
    }

    private async Task LoadSortPropertiesAsync(TabModel tab, List<FileEntry> entries, ShellProperty property, PropertyReadOptions options, CancellationToken ct)
    {
        try
        {
            await PropertyColumns.LoadAllAsync(entries, [property], options, ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidCastException or IOException)
        {
            // Logged by the loader. _sortLoad stays set, so this folder is not read again for the sort.
            return;
        }

        if (ct.IsCancellationRequested || tab != Tab) return;
        _sortLoad = null;
        RememberSelection();
        Present(tab);
    }
}
