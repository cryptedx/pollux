using System.Collections.ObjectModel;
using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Pollux.App.Services;
using Pollux.App.ViewModels;
using Pollux.Core.Compare;
using Pollux.Core.Files;
using Pollux.Core.Git;
using Pollux.Core.Navigation;
using Pollux.Core.Session;
using Pollux.Core.Settings;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.System;

namespace Pollux.App.Views;

/// <summary>One Pane: its Tabs, path bar, Filter and file list (or Home).</summary>
public sealed partial class PaneView : UserControl
{
    private readonly PaneTabs _tabs;
    private static (PaneView Source, int Tab)? _draggedTab;
    private IPaneHost _host = null!;
    private CancellationTokenSource? _loadCts;
    private List<FileEntry> _entries = [];
    private CancellationTokenSource? _sizesCts;
    private Dictionary<string, long> _folderSizes = new(StringComparer.OrdinalIgnoreCase);
    private string? _sizesLocation;
    private bool _sizesWanted;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _sizesTimer;
    private List<FileItem> _visible = [];
    private FileSystemWatcher? _watcher;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _reloadTimer;
    private readonly TypeAheadMatcher _typeAhead = new();
    private bool _isActive;
    private string? _error;
    private FolderGit? _git;
    /// <summary>The git gate's last verdict and the location it was made for.</summary>
    private (string Location, GitGateResult Result)? _gitVerdict;
    private ViewMode _shownMode = ViewMode.Details;
    private FolderComparison? _comparison;
    private CompareSide _compareSide;

    public PaneView()
    {
        _tabs = new PaneTabs(NewTabLocation);
        InitializeComponent();
        // Mouse back/forward buttons, like Explorer. handledEventsToo: the ListView swallows presses on items.
        AddHandler(PointerPressedEvent, new PointerEventHandler(OnXButtonPressed), handledEventsToo: true);
    }

    private void OnXButtonPressed(object sender, PointerRoutedEventArgs e)
    {
        var props = e.GetCurrentPoint(this).Properties;
        if (props.IsXButton1Pressed) Back();
        else if (props.IsXButton2Pressed) Forward();
        else
        {
            // Primary presses only (touch and pen report as left): right-click has List_RightTapped, and a queued
            // FocusList would select the first row before a background context menu opens.
            if (props.IsLeftButtonPressed) ActivateOnPress(e.OriginalSource as DependencyObject);
            return;
        }
        _host.OnPaneActivated(this);
        e.Handled = true;
    }

    /// <summary>
    /// Any press inside an inactive pane activates it — including empty list space, the header or the home view,
    /// which never take focus themselves. Presses on rows, buttons or text boxes focus their own target, so only
    /// the "dead" areas move keyboard focus to the list. The tab strip is left to its Tapped handler: rebuilding it
    /// on press would remove the tab being dragged.
    /// </summary>
    private void ActivateOnPress(DependencyObject? source)
    {
        if (_isActive) return;
        var focusList = true;
        for (var d = source; d is not null && d != this; d = VisualTreeHelper.GetParent(d))
        {
            if (d == TabStrip) return;
            if (d is ListViewItem or TextBox or Microsoft.UI.Xaml.Controls.Primitives.ButtonBase or Microsoft.UI.Xaml.Controls.Primitives.ScrollBar) focusList = false;
        }
        _host.OnPaneActivated(this);
        if (focusList) FocusListSoon();
    }

    internal int Index { get; set; }
    internal TabModel Tab => _tabs.Active;
    internal IReadOnlyList<TabModel> Tabs => _tabs.Tabs;
    internal int ActiveTabIndex => _tabs.ActiveIndex;

    /// <summary>This Pane's Tabs; change them through EditTabs so the Pane shows the result.</summary>
    internal PaneTabs PaneTabs => _tabs;

    /// <summary>The folder shown, or null on Home and in the Recycle Bin.</summary>
    internal string? Folder => Tab.Folder;

    internal IReadOnlyList<FileItem> VisibleItems => _visible;
    internal int TotalCount => _entries.Count;

    internal IReadOnlyList<FileItem> SelectedItems => List.SelectedItems.OfType<FileItem>().ToList();

    internal IReadOnlyList<string> SelectedPaths => SelectedItems.Select(i => i.Path).ToList();

    internal bool IsFilterFocused => ReferenceEquals(FocusManager.GetFocusedElement(XamlRoot), FilterBox);
    internal bool IsListFocused => FocusManager.GetFocusedElement(XamlRoot) is ListViewItem or ListView;

    private PolluxSettings Settings => _host.Services.Settings;

    internal void Initialize(IPaneHost host, PaneSession session)
    {
        _host = host;
        _tabs.Load(session);
        RenderTabs();
        _ = ShowCurrentAsync();
    }

    /// <summary>Replaces all Tabs (used when switching Workspaces).</summary>
    internal void LoadSession(PaneSession session) => EditTabs(t => t.Load(session));

    /// <summary>Stops background work when the window closes for good.</summary>
    internal void Shutdown()
    {
        _loadCts?.Cancel();
        _searchCts?.Cancel();
        _sizesCts?.Cancel();
        _sizesTimer?.Stop();
        _reloadTimer?.Stop();
        StopWatching();
    }

    internal PaneSession ToSession() => _tabs.ToSession();

    internal bool IsActive
    {
        get => _isActive;
        set
        {
            _isActive = value;
            Card.BorderBrush = Res(value ? "PolluxAccentBrush" : "PolluxLineBrush");
            Card.BorderThickness = new Thickness(1);
            RenderTabs();
        }
    }

    internal void FocusList()
    {
        // Focusing an element of a hidden window activates it and takes the activation from Pollux's visible windows
        // (e.g. the Clipboard Stack opened on a cold start).
        if (_host.IsHidden) return;
        if (Tab.IsHome)
        {
            HomeHost.Focus(FocusState.Programmatic);
            return;
        }

        if (List.SelectedIndex < 0 && _visible.Count > 0) List.SelectedIndex = 0;
        var container = List.SelectedIndex >= 0 ? List.ContainerFromIndex(List.SelectedIndex) as ListViewItem : null;
        // Programmatic, not Keyboard: the focus frame then only shows when the last input came from the keyboard,
        // so clicking into a Pane does not flash it around the selected row.
        if (container is not null) container.Focus(FocusState.Programmatic);
        else List.Focus(FocusState.Programmatic);
    }

