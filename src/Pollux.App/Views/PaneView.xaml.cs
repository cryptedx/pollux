using System.Collections.ObjectModel;
using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Pollux.App.Services;
using Pollux.App.ViewModels;
using Pollux.Core.Files;
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
    private readonly List<TabModel> _tabs = [];
    private readonly Stack<string> _closedTabs = new();
    private static (PaneView Source, int Tab)? _draggedTab;
    private IPaneHost _host = null!;
    private int _active;
    private CancellationTokenSource? _loadCts;
    private List<FileEntry> _entries = [];
    private List<FileItem> _visible = [];
    private FileSystemWatcher? _watcher;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _reloadTimer;
    private string _typeAhead = "";
    private DateTime _typeAheadAt;
    private bool _isActive;
    private string? _error;
    private Core.Git.GitStatus? _git;
    private string? _gitRoot;

    public PaneView()
    {
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
    internal TabModel Tab => _tabs[_active];
    internal IReadOnlyList<TabModel> Tabs => _tabs;
    internal int ActiveTabIndex => _active;

    /// <summary>The folder shown, or null on Home.</summary>
    internal string? Folder => Tab.IsHome ? null : Tab.Location;

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
        foreach (var t in session.Tabs) _tabs.Add(new TabModel(t.Location));
        if (_tabs.Count == 0) _tabs.Add(new TabModel(NewTabLocation()));
        _active = Math.Clamp(session.ActiveTab, 0, _tabs.Count - 1);
        RenderTabs();
        _ = ShowCurrentAsync();
    }

    /// <summary>Replaces all Tabs (used when switching Workspaces).</summary>
    internal void LoadSession(PaneSession session)
    {
        RememberSelection();
        _tabs.Clear();
        foreach (var t in session.Tabs) _tabs.Add(new TabModel(t.Location));
        if (_tabs.Count == 0) _tabs.Add(new TabModel(NewTabLocation()));
        _active = Math.Clamp(session.ActiveTab, 0, _tabs.Count - 1);
        RenderTabs();
        _ = ShowCurrentAsync();
    }

    /// <summary>Stops background work when the window closes for good.</summary>
    internal void Shutdown()
    {
        _loadCts?.Cancel();
        _reloadTimer?.Stop();
        StopWatching();
    }

    internal PaneSession ToSession() => new()
    {
        Tabs = _tabs.Select(t => new TabSession { Location = t.Location }).ToList(),
        ActiveTab = _active,
    };

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
        if (container is not null) container.Focus(FocusState.Keyboard);
        else List.Focus(FocusState.Keyboard);
    }

    // ---------------- tabs ----------------

    /// <summary>Removes a Tab and returns its location (for moving it elsewhere).</summary>
    internal string DetachTab(int index)
    {
        var location = _tabs[index].Location;
        if (_tabs.Count == 1)
        {
            _tabs[0] = new TabModel(NewTabLocation());
            SwitchTo(0, force: true);
            return location;
        }

        _tabs.RemoveAt(index);
        if (_active >= _tabs.Count || index < _active) _active = Math.Max(0, _active - 1);
        SwitchTo(_active, force: true);
        return location;
    }

    internal void InsertTab(int index, string location)
    {
        RememberSelection();
        _tabs.Insert(Math.Clamp(index, 0, _tabs.Count), new TabModel(location));
        _active = Math.Clamp(index, 0, _tabs.Count - 1);
        RenderTabs();
        _ = ShowCurrentAsync();
    }

    internal void ReorderTab(int from, int to)
    {
        if (from == to || from < 0 || from >= _tabs.Count) return;
        var activeTab = _tabs[_active]; // the shown tab stays active, wherever it ends up
        var t = _tabs[from];
        _tabs.RemoveAt(from);
        _tabs.Insert(Math.Clamp(to, 0, _tabs.Count), t);
        _active = _tabs.IndexOf(activeTab);
        RenderTabs();
    }

    internal void NewTab(string? location = null, bool activate = true)
    {
        _tabs.Insert(_active + 1, new TabModel(location ?? NewTabLocation()));
        if (activate) SwitchTo(_active + 1);
        else RenderTabs();
    }

    internal void CloseTab(int index)
    {
        if (index < 0 || index >= _tabs.Count) return;
        _closedTabs.Push(DetachTab(index));
    }

    internal void ReopenClosedTab()
    {
        if (_closedTabs.Count > 0) NewTab(_closedTabs.Pop());
    }

    internal void NextTab(int delta) => SwitchTo(((_active + delta) % _tabs.Count + _tabs.Count) % _tabs.Count);

    internal void SwitchTo(int index, bool force = false)
    {
        if (index == _active && !force) return;
        RememberSelection();
        _active = Math.Clamp(index, 0, _tabs.Count - 1);
        RenderTabs();
        _ = ShowCurrentAsync();
    }

    private string NewTabLocation()
    {
        var l = _host?.Services.Settings.Tabs.NewTabLocation ?? "home";
        return l.Equals("home", StringComparison.OrdinalIgnoreCase) || !Directory.Exists(l) ? WinPath.Home : l;
    }

    private void RenderTabs()
    {
        TabStrip.Children.Clear();
        for (var i = 0; i < _tabs.Count; i++)
        {
            var index = i;
            var on = i == _active;
            var title = new TextBlock { Text = _tabs[i].Title, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 150 };
            var icon = _tabs[i].IsHome
                ? (FrameworkElement)new TextBlock { Text = "⌂", FontSize = 12, VerticalAlignment = VerticalAlignment.Center }
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
            ToolTipService.SetToolTip(tab, _tabs[i].IsHome ? "Home" : _tabs[i].Location);
            tab.CanDrag = true;
            tab.AllowDrop = true;
            tab.DragStarting += (_, e) =>
            {
                _draggedTab = (this, index);
                e.Data.SetText(_tabs[index].Location);
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
            var end = from.Source == this ? _tabs.Count - 1 : _tabs.Count;
            _host.MoveTab(from.Source.Index, from.Tab, Index, targetIndex < 0 ? end : targetIndex);
            return;
        }

        if (from.Tab < 0 || from.Tab >= from.Source.Tabs.Count) return;
        var location = from.Source.DetachTab(from.Tab);
        InsertTab(targetIndex < 0 ? _tabs.Count : targetIndex, location);
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

    internal void Refresh() => _ = ShowCurrentAsync(keepSelection: true);

    private void RememberSelection()
    {
        if (_tabs.Count == 0 || Tab.IsHome) return;
        Tab.SelectedNames = SelectedItems.Select(i => i.Entry.Name).ToList();
    }

    private Task _loading = Task.CompletedTask;

    /// <summary>Completes when the folder of the current Tab is listed (or its load was superseded or failed).</summary>
    internal Task ListingLoaded => _loading;

    /// <summary>Selects exactly the visible entries with these names (Copilot's Pollux tools); returns how many were selected.</summary>
    internal int SelectNames(IReadOnlyList<string> names)
    {
        if (Tab.IsHome) return 0;
        Tab.SelectedNames = [.. names];
        Tab.FocusName = null;
        Present(Tab);
        return SelectedItems.Count;
    }

    private Task ShowCurrentAsync(bool keepSelection = false) => _loading = ShowCurrentCoreAsync(keepSelection);

    private async Task ShowCurrentCoreAsync(bool keepSelection)
    {
        _loadCts?.Cancel();
        var cts = _loadCts = new CancellationTokenSource();
        var tab = Tab;
        if (keepSelection) RememberSelection();
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
            RenderHome();
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
            _error = ex is DirectoryNotFoundException ? "Folder not found." : ex.Message;
        }
        finally
        {
            slowTimer.Stop();
        }

        if (cts.IsCancellationRequested) return;
        LoadingBar.Visibility = Visibility.Collapsed;
        _entries = all;
        Present(tab);
        CrashLog.Trace($"folder {tab.Location}: all {all.Count} rows after {stopwatch.ElapsedMilliseconds} ms");
        Watch(tab.Location);
        _ = LoadGitStatusAsync(tab, cts.Token);
    }

    /// <summary>Applies hidden/system visibility, sort and Filter, then shows the rows and restores selection.</summary>
    private void Present(TabModel tab)
    {
        var s = Settings.Files;
        var comparer = new EntryComparer(s.SortBy, s.SortDescending, s.FoldersFirst);
        var query = tab.Filter ?? "";
        var items = new List<FileItem>(_entries.Count);
        foreach (var e in _entries.Order(comparer))
        {
            if (e.Name.EndsWith(".pollux-part", StringComparison.OrdinalIgnoreCase)) continue; // in-flight transfer
            if (!s.ShowHidden && e.IsHidden) continue;
            if (!s.ShowSystem && e.IsSystem && e.IsHidden) continue;
            var m = FilterMatcher.Match(e.Name, query, Settings.Filter.MatchInitials);
            if (!m.IsMatch) continue;
            var git = _git is not null && _gitRoot is not null && WinPath.IsSameOrUnder(tab.Location, _gitRoot) ? _git.MarkFor(RepoRelative(tab.Location), e.Name, e.IsDirectory) : Core.Git.GitMark.None;
            items.Add(new FileItem(e, s.DateFormat, s.ShowExtensions) { Highlights = m.Ranges, Git = git });
        }

        _visible = items;
        List.ItemsSource = items;
        UpdateSortHeader();

        EmptyText.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = _error ?? (query.Length > 0 ? "no matches" : "empty folder");
        FilterCount.Text = $"{items.Count}/{_entries.Count}";
        InfoText.Text = GitInfo.BranchFor(tab.Location) is { } branch ? "⎇ " + branch : "";

        // Restore selection / focus target.
        var names = tab.FocusName is { } f ? [f] : tab.SelectedNames;
        tab.FocusName = null;
        if (names.Count > 0)
        {
            var set = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
            FileItem? firstSel = null;
            foreach (var it in items)
            {
                if (set.Contains(it.Entry.Name))
                {
                    List.SelectedItems.Add(it);
                    firstSel ??= it;
                }
            }

            if (firstSel is not null) List.ScrollIntoView(firstSel);
        }
        else if (items.Count > 0)
        {
            List.SelectedIndex = 0;
        }

        _host.OnPaneStateChanged(this);
        if (_isActive && FocusManager.GetFocusedElement(XamlRoot) is not TextBox) FocusListSoon();
    }

    private void FocusListSoon() => DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, FocusList);

    private async Task LoadGitStatusAsync(TabModel tab, CancellationToken ct)
    {
        var root = Settings.Files.GitStatus ? GitInfo.RootFor(tab.Location) : null;
        if (root is null)
        {
            if (_git is not null) { _git = null; _gitRoot = null; RememberSelection(); Present(tab); }
            return;
        }

        var status = await GitInfo.StatusAsync(root, ct);
        if (ct.IsCancellationRequested || status is null || tab != Tab) return;
        _git = status;
        _gitRoot = root;
        RememberSelection();
        Present(tab);
    }

    private string RepoRelative(string folder) =>
        _gitRoot is null || folder.Length <= _gitRoot.Length ? "" : folder[(_gitRoot.TrimEnd('\\').Length + 1)..].Replace('\\', '/');

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
        SortModified.Content = "Modified" + (s.SortBy == SortColumn.Modified ? arrow : "");
        SortType.Content = "Type" + (s.SortBy == SortColumn.Type ? arrow : "");
        SortSize.Content = "Size" + (s.SortBy == SortColumn.Size ? arrow : "");
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
        PathBox.Text = Tab.IsHome ? "" : Tab.Location;
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
        var sel = SelectedItems;
        if (sel.Count == 1) Open(sel[0]);
        else foreach (var f in sel.Where(i => !i.IsDirectory)) Shell.Open(f.Path);
    }

    private void Open(FileItem item)
    {
        if (item.IsDirectory) Navigate(item.Path);
        else Shell.Open(item.Path);
    }

    internal void SelectAll() => List.SelectAll();

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
        var now = DateTime.UtcNow;
        _typeAhead = (now - _typeAheadAt).TotalMilliseconds < 900 ? _typeAhead + c : c.ToString();
        _typeAheadAt = now;
        var start = _typeAhead.Length == 1 ? List.SelectedIndex + 1 : Math.Max(0, List.SelectedIndex);
        for (var k = 0; k < _visible.Count; k++)
        {
            var i = (start + k) % _visible.Count;
            if (_visible[i].Entry.Name.StartsWith(_typeAhead, StringComparison.OrdinalIgnoreCase))
            {
                List.SelectedItems.Clear();
                List.SelectedIndex = i;
                List.ScrollIntoView(_visible[i]);
                FocusList();
                return;
            }
        }
    }

    private void List_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue || args.Item is not FileItem item) return;
        if (args.ItemContainer.ContentTemplateRoot is not Grid root) return;
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
        if (KeyInput.IsDown(VirtualKey.Shift) && SelectedItems.Count > 0)
        {
            _host.Execute("file.shellMenu");
            e.Handled = true;
            return;
        }

        var menu = ContextMenus.ForSelection(_host, SelectedItems, Folder);
        menu.ShowAt(List, e.GetPosition(List));
        e.Handled = true;
    }

    // ---------------- drag & drop ----------------

    private void List_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        var paths = e.Items.OfType<FileItem>().Select(i => i.Path).ToList();
        CrashLog.Trace($"drag start pane {Index}: {paths.Count} items");
        if (paths.Count == 0) { e.Cancel = true; return; }
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