    // ---------------- tabs ----------------

    /// <summary>
    /// Changes the Tabs of one or more Panes and shows the outcome: the shown Tabs remember their selection first,
    /// then every Pane redraws its tab strip and lists its active Tab again if that changed (or <paramref name="reload"/>).
    /// </summary>
    internal static void EditTabs(IReadOnlyList<PaneView> panes, Action change, bool reload = false)
    {
        var shown = new TabModel[panes.Count];
        for (var i = 0; i < panes.Count; i++)
        {
            panes[i].RememberSelection();
            shown[i] = panes[i].Tab;
        }

        change();
        for (var i = 0; i < panes.Count; i++)
        {
            panes[i].RenderTabs();
            if (reload || panes[i].Tab != shown[i]) _ = panes[i].ShowCurrentAsync();
        }
    }

    internal void EditTabs(Action<PaneTabs> change, bool reload = false) => EditTabs([this], () => change(_tabs), reload);

    /// <summary>Removes a Tab and returns its location (for moving it elsewhere).</summary>
    internal string DetachTab(int index)
    {
        var location = "";
        EditTabs(t => location = t.Detach(index), reload: true);
        return location;
    }

    internal void NewTab(string? location = null, bool activate = true) => EditTabs(t => t.New(location, activate));

    internal void CloseTab(int index) => EditTabs(t => t.Close(index), reload: index >= 0 && index < _tabs.Tabs.Count);

    internal void ReopenClosedTab() => EditTabs(t => t.ReopenClosed());

    internal void NextTab(int delta) => EditTabs(t => t.Next(delta));

    internal void SwitchTo(int index) => EditTabs(t => t.SwitchTo(index));

    private string NewTabLocation()
    {
        var l = _host?.Services.Settings.Tabs.NewTabLocation ?? "home";
        return l.Equals("home", StringComparison.OrdinalIgnoreCase) || !Directory.Exists(l) ? WinPath.Home : l;
    }

    private void RenderTabs()
    {
        TabStrip.Children.Clear();
        for (var i = 0; i < Tabs.Count; i++)
        {
            var index = i;
            var on = i == ActiveTabIndex;
            var title = new TextBlock { Text = Tabs[i].Title, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 150 };
            var icon = Tabs[i].IsHome || Tabs[i].IsRecycleBin
                ? (FrameworkElement)new TextBlock { Text = Tabs[i].IsHome ? "⌂" : "♻", FontSize = 12, VerticalAlignment = VerticalAlignment.Center }
                : new Border { Width = 14, Height = 10, CornerRadius = new CornerRadius(2, 3, 2, 2), Background = Res("PolluxFolderBrush"), VerticalAlignment = VerticalAlignment.Center };
            var close = new Button
            {
                Content = "✕",
                FontSize = 9,
                Padding = new Thickness(4, 1, 4, 1),
                Style = Ui.FlatButton,
                Foreground = Res("PolluxMutedBrush"),
                Visibility = Settings.Tabs.ShowTabCloseButtons ? Visibility.Visible : Visibility.Collapsed,
                IsTabStop = false,
            };
            close.Click += (_, _) => CloseTab(index);
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
            panel.Children.Add(icon);
            panel.Children.Add(title);
            panel.Children.Add(close);
            var tab = new Border
            {
                Child = panel,
                Padding = new Thickness(10, 4, 4, 5),
                CornerRadius = new CornerRadius(6, 6, 0, 0),
                Background = on ? Res("PolluxSurfaceBrush") : null,
                BorderBrush = on && _isActive ? Res("PolluxAccentBrush") : null,
                // Always reserve the 2px indicator so toggling the active pane doesn't shift the layout.
                BorderThickness = new Thickness(0, 0, 0, 2),
                Opacity = on ? 1 : 0.7,
            };
            ToolTipService.SetToolTip(tab, Tabs[i].Folder ?? Tabs[i].Title);
            tab.CanDrag = true;
            tab.AllowDrop = true;
            tab.DragStarting += (_, e) =>
            {
                _draggedTab = (this, index);
                e.Data.SetText(Tabs[index].Location);
                e.Data.RequestedOperation = DataPackageOperation.Move;
            };
            tab.DragOver += (_, e) =>
            {
                if (_draggedTab is null) return;
                e.AcceptedOperation = DataPackageOperation.Move;
                e.DragUIOverride.IsCaptionVisible = false;
                e.DragUIOverride.IsGlyphVisible = false;
            };
            tab.Drop += (_, e) =>
            {
                e.Handled = true; // the tab bar behind must not also handle it
                AcceptDraggedTab(index);
            };
            tab.DropCompleted += (_, _) => _draggedTab = null;
            // Activate on Tapped (not PointerPressed): rebuilding the strip on press would remove the tab being dragged.
            tab.Tapped += (_, _) =>
            {
                _host.OnPaneActivated(this);
                SwitchTo(index);
            };
            tab.PointerReleased += (_, e) =>
            {
                if (e.GetCurrentPoint(tab).Properties.PointerUpdateKind == Microsoft.UI.Input.PointerUpdateKind.MiddleButtonReleased) CloseTab(index);
            };
            TabStrip.Children.Add(tab);
        }
    }

    /// <summary>Dropping a tab on the free part of the tab bar moves it to the end of this Pane.</summary>
    private void TabBar_DragOver(object sender, DragEventArgs e)
    {
        if (_draggedTab is null) return;
        e.AcceptedOperation = DataPackageOperation.Move;
        e.DragUIOverride.IsCaptionVisible = false;
        e.DragUIOverride.IsGlyphVisible = false;
    }

    private void TabBar_Drop(object sender, DragEventArgs e) => AcceptDraggedTab(-1);

    /// <summary>
    /// Takes the tab being dragged: from this window (reorder / other pane) or from another Pollux window,
    /// which hands the tab over. <paramref name="targetIndex"/> -1 appends.
    /// </summary>
    private void AcceptDraggedTab(int targetIndex)
    {
        if (_draggedTab is not { } from) return;
        _draggedTab = null;
        if (from.Source._host == _host)
        {
            var end = from.Source == this ? Tabs.Count - 1 : Tabs.Count;
            _host.MoveTab(from.Source.Index, from.Tab, Index, targetIndex < 0 ? end : targetIndex);
            return;
        }

        if (from.Tab < 0 || from.Tab >= from.Source.Tabs.Count) return;
        EditTabs([from.Source, this], () => Core.Session.PaneTabs.Move(from.Source._tabs, from.Tab, _tabs, targetIndex < 0 ? Tabs.Count : targetIndex), reload: true);
        _host.OnPaneActivated(this);
        _host.Activate();
    }

    private void NewTab_Click(object sender, RoutedEventArgs e)
    {
        _host.OnPaneActivated(this);
        NewTab();
    }

    // ---------------- navigation ----------------

    internal void Navigate(string location, string? focusName = null)
    {
        if (location != WinPath.Home)
        {
            location = WinPath.Normalize(location);
            if (File.Exists(location))
            {
                Shell.Open(location);
                return;
            }
        }

        RememberSelection();
        Tab.History.Navigate(location);
        Tab.Filter = null;
        Tab.SelectedNames = [];
        Tab.FocusName = focusName;
        RenderTabs();
        _ = ShowCurrentAsync();
    }

    internal void Back() => Step(Tab.History.Back(), childToFocus: null);
    internal void Forward() => Step(Tab.History.Forward(), childToFocus: null);

    internal void Up()
    {
        if (Tab.IsHome) return;
        var parent = WinPath.Parent(Tab.Location);
        if (parent is null) Navigate(WinPath.Home);
        else Navigate(parent, WinPath.Name(Tab.Location));
    }

    private void Step(string? location, string? childToFocus)
    {
        if (location is null) return;
        Tab.Filter = null;
        Tab.FocusName = childToFocus;
        RenderTabs();
        _ = ShowCurrentAsync();
    }

    internal void Refresh()
    {
        if (_searchHits is not null) RefreshSearch();
        else _ = ShowCurrentAsync(keepSelection: true);
    }

    private void RememberSelection()
    {
        // Search results are not entries of the Tab's folder.
        if (Tab.IsHome || _searchHits is not null) return;
        Tab.SelectedNames = SelectedItems.Select(i => i.Entry.Name).ToList();
    }

    private Task _loading = Task.CompletedTask;

    /// <summary>Completes when the folder of the current Tab is listed (or its load was superseded or failed).</summary>
    internal Task ListingLoaded => _loading;

    /// <summary>Selects exactly the visible entries with these names (Copilot's Pollux tools); returns how many were selected.</summary>
    internal int SelectNames(IReadOnlyList<string> names)
    {
        if (Tab.IsHome || _searchHits is not null) return 0;
        Tab.SelectedNames = [.. names];
        Tab.FocusName = null;
        Present(Tab);
        return SelectedItems.Count;
    }

    private Task ShowCurrentAsync(bool keepSelection = false) => _loading = ShowCurrentCoreAsync(keepSelection);

    private async Task ShowCurrentCoreAsync(bool keepSelection)
    {
        _loadCts?.Cancel();
        _sizesCts?.Cancel();
        var cts = _loadCts = new CancellationTokenSource();
        var tab = Tab;
        if (tab.Location != _sizesLocation)
        {
            _folderSizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            _sizesLocation = tab.Location;
            _sizesWanted = false;
        }

        if (keepSelection) RememberSelection();
        EndSearch();
        RenderCrumbs();
        ApplyFilterBar();
        _error = null;
        _host.OnPaneStateChanged(this);

        if (tab.IsHome)
        {
            StopWatching();
            ListHost.Visibility = Visibility.Collapsed;
            HomeHost.Visibility = Visibility.Visible;
            LoadingBar.Visibility = Visibility.Collapsed;
            InfoText.Text = "";
            TrustButton.Visibility = Visibility.Collapsed;
            RenderHome();
            ListingChanged?.Invoke();
            return;
        }

        if (tab.IsRecycleBin)
        {
            await ShowRecycleBinAsync(tab, cts.Token);
            return;
        }

        HomeHost.Visibility = Visibility.Collapsed;
        ListHost.Visibility = Visibility.Visible;
        _host.Services.VisitLocation(tab.Location);

        var started = DateTime.UtcNow;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var slowTimer = DispatcherQueue.CreateTimer();
        slowTimer.Interval = TimeSpan.FromMilliseconds(150);
        slowTimer.Tick += (_, _) =>
        {
            if (cts.IsCancellationRequested) { slowTimer.Stop(); return; }
            LoadingBar.Visibility = Visibility.Visible;
            if ((DateTime.UtcNow - started).TotalSeconds >= Settings.Files.SlowFolderSeconds)
            {
                EmptyText.Text = $"Still loading {tab.Location}…\nSlow or unreachable location. Esc or navigate away to cancel.";
                EmptyText.Visibility = Visibility.Visible;
            }
        };
        slowTimer.Start();

        var all = new List<FileEntry>();
        var first = true;
        string? fileFolder = null;
        try
        {
            await foreach (var batch in DirectoryReader.ReadAsync(tab.Location, 300, cts.Token))
            {
                all.AddRange(batch);
                if (first)
                {
                    first = false;
                    _entries = all;
                    Present(tab);
                    StartupLog.Mark("firstEntries");
                    CrashLog.Trace($"folder {tab.Location}: first {all.Count} rows after {stopwatch.ElapsedMilliseconds} ms");
                }
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            // A Tab restored from a Session (or written by a build that browses .zip files) can point at a file:
            // show its folder with the file selected instead of the OS error ("The parameter is incorrect").
            if (ex is IOException && File.Exists(tab.Location)) fileFolder = WinPath.Parent(tab.Location);
            if (fileFolder is null) _error = ex is DirectoryNotFoundException ? "Folder not found." : ex.Message;
        }
        finally
        {
            slowTimer.Stop();
        }

        if (cts.IsCancellationRequested) return;
        if (fileFolder is not null)
        {
            // SelectedNames, not FocusName: FocusName is used up by the first batch and lost on the final Present.
            tab.SelectedNames = [WinPath.Name(tab.Location)];
            tab.History.ReplaceCurrent(fileFolder);
            RenderTabs();
            await ShowCurrentCoreAsync(keepSelection: false);
            return;
        }

        LoadingBar.Visibility = Visibility.Collapsed;
        _entries = all;
        Present(tab);
        CrashLog.Trace($"folder {tab.Location}: all {all.Count} rows after {stopwatch.ElapsedMilliseconds} ms");
        Watch(tab.Location);
        _ = LoadGitStatusAsync(tab, cts.Token);
        if (_sizesWanted || (Settings.Files.AutoFolderSizes && IsLocalDrive(tab.Location))) StartFolderSizes(tab, recomputeAll: false);
        ListingChanged?.Invoke();
    }

    /// <summary>Raised when the folder was read from disk again (or the Pane went Home); not when only the presentation changed.</summary>
    internal event Action? ListingChanged;

    /// <summary>Marks every row by how it compares to the other Pane's folder (<paramref name="side"/> is this Pane's side); null clears the marks.</summary>
    internal void SetCompare(FolderComparison? comparison, CompareSide side)
    {
        if (_comparison is null && comparison is null) return;
        _comparison = comparison;
        _compareSide = side;
        if (Tab.IsHome) return;
        // The rows stay as they are: only their marks change, so scroll position, caret and selection are not touched.
        var compared = ComparedNow(Tab);
        foreach (var item in _visible) item.SetCompare(compared?.MarkFor(_compareSide, item.Entry.Name) ?? CompareMark.None, _compareSide);
    }

    private FolderComparison? ComparedNow(TabModel tab) =>
        _comparison is { } c && string.Equals(tab.Location, c.FolderOf(_compareSide), StringComparison.OrdinalIgnoreCase) ? c : null;

    /// <summary>Lists the Recycle Bin through the shell: it spans all drives, so there is no folder to read, watch or git-check.</summary>
    private async Task ShowRecycleBinAsync(TabModel tab, CancellationToken ct)
    {
        StopWatching();
        HomeHost.Visibility = Visibility.Collapsed;
        ListHost.Visibility = Visibility.Visible;
        LoadingBar.Visibility = Visibility.Visible;
        List<FileEntry> entries;
        try
        {
            entries = [.. (await Interop.RecycleBin.ListAsync()).Select(i => i.ToEntry())];
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidCastException or IOException or UnauthorizedAccessException)
        {
            entries = [];
            _error = ex.Message;
        }

        if (ct.IsCancellationRequested) return;
        LoadingBar.Visibility = Visibility.Collapsed;
        _entries = entries;
        Present(tab);
    }

    /// <summary>Shows the rows <see cref="FolderProjection"/> picks for the Tab and restores its selection.</summary>
    private void Present(TabModel tab)
    {
        if (_searchHits is not null)
        {
            PresentSearch();
            return;
        }

        var s = Settings.Files;
        var listing = FolderProjection.Project(_entries, tab.Location, Settings, tab.Filter, _git, tab.SelectedNames, tab.FocusName, folderSizes: _folderSizes);
        tab.FocusName = null;
        var columns = ListColumns.Parse(s.Columns);
        var compared = ComparedNow(tab);
        var items = listing.Rows.Select(r => new FileItem(r.Entry, s.DateFormat, s.ShowExtensions, columns, folderSizeKnown: _folderSizes.ContainsKey(r.Entry.FullPath))
        {
            Highlights = r.Highlights,
            Git = r.Git,
            CompareState = compared?.MarkFor(_compareSide, r.Entry.Name) ?? CompareMark.None,
            CompareFrom = _compareSide,
        }).ToList();

        ShowViewMode(ViewModes.Effective(tab.ViewMode, s.ViewMode));
        _visible = items;
        if (listing.Groups is { Count: > 0 } groups && _shownMode == ViewMode.Details)
        {
            // Rows are already in group order, so indices into _visible stay valid.
            var source = new CollectionViewSource
            {
                IsSourceGrouped = true,
                Source = groups.Select(g => new FileGroup(g.Title, items.GetRange(g.Start, g.Count))).ToList(),
            };
            List.ItemsSource = source.View;
        }
        else
        {
            List.ItemsSource = items;
        }

        UpdateSortHeader();

        EmptyText.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = _error ?? (string.IsNullOrEmpty(tab.Filter) ? (tab.IsRecycleBin ? "Recycle Bin is empty" : "empty folder") : "no matches");
        FilterCount.Text = $"{items.Count}/{_entries.Count}";
        PresentGitInfo(tab);

        foreach (var i in listing.Selected) List.SelectedItems.Add(items[i]);
        if (listing.Selected.Count > 0) List.ScrollIntoView(items[listing.Selected[0]]);

        _host.OnPaneStateChanged(this);
        if (_isActive && FocusManager.GetFocusedElement(XamlRoot) is not TextBox) FocusListSoon();
    }

    private void FocusListSoon() => DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, FocusList);

    /// <summary>Computes the sizes of the folders in the listing in the background (View: Calculate Folder Sizes).</summary>
    internal void CalculateFolderSizes()
    {
        if (!Tab.IsHome) StartFolderSizes(Tab, recomputeAll: true);
    }

    private static bool IsLocalDrive(string path)
    {
        try
        {
            var root = Path.GetPathRoot(path);
            if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal)) return false;
            return new DriveInfo(root).DriveType is DriveType.Fixed or DriveType.Removable;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void StartFolderSizes(TabModel tab, bool recomputeAll)
    {
        _sizesCts?.Cancel();
        var cts = _sizesCts = new CancellationTokenSource();
        _sizesWanted = true;
        var folders = _entries
            .Where(e => e.IsDirectory && !e.Attributes.HasFlag(System.IO.FileAttributes.ReparsePoint) && (recomputeAll || !_folderSizes.ContainsKey(e.FullPath)))
            .Select(e => e.FullPath)
            .ToList();
        if (folders.Count == 0) return;

        _ = Task.Run(() => FolderSizer.MeasureAllAsync(folders, (path, size) =>
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (cts.IsCancellationRequested || tab != Tab) return;
                _folderSizes[path] = size;
                ScheduleSizesPresent(tab);
            });
        }, cts.Token)).ContinueWith(_ => DispatcherQueue.TryEnqueue(() =>
        {
            if (cts.IsCancellationRequested || tab != Tab) return;
            // All measured: the one time a list sorted by size is put in order again.
            _sizesTimer?.Stop();
            ShowFolderSizes(final: true);
        }), TaskScheduler.Default);
    }

    /// <summary>
    /// Shows the sizes measured so far in the rows that are there, without building the list again (that would throw
    /// away the scroll position, the caret and the selection anchor while the user works). Only a list sorted by size
    /// has to be put in order again, and only when everything is measured.
    /// </summary>
    private void ShowFolderSizes(bool final)
    {
        if (Tab.IsHome) return;
        foreach (var item in _visible)
        {
            if (item.IsDirectory && _folderSizes.TryGetValue(item.Path, out var size)) item.SetFolderSize(size);
        }

        if (final && Settings.Files.SortBy == SortColumn.Size)
        {
            RememberSelection();
            Present(Tab);
        }
    }

    private void ScheduleSizesPresent(TabModel tab)
    {
        if (_sizesTimer is null)
        {
            _sizesTimer = DispatcherQueue.CreateTimer();
            _sizesTimer.Interval = TimeSpan.FromMilliseconds(300);
            _sizesTimer.IsRepeating = false;
            _sizesTimer.Tick += (_, _) => ShowFolderSizes(final: false);
        }

        if (!_sizesTimer.IsRunning) _sizesTimer.Start();
    }

    /// <summary>
    /// The path bar's git part: the branch, a hint when the config scan refused git status, and "Trust this folder" in
    /// an untrusted repository. The branch is read on every presentation; hint and action come from the last verdict
    /// of the git gate for this location (<see cref="LoadGitStatusAsync"/>).
    /// </summary>
    private void PresentGitInfo(TabModel tab)
    {
        var branch = tab.Folder is { } folder ? GitInfo.BranchFor(folder) : null;
        var verdict = _gitVerdict is { } v && v.Location == tab.Location ? v.Result : null;
        var text = branch is null ? "" : "⎇ " + branch;
        if (verdict?.RefusedConfigKey is { } key)
        {
            text += (text.Length > 0 ? " · " : "") + GitConfigScan.ShortHint;
            ToolTipService.SetToolTip(InfoText, GitConfigScan.Hint(key));
        }
        else
        {
            ToolTipService.SetToolTip(InfoText, null);
        }

        InfoText.Text = text;
        TrustButton.Visibility = verdict is { CanTrust: true } && Settings.Files.GitStatus ? Visibility.Visible : Visibility.Collapsed;
        ToolTipService.SetToolTip(TrustButton, verdict?.RepoRoot is { } root ? $"Add {root} to Trusted Folders so git status runs here" : null);
    }

    private void TrustButton_Click(object sender, RoutedEventArgs e)
    {
        if (_gitVerdict is not { Result.RepoRoot: { } root } v || v.Location != Tab.Location) return;
        // The repository root, not the browsed subfolder; network locations are refused by the gate (S-01).
        if (GitGate.AddTrustedFolder(Settings.Files.TrustedFolders, root) is not { } trusted) return;
        Settings.Files.TrustedFolders = trusted;
        _host.Services.SaveSettings(); // SettingsChanged → ApplySettings asks the gate again, so letters appear
    }

    private async Task LoadGitStatusAsync(TabModel tab, CancellationToken ct)
    {
        // The git gate decides (ADR 0006): git status only runs in Trusted Folders, never for network gitdirs.
        var gate = GitInfo.Gate(Settings);
        var location = tab.Location;
        var verdict = await Task.Run(() => gate.Evaluate(location));
        if (ct.IsCancellationRequested || tab != Tab) return;
        var changedHint = _gitVerdict?.Location != location || _gitVerdict?.Result != verdict;
        _gitVerdict = (location, verdict);
        if (verdict is not { Mode: GitGateMode.RunStatus, RepoRoot: { } root })
        {
            if (_git is not null) { _git = null; RememberSelection(); Present(tab); }
            else if (changedHint) PresentGitInfo(tab);
            return;
        }

        var status = await GitInfo.StatusAsync(verdict, ct);
        if (ct.IsCancellationRequested || status is null || tab != Tab) return;
        _git = new FolderGit(status, root);
        RememberSelection();
        Present(tab);
    }

    // ---------------- view mode ----------------

    /// <summary>The view mode of the active Tab: its own, or the files.viewMode setting.</summary>
    internal ViewMode ViewMode => ViewModes.Effective(Tab.ViewMode, Settings.Files.ViewMode);

    /// <summary>Gives the active Tab its own view mode (kept in the session) and shows it, keeping the selection.</summary>
    internal void SetViewMode(ViewMode mode)
    {
        RememberSelection();
        Tab.ViewMode = mode;
        if (!Tab.IsHome) Present(Tab);
    }

    /// <summary>The ListView of the shown view mode; every list operation goes through it.</summary>
    private ListView List => ListFor(_shownMode);

    private ListView ListFor(ViewMode mode) => mode switch
    {
        ViewMode.Tiles => TilesList,
        ViewMode.Icons => IconsList,
        _ => DetailsList,
    };

    /// <summary>
    /// Shows the ListView of a view mode and empties the one shown before. Each mode has its own ListView with fixed
    /// template, container style and panel, so switching never swaps them on a live ListView.
    /// </summary>
    private void ShowViewMode(ViewMode mode)
    {
        if (mode == _shownMode) return;
        var old = List;
        var hadFocus = FocusManager.GetFocusedElement(XamlRoot) is DependencyObject f && IsWithin(f, old);
        old.ItemsSource = null;
        old.Visibility = Visibility.Collapsed;
        _shownMode = mode;
        List.Visibility = Visibility.Visible;
        Header.Visibility = mode == ViewMode.Details ? Visibility.Visible : Visibility.Collapsed;
        if (hadFocus) FocusListSoon();
    }

    private static bool IsWithin(DependencyObject d, DependencyObject root)
    {
        for (var e = d; e is not null; e = VisualTreeHelper.GetParent(e)) if (e == root) return true;
        return false;
    }

    /// <summary>
    /// Tiles and Large Icons: the shell thumbnail over the folder/badge fallback. Asked for when a container is
    /// realized, so only entries on (or near) the screen load; cached thumbnails show at once.
    /// </summary>
    private void ShowThumbnail(ContentControl container, Grid root, FileItem item)
    {
        if (root.Children[0] is not Grid icon || icon.Children[0] is not UIElement fallback || icon.Children[1] is not Image image) return;
        // A mapped network drive (Z:\) looks local, but reading a file's content over SMB can hang the shared thumbnail thread.
        var onNetworkDrive = Drives.For(item.Path)?.Type == DriveType.Network;
        var key = ThumbnailKey.For(item.Entry, ViewModes.ThumbnailPixels(_shownMode, XamlRoot?.RasterizationScale ?? 1), onNetworkDrive);
        var cached = Thumbnails.Get(key, DispatcherQueue, loaded =>
        {
            if (!ReferenceEquals(container.Content, item)) return; // the container was recycled for another entry
            image.Source = loaded;
            fallback.Visibility = Visibility.Collapsed;
        });
        image.Source = cached;
        fallback.Visibility = cached is null ? Visibility.Visible : Visibility.Collapsed;
    }

    internal void SelectByName(string name)
    {
        Tab.FocusName = name;
        Refresh();
    }

    private void UpdateSortHeader()
    {
        var s = Settings.Files;
        var arrow = s.SortDescending ? " ↓" : " ↑";
        SortName.Content = "Name" + (s.SortBy == SortColumn.Name ? arrow : "");
        SortModified.Content = (Tab.IsRecycleBin ? "Deleted" : "Modified") + (s.SortBy == SortColumn.Modified ? arrow : "");
        SortType.Content = "Type" + (s.SortBy == SortColumn.Type ? arrow : "");
        SortSize.Content = "Size" + (s.SortBy == SortColumn.Size ? arrow : "");
        var columns = ListColumns.Parse(s.Columns);
        SortCreated.Content = "Created" + (s.SortBy == SortColumn.Created ? arrow : "");
        SortAttributes.Content = "Attr" + (s.SortBy == SortColumn.Attributes ? arrow : "");
        SortExtension.Content = "Ext" + (s.SortBy == SortColumn.Extension ? arrow : "");
        SortCreated.Visibility = columns.HasFlag(OptionalColumns.Created) ? Visibility.Visible : Visibility.Collapsed;
        SortAttributes.Visibility = columns.HasFlag(OptionalColumns.Attributes) ? Visibility.Visible : Visibility.Collapsed;
        SortExtension.Visibility = columns.HasFlag(OptionalColumns.Extension) ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Right-click on the column header: toggle the optional columns.</summary>
    private void Header_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        var shown = ListColumns.Parse(Settings.Files.Columns);
        var menu = new MenuFlyout();
        foreach (var c in ListColumns.All)
        {
            var column = c;
            var item = new ToggleMenuFlyoutItem { Text = ListColumns.Title(column), IsChecked = shown.HasFlag(column) };
            item.Click += (_, _) =>
            {
                Settings.Files.Columns = ListColumns.Toggle(Settings.Files.Columns, column);
                _host.Services.SaveSettings();
            };
            menu.Items.Add(item);
        }

        menu.ShowAt(Header, e.GetPosition(Header));
        e.Handled = true;
    }

    private void Sort_Click(object sender, RoutedEventArgs e)
    {
        var column = Enum.Parse<SortColumn>((string)((Button)sender).Tag);
        SetSort(column);
    }

    internal void SetSort(SortColumn column)
    {
        var s = Settings.Files;
        if (s.SortBy == column) s.SortDescending = !s.SortDescending;
        else
        {
            s.SortBy = column;
            s.SortDescending = column is SortColumn.Modified or SortColumn.Size;
        }

        _host.Services.SaveSettings();
    }

    /// <summary>Called when settings change: re-present without reloading from disk.</summary>
    internal void ApplySettings()
    {
        RenderTabs();
        if (!Tab.IsHome)
        {
            RememberSelection();
            Present(Tab);
            // Trusted Folders or the git-status switch may have changed: ask the git gate again.
            if (_loadCts is { IsCancellationRequested: false } cts) _ = LoadGitStatusAsync(Tab, cts.Token);
        }
        else
        {
            RenderHome();
        }
    }

    private void Watch(string folder)
    {
        StopWatching();
        if (WinPath.IsUnc(folder)) return; // network shares: no live watching, use refresh
        try
        {
            _watcher = new FileSystemWatcher(folder)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size | NotifyFilters.LastWrite | NotifyFilters.Attributes,
                EnableRaisingEvents = true,
            };
            FileSystemEventHandler h = (_, _) => DispatcherQueue.TryEnqueue(ScheduleReload);
            _watcher.Changed += h;
            _watcher.Created += h;
            _watcher.Deleted += h;
            _watcher.Renamed += (_, _) => DispatcherQueue.TryEnqueue(ScheduleReload);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            _watcher = null;
        }
    }

    private void ScheduleReload()
    {
        if (_reloadTimer is null)
        {
            _reloadTimer = DispatcherQueue.CreateTimer();
            _reloadTimer.Interval = TimeSpan.FromMilliseconds(250);
            _reloadTimer.IsRepeating = false;
            _reloadTimer.Tick += (_, _) => _ = ShowCurrentAsync(keepSelection: true);
        }

        _reloadTimer.Start();
    }

    private void StopWatching()
    {
        _watcher?.Dispose();
        _watcher = null;
    }

    // ---------------- breadcrumb / path box ----------------

    private void RenderCrumbs()
    {
        Crumbs.Children.Clear();
        PathBox.Visibility = Visibility.Collapsed;
        CrumbHost.Visibility = Visibility.Visible;
        if (Tab.IsHome)
        {
            Crumbs.Children.Add(Crumb("⌂ home", WinPath.Home));
            return;
        }

        if (Tab.IsRecycleBin)
        {
            Crumbs.Children.Add(Crumb("♻ Recycle Bin", WinPath.RecycleBin));
            return;
        }

        var segments = WinPath.Segments(Tab.Location);
        for (var i = 0; i < segments.Count; i++)
        {
            if (i > 0) Crumbs.Children.Add(new TextBlock { Text = "›", Foreground = Res("PolluxMutedBrush"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(1, 0, 1, 0) });
            var label = i == 0 ? segments[0].TrimEnd('\\') : WinPath.Name(segments[i]);
            Crumbs.Children.Add(Crumb(label, segments[i]));
        }

        CrumbScroll.UpdateLayout();
        CrumbScroll.ChangeView(CrumbScroll.ScrollableWidth, null, null, true);
    }

    private Button Crumb(string label, string target)
    {
        var b = new Button
        {
            Content = label,
            FontFamily = Ui.Mono,
            FontSize = 12,
            Padding = new Thickness(5, 2, 5, 2),
            Style = Ui.FlatButton,
            IsTabStop = false,
        };
        b.Click += (_, _) =>
        {
            _host.OnPaneActivated(this);
            if (!string.Equals(target, Tab.Location, StringComparison.OrdinalIgnoreCase)) Navigate(target);
        };
        return b;
    }

    private void CrumbHost_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (e.OriginalSource is FrameworkElement fe && (fe == CrumbHost || fe == Crumbs || fe is ScrollViewer || fe.Parent == CrumbScroll)) EditPath();
    }

    internal void EditPath()
    {
        _host.OnPaneActivated(this);
        CrumbHost.Visibility = Visibility.Collapsed;
        PathBox.Visibility = Visibility.Visible;
        PathBox.Text = Tab.Folder ?? "";
        PathBox.Focus(FocusState.Keyboard);
        PathBox.SelectAll();
    }

    private void PathBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            var text = PathBox.Text.Trim();
            if (text.Length == 0) { RenderCrumbs(); FocusList(); return; }
            if (WinPath.WellKnown(text) is { } known)
            {
                Navigate(known);
                FocusList();
                return;
            }

            var target = WinPath.Resolve(text, Folder ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Environment.GetEnvironmentVariable);
            if (Directory.Exists(target) || File.Exists(target))
            {
                Navigate(target);
                FocusList();
            }
            else
            {
                PathBox.Header = null;
                PathBox.Foreground = Res("PolluxBadBrush");
                ToolTipService.SetToolTip(PathBox, "Path not found: " + target);
            }
        }
        else if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            RenderCrumbs();
            FocusList();
        }
        else
        {
            PathBox.ClearValue(ForegroundProperty);
        }
    }

    private void PathBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (PathBox.Visibility == Visibility.Visible) RenderCrumbs();
    }

    // ---------------- filter ----------------

    internal void ShowFilter()
    {
        if (Tab.IsHome) return;
        if (IsSearchActive) CloseSearch();
        Tab.Filter ??= "";
        ApplyFilterBar();
        FilterBox.Focus(FocusState.Keyboard);
        FilterBox.SelectAll();
    }

    internal void CloseFilter()
    {
        Tab.Filter = null;
        ApplyFilterBar();
        RememberSelection();
        Present(Tab);
        FocusList();
    }

    private void ApplyFilterBar()
    {
        var visible = Tab.Filter is not null && !Tab.IsHome;
        FilterBar.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (visible && FilterBox.Text != Tab.Filter) FilterBox.Text = Tab.Filter;
    }

    private void FilterBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (Tab.Filter is null || Tab.Filter == FilterBox.Text) return;
        Tab.Filter = FilterBox.Text;
        Tab.SelectedNames = [];
        Present(Tab);
    }

    private void FilterBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Down:
                e.Handled = true;
                if (List.SelectedIndex < _visible.Count - 1) List.SelectedIndex++;
                List.ScrollIntoView(List.SelectedItem);
                break;
            case VirtualKey.Up:
                e.Handled = true;
                if (List.SelectedIndex > 0) List.SelectedIndex--;
                List.ScrollIntoView(List.SelectedItem);
                break;
            case VirtualKey.Enter:
                e.Handled = true;
                OpenSelected();
                break;
        }
    }

    // ---------------- list ----------------

    private void List_GotFocus(object sender, RoutedEventArgs e) => _host.OnPaneActivated(this);

    private void List_SelectionChanged(object sender, SelectionChangedEventArgs e) => _host.OnPaneStateChanged(this);

    private void List_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is FileItem item) Open(item);
    }

    internal void OpenSelected()
    {
        if (Tab.IsRecycleBin) return; // recycled entries are only restored or deleted, never opened
        var sel = SelectedItems;
        if (sel.Count == 1) Open(sel[0]);
        else foreach (var f in sel.Where(i => !i.IsDirectory)) Shell.Open(f.Path);
    }

    private void Open(FileItem item)
    {
        if (Tab.IsRecycleBin) return;
        if (item.IsDirectory) Navigate(item.Path);
        else Shell.Open(item.Path);
    }

    internal void SelectAll() => List.SelectAll();

    internal void SelectNone() => List.SelectedItems.Clear();

    internal void InvertSelection()
    {
        var inverted = _visible.Where(i => !List.SelectedItems.Contains(i)).ToList();
        List.SelectedItems.Clear();
        foreach (var item in inverted) List.SelectedItems.Add(item);
    }

    /// <summary>Selects (or deselects) the visible entries whose names match the wildcard <paramref name="patterns"/>.</summary>
    internal void SelectByPattern(string patterns, bool select)
    {
        var names = _visible.Select(i => i.Entry.Name).ToList();
        foreach (var index in WildcardMatcher.Indexes(names, patterns))
        {
            var item = _visible[index];
            if (select && !List.SelectedItems.Contains(item)) List.SelectedItems.Add(item);
            else if (!select) List.SelectedItems.Remove(item);
        }
    }

    internal void MoveSelection(int delta)
    {
        if (_visible.Count == 0) return;
        var i = Math.Clamp(List.SelectedIndex + delta, 0, _visible.Count - 1);
        List.SelectedItems.Clear();
        List.SelectedIndex = i;
        List.ScrollIntoView(_visible[i]);
    }

    /// <summary>Explorer-style type-ahead: typing selects the next entry starting with the typed text.</summary>
    internal void TypeAhead(char c)
    {
        var i = _typeAhead.Next(c, DateTime.UtcNow, List.SelectedIndex, _visible.Select(v => v.Entry.Name).ToList());
        if (i < 0) return;
        List.SelectedItems.Clear();
        List.SelectedIndex = i;
        List.ScrollIntoView(_visible[i]);
        FocusList();
    }

    private void List_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue || args.Item is not FileItem item) return;
        if (args.ItemContainer.ContentTemplateRoot is not Grid root) return;
        if (_shownMode != ViewMode.Details)
        {
            ShowThumbnail(args.ItemContainer, root, item);
            return;
        }

        if (root.Children[0] is not StackPanel sp || sp.Children[1] is not TextBlock name) return;
        name.TextHighlighters.Clear();
        if (item.Highlights.Count == 0) return;
        var hl = new TextHighlighter { Background = Res("PolluxAccentSoftBrush"), Foreground = Res("PolluxAccentBrush") };
        foreach (var r in item.Highlights)
        {
            if (r.Start + r.Length <= item.Name.Length) hl.Ranges.Add(new TextRange { StartIndex = r.Start, Length = r.Length });
        }

        name.TextHighlighters.Add(hl);
    }

    private void List_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        _host.OnPaneActivated(this);
        if ((e.OriginalSource as FrameworkElement)?.DataContext is FileItem item && !List.SelectedItems.Contains(item))
        {
            List.SelectedItems.Clear();
            List.SelectedItems.Add(item);
        }

        // Shift+right-click goes straight to the full Windows menu, like Explorer's "Show more options".
        if (KeyInput.IsDown(VirtualKey.Shift) && SelectedItems.Count > 0 && !Tab.IsRecycleBin)
        {
            _host.Execute("file.shellMenu");
            e.Handled = true;
            return;
        }

        var menu = Tab.IsRecycleBin ? ContextMenus.ForRecycleBin(_host, SelectedItems) : ContextMenus.ForSelection(_host, SelectedItems, Folder, HasSearchResults);
        menu.ShowAt(List, e.GetPosition(List));
        e.Handled = true;
    }

    // ---------------- drag & drop ----------------

    private void List_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        var paths = e.Items.OfType<FileItem>().Select(i => i.Path).ToList();
        CrashLog.Trace($"drag start pane {Index}: {paths.Count} items");
        // Recycled entries leave the bin only through Restore.
        if (paths.Count == 0 || Tab.IsRecycleBin) { e.Cancel = true; return; }
        e.Data.RequestedOperation = DataPackageOperation.Copy | DataPackageOperation.Move;
        e.Data.Properties.Add("PolluxSourceFolder", Folder ?? "");
        e.Data.SetDataProvider(StandardDataFormats.StorageItems, async request =>
        {
            var deferral = request.GetDeferral();
            try { request.SetData(await StorageItemsFor(paths)); }
            finally { deferral.Complete(); }
        });
    }

    internal static async Task<List<IStorageItem>> StorageItemsFor(IEnumerable<string> paths)
    {
        var list = new List<IStorageItem>();
        foreach (var p in paths)
        {
            try
            {
                list.Add(Directory.Exists(p) ? await StorageFolder.GetFolderFromPathAsync(p) : await StorageFile.GetFileFromPathAsync(p));
            }
            catch (Exception ex) when (ex is FileNotFoundException or UnauthorizedAccessException or ArgumentException)
            {
                // Skip items that vanished or are not accessible.
            }
        }

        return list;
    }

    private void List_DragOver(object sender, DragEventArgs e)
    {
        CrashLog.Trace($"drag over pane {Index}: storage={e.DataView.Contains(StandardDataFormats.StorageItems)}");
        if (Folder is null || !e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        var sameFolder = e.DataView.Properties.TryGetValue("PolluxSourceFolder", out var src) && string.Equals(src as string, Folder, StringComparison.OrdinalIgnoreCase);
        if (sameFolder) { e.AcceptedOperation = DataPackageOperation.None; return; }
        var move = e.Modifiers.HasFlag(Windows.ApplicationModel.DataTransfer.DragDrop.DragDropModifiers.Shift);
        e.AcceptedOperation = move ? DataPackageOperation.Move : DataPackageOperation.Copy;
        e.DragUIOverride.Caption = (move ? "Move to " : "Copy to ") + WinPath.Name(Folder);
    }

    private async void List_Drop(object sender, DragEventArgs e)
    {
        CrashLog.Trace($"drop pane {Index}: op={e.AcceptedOperation}");
        if (Folder is null || !e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        var deferral = e.GetDeferral();
        try
        {
            var items = await e.DataView.GetStorageItemsAsync();
            var paths = items.Select(i => i.Path).Where(p => !string.IsNullOrEmpty(p)).ToList();
            _host.Transfer(e.AcceptedOperation == DataPackageOperation.Move, paths, Folder);
        }
        finally
        {
            deferral.Complete();
        }
    }

    // ---------------- home ----------------

    private void RenderHome() => HomeView.Render(HomePanel, _host, this);

    internal static Brush Res(string key) => Theme.Brush(key);
}
