using System.Diagnostics;
using System.Reflection;
using System.Security.Principal;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Pollux.App.Services;
using Pollux.App.ViewModels;
using Pollux.Core;
using Pollux.Copilot.Models;
using Pollux.Core.Commands;
using Pollux.Core.Copilot;
using Pollux.Core.Files;
using Pollux.Core.Navigation;
using Pollux.Core.Places;
using Pollux.Core.Session;
using Pollux.Core.Settings;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using WinRT.Interop;

namespace Pollux.App.Views;

internal sealed partial class MainWindow : Window, IPaneHost
{
    private readonly App _app;
    private readonly PaneView[] _panes;
    private readonly DispatcherTimerLite _statsTimer;
    private SystemStats? _stats;
    private int _activePane;
    private bool _dual = true;
    private bool _sidebar = true;
    private PaletteMode _paletteMode;
    private List<PaletteItem> _paletteSource = [];
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _toastTimer;
    private CancellationTokenSource? _searchCts;
    private Core.Workspaces.Workspace? _workspace;
    private PreviewController? _preview;
    private PreviewController? _previewPane;
    private readonly OverlappedPresenter _presenter = OverlappedPresenter.Create();
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _previewPaneTimer;
    private string? _previewPanePath;
    private TerminalController? _terminal;
    private string? _lastTerminalFolder;

    private enum PaletteMode { Commands, Go, Workspaces, Templates }

    public MainWindow(App app, WindowSession session)
    {
        _app = app;
        InitializeComponent();
        Hwnd = WindowNative.GetWindowHandle(this);
        _panes = [Pane1, Pane2];
        Pane1.Index = 0;
        Pane2.Index = 1;
        HookCompare();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBar);
        VersionText.Text = RunningVersion.Label;
        Root.Loaded += (_, _) => Root.XamlRoot.Changed += (_, _) => UpdateVersionPassthrough();
        ToolTipService.SetToolTip(VersionText, $"Pollux {RunningVersion.Number}{(RunningVersion.Commit is { } c ? $" ({c})" : "")} · click to copy");
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "pollux.ico"));
        AppWindow.Closing += AppWindow_Closing;
        Closed += (_, _) => Cleanup();
        AdminBadge.Visibility = IsElevated ? Visibility.Visible : Visibility.Collapsed;
        if (IsElevated) Title = "Pollux (Admin)";
        UpdatesWindow.ResultChanged += OnUpdatesChanged;
        _app.CopilotQuotaState.Changed += OnCopilotQuotaChanged;
        _app.Transfers.Changed += OnTransferChanged;
        AppWindow.SetPresenter(_presenter); // own instance: casting AppWindow.Presenter fails under Native AOT
        ApplyWindowBounds(session);
        _dual = session.DualPane;
        _sidebar = session.SidebarVisible;
        if (session.Workspace is { } wsName) _workspace = Services.Workspaces.Get(wsName);
        Pane1.Initialize(this, session.Panes.ElementAtOrDefault(0) ?? new PaneSession());
        Pane2.Initialize(this, session.Panes.ElementAtOrDefault(1) ?? new PaneSession());
        _activePane = Math.Clamp(session.ActivePane, 0, 1);
        ApplySettings();
        Services.SettingsChanged += OnSettingsChanged;
        Services.FolderViewsChanged += OnFolderViewsChanged;
        Services.PlacesChanged += RenderSidebar;
        Drives.Changed += OnDrivesChanged;

        _statsTimer = new DispatcherTimerLite(DispatcherQueue, TimeSpan.FromSeconds(Math.Max(1, Services.Settings.StatusBar.RefreshSeconds)), UpdateStats);
        Activated += (_, e) =>
        {
            if (e.WindowActivationState == WindowActivationState.Deactivated)
            {
                // Like VS Code: the palette is a transient prompt and closes when the window loses focus.
                if (PaletteOverlay.Visibility == Visibility.Visible) HidePalette(refocus: false);
                return;
            }

            _statsTimer.Start();
            _app.LastActive = this;
        };
        AppWindow.Changed += (_, e) =>
        {
            if (e.DidVisibilityChange && !AppWindow.IsVisible) _statsTimer.Stop();
        };
        Root.Loaded += (_, _) =>
        {
            SetActivePane(_activePane);
            UpdateStats();
        };
    }

    public AppServices Services => _app.Services;
    public nint Hwnd { get; }
    public bool IsHidden { get; private set; }
    private PaneView Active => _panes[_activePane];
    private PaneView Other => _panes[1 - _activePane];

    public string CurrentFolder => Active.Folder ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public static bool IsElevated { get; } = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    // ---------------- window lifecycle ----------------

    private void ApplyWindowBounds(WindowSession s)
    {
        var scale = Interop.Native.GetDpiForWindow(Hwnd) / 96.0;
        var w = (int)(Math.Max(640, s.Width) * scale);
        var h = (int)(Math.Max(400, s.Height) * scale);
        if (s.X >= 0 && s.Y >= 0 && IsOnScreen(s.X, s.Y)) AppWindow.MoveAndResize(new RectInt32(s.X, s.Y, w, h));
        else AppWindow.Resize(new SizeInt32(w, h));
        if (s.Maximized) _presenter.Maximize();
    }

    /// <summary>True if the saved top-left corner (plus some margin) is on a connected monitor. Win32 instead of
    /// DisplayArea.FindAll(), whose list projection throws "ClassFactory cannot supply requested class" here.</summary>
    private static bool IsOnScreen(int x, int y) =>
        Interop.Native.MonitorFromPoint(new Interop.Native.POINT { X = x + 50, Y = y + 20 }, Interop.Native.MONITOR_DEFAULTTONULL) != 0;

    public WindowSession ToSession()
    {
        var scale = Interop.Native.GetDpiForWindow(Hwnd) / 96.0;
        var maximized = _presenter.State == OverlappedPresenterState.Maximized;
        return new WindowSession
        {
            Panes = [Pane1.ToSession(), Pane2.ToSession()],
            ActivePane = _activePane,
            DualPane = _dual,
            SidebarVisible = _sidebar,
            X = AppWindow.Position.X,
            Y = AppWindow.Position.Y,
            Width = (int)(AppWindow.Size.Width / scale),
            Height = (int)(AppWindow.Size.Height / scale),
            Maximized = maximized,
            Workspace = _workspace?.Name,
        };
    }

    private void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        _app.OnWindowClosing(this);
        if (_app.KeepResident(this))
        {
            args.Cancel = true;
            HideWindow();
        }
    }

    /// <summary>Runs on every real close (also Close() from code, which skips AppWindow.Closing).</summary>
    private void Cleanup()
    {
        _closed = true;
        _archiveCts?.Cancel(); // a zip job must not keep writing for a window that is gone
        lock (_pendingAsks)
        {
            foreach (var t in _pendingAsks.ToList()) t.TrySetResult(new Core.Operations.ConflictDecision(Core.Operations.ConflictChoice.Cancel, false));
        }

        Services.SettingsChanged -= OnSettingsChanged;
        Services.FolderViewsChanged -= OnFolderViewsChanged;
        Services.PlacesChanged -= RenderSidebar;
        Drives.Changed -= OnDrivesChanged;
        UpdatesWindow.ResultChanged -= OnUpdatesChanged;
        _app.CopilotQuotaState.Changed -= OnCopilotQuotaChanged;
        _app.Transfers.Changed -= OnTransferChanged;
        _statsTimer.Stop();
        _stats?.Dispose();
        _terminal?.Dispose();
        _copilotPanel?.Dispose();
        foreach (var p in _panes) p.Shutdown();
    }

    public void HideWindow()
    {
        IsHidden = true;
        _statsTimer.Stop();
        AppWindow.Hide();
    }

    public void ShowWindow(string? navigateTo = null)
    {
        IsHidden = false;
        if (navigateTo is not null) Active.Navigate(navigateTo);
        AppWindow.Show(true);
        Activate();
        Interop.Native.SetForegroundWindow(Hwnd);
        Active.FocusList();
        _statsTimer.Start();
    }

    /// <summary>Opens <paramref name="folder"/> in a new Tab of the active Pane.</summary>
    public void OpenInNewTab(string folder) => Active.NewTab(folder);

    /// <summary>Shows a file selected in its folder.</summary>
    public void Reveal(string path)
    {
        if (Path.GetDirectoryName(path) is { } dir) Active.Navigate(dir, Path.GetFileName(path));
    }

    private void OnUpdatesChanged() => DispatcherQueue.TryEnqueue(() =>
    {
        var n = UpdatesWindow.LastResult.Count;
        UpdatesBadge.Visibility = n > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdatesBadgeText.Text = $"⇪ {n} update{(n == 1 ? "" : "s")}";
    });

    private void OnCopilotQuotaChanged(CopilotQuota? _) => DispatcherQueue.TryEnqueue(ApplyCopilotQuotaBadge);

    /// <summary>Remaining premium requests in the status bar: only while Copilot is available, the item is on and the quota is known.</summary>
    private void ApplyCopilotQuotaBadge()
    {
        var quota = _app.CopilotQuotaState.Latest;
        var show = quota is not null && _app.CopilotAvailable && Services.Settings.StatusBar.CopilotQuota;
        CopilotQuotaBadge.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (quota is null || !show) return;
        CopilotQuotaText.Text = $"Copilot {quota.Compact}";
        ToolTipService.SetToolTip(CopilotQuotaBadge, quota.Text);
        CopilotQuotaText.Foreground = Theme.Brush(quota.Level switch
        {
            CopilotQuotaLevel.Exhausted => "PolluxBadBrush",
            CopilotQuotaLevel.Low => "PolluxWarnBrush",
            _ => "PolluxMutedBrush",
        });
    }

    private void CopilotQuotaBadge_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (!_copilotPanelOpen) ToggleCopilotPanel();
    }

    private void UpdatesBadge_Tapped(object sender, TappedRoutedEventArgs e) => _app.ShowUpdates();

    private void SettingsButton_Click(object sender, RoutedEventArgs e) => _app.ShowSettings();

    private static readonly AppVersion RunningVersion = AppVersion.Parse(
        typeof(MainWindow).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);

    private void VersionText_Tapped(object sender, TappedRoutedEventArgs e)
    {
        CopyText(RunningVersion.ToString());
        ShowToast("Copied " + RunningVersion, 2);
    }

    // The title bar is a drag region; the version label needs a passthrough rect to receive clicks.
    private void VersionText_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateVersionPassthrough();

    private void UpdateVersionPassthrough()
    {
        if (VersionText.XamlRoot is not { } root || VersionText.ActualWidth == 0) return;
        var scale = root.RasterizationScale;
        var b = VersionText.TransformToVisual(null).TransformBounds(new Windows.Foundation.Rect(0, 0, VersionText.ActualWidth, VersionText.ActualHeight));
        var rect = new RectInt32((int)Math.Round(b.X * scale), (int)Math.Round(b.Y * scale), (int)Math.Ceiling(b.Width * scale), (int)Math.Ceiling(b.Height * scale));
        InputNonClientPointerSource.GetForWindowId(AppWindow.Id).SetRegionRects(NonClientRegionKind.Passthrough, [rect]);
    }

    // ---------------- settings & theme ----------------

    private void OnSettingsChanged()
    {
        ApplySettings();
        foreach (var p in _panes) p.ApplySettings();
        CompareSettingsChanged();
        _terminal?.SetTheme();
        _copilotPanel?.SetTheme();
        if (Services.SettingsError is { } err) ShowToast("Settings error: " + err, 6);
    }

    private void OnFolderViewsChanged()
    {
        foreach (var p in _panes) p.ApplyFolderView();
    }

    private void ApplySettings()
    {
        var s = Services.Settings;
        Theme.Apply(s.Appearance.Theme, IsElevated ? "#e5484d" : s.Appearance.AccentColor);
        Theme.PushAccentToResources();
        Root.RequestedTheme = Theme.Current;
        SystemBackdrop = s.Appearance.Mica ? new MicaBackdrop() : null;
        Root.Background = s.Appearance.Mica ? null : Theme.Brush("PolluxSurface2Brush");
        if (AppWindow.TitleBar is { } tb)
        {
            tb.ButtonBackgroundColor = Colors.Transparent;
            tb.ButtonInactiveBackgroundColor = Colors.Transparent;
            tb.ButtonForegroundColor = Theme.Current == ElementTheme.Dark ? Colors.White : Colors.Black;
        }

        if (s.Appearance.UiFont.Length > 0) Root.Resources["ContentControlThemeFontFamily"] = new FontFamily(s.Appearance.UiFont);
        Root.Resources["ControlContentThemeFontSize"] = s.Appearance.FontSize;
        StatusBar.Visibility = s.StatusBar.Visible ? Visibility.Visible : Visibility.Collapsed;
        _statsTimer?.SetInterval(TimeSpan.FromSeconds(Math.Max(1, s.StatusBar.RefreshSeconds)));
        PaletteHint.Text = Services.Keys.KeyFor("palette.show")?.ToString() ?? "";
        ToolTipService.SetToolTip(SettingsButton, Services.Keys.KeyFor("settings.open") is { } key ? $"Settings ({key})" : "Settings");
        ApplyLayout();
        RenderSidebar();
        UpdateStats();
    }

    private void ApplyLayout()
    {
        var preview = Services.Settings.Preview;
        PreviewPane.Visibility = preview.PaneVisible ? Visibility.Visible : Visibility.Collapsed;
        PreviewPaneColumn.Width = preview.PaneVisible ? new GridLength(Math.Clamp(preview.PaneWidth, 220, 1200)) : new GridLength(0);
        if (preview.PaneVisible) SchedulePreviewPane();
        ApplyCopilotPanel();
        ApplyCopilotQuotaBadge();
        SecondPaneColumn.Width = _dual ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        Pane2.Visibility = _dual ? Visibility.Visible : Visibility.Collapsed;
        PanesGrid.ColumnSpacing = _dual ? 6 : 0;
        var showSidebar = _sidebar && Services.Settings.Sidebar.Visible;
        SidebarColumn.Width = showSidebar ? new GridLength(Services.Settings.Sidebar.Width) : new GridLength(0);
        SidebarHost.Visibility = showSidebar ? Visibility.Visible : Visibility.Collapsed;
        if (!_dual && _activePane == 1)
        {
            SetActivePane(0);
        }
    }

    // ---------------- panes ----------------

    public void MoveTab(int fromPane, int fromIndex, int toPane, int toIndex)
    {
        var from = _panes[fromPane];
        var to = _panes[toPane];
        PaneView.EditTabs(from == to ? new[] { from } : new[] { from, to }, () => PaneTabs.Move(from.PaneTabs, fromIndex, to.PaneTabs, toIndex), reload: fromPane != toPane);
        if (fromPane != toPane) SetActivePane(toPane);
    }

    public void OnPaneActivated(PaneView pane)
    {
        _terminalFocused = false;
        if (pane.Index != _activePane) SetActivePane(pane.Index, focus: false);
    }

    private void SetActivePane(int index, bool focus = true)
    {
        _activePane = index;
        for (var i = 0; i < _panes.Length; i++) _panes[i].IsActive = i == index;
        OnPaneStateChanged(Active);
        RenderSidebar();
        if (focus) Active.FocusList();
    }

    public void OnPaneStateChanged(PaneView pane)
    {
        if (pane != Active) return;
        // Locations that are not recorded in Recent (This PC, zips, servers) do not re-render the sidebar on their own.
        if (!string.Equals(_sidebarLocation, pane.Tab.Location, StringComparison.OrdinalIgnoreCase)) RenderSidebar();
        TitlePath.Text = (_workspace is { } ws ? $"⧉ {ws.Name} · " : "") + pane.Tab.Kind switch
        {
            LocationKind.Folder or LocationKind.Zip or LocationKind.Server => ShortPath(pane.Tab.Location),
            LocationKind.RecycleBin => "recycle bin",
            LocationKind.Home => "home",
            _ => pane.Tab.Title,
        };
        var sel = pane.SelectedItems;
        var text = $"{pane.VisibleItems.Count} items";
        if (sel.Count > 1) text = $"{sel.Count} selected · {Format.Size(sel.Where(i => !i.IsDirectory).Sum(i => i.Entry.Size))}";
        else if (sel.Count == 1 && !sel[0].IsDirectory) text += $" · {Format.Size(sel[0].Entry.Size)}";
        SelectionText.Text = Services.Settings.StatusBar.Selection ? text : "";
        SchedulePreviewPane();
        FollowPaneInTerminal();
        UpdateDisk();
        _copilotPanel?.RefreshContext();
    }

    private static string ShortPath(string path)
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return WinPath.IsSameOrUnder(path, profile) ? "~" + path[profile.Length..].Replace('\\', '/') : path;
    }

    // ---------------- sidebar ----------------

    /// <summary>The location the sidebar last highlighted.</summary>
    private string? _sidebarLocation;

    private void RenderSidebar()
    {
        if (SidebarHost.Visibility != Visibility.Visible) return;
        var s = Services;
        var current = _sidebarLocation = Active.Tab.Location;
        SidebarPanel.Children.Clear();
        SidebarPanel.Children.Add(NavRow("Home", WinPath.Home, current, "⌂", ""));
        SidebarPanel.Children.Add(RecycleBinRow(current));
        AddLocationRows(current);

        SidebarPanel.Children.Add(Heading("Bookmarks"));
        var n = 0;
        foreach (var g in s.Bookmarks.Groups)
        {
            if (g.Name.Length > 0)
            {
                var group = g;
                var header = new Button
                {
                    Content = (g.Collapsed ? "▸ " : "▾ ") + g.Name,
                    Style = Ui.FlatButton,
                    FontFamily = Mono,
                    FontSize = 11,
                    Foreground = Theme.Brush("PolluxMutedBrush"),
                    Padding = new Thickness(8, 4, 8, 2),
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    IsTabStop = false,
                };
                header.Click += (_, _) =>
                {
                    group.Collapsed = !group.Collapsed;
                    Services.PlacesUpdated();
                };
                SidebarPanel.Children.Add(header);
            }

            foreach (var b in g.Items)
            {
                n++;
                if (g.Collapsed) continue;
                var row = NavRow(b.Name, b.Path, current, null, n <= 9 ? $"^⇧{n}" : "");
                var bookmark = b;
                var menu = new MenuFlyout();
                var open = new MenuFlyoutItem { Text = "Open in other pane" };
                open.Click += (_, _) => OpenInOtherPane(bookmark.Path);
                var newTab = new MenuFlyoutItem { Text = "Open in new tab" };
                newTab.Click += (_, _) => Active.NewTab(bookmark.Path);
                var rename = new MenuFlyoutItem { Text = "Rename…" };
                rename.Click += async (_, _) =>
                {
                    var name = await PromptAsync("Rename bookmark", bookmark.Name);
                    if (!string.IsNullOrWhiteSpace(name)) { bookmark.Name = name; Services.PlacesUpdated(); }
                };
                var regroup = new MenuFlyoutItem { Text = "Move to group…" };
                regroup.Click += async (_, _) =>
                {
                    var name = await PromptAsync("Group name (empty = ungrouped)", g.Name);
                    if (name is not null) { Services.Bookmarks.Move(bookmark.Path, name.Trim(), int.MaxValue); Services.PlacesUpdated(); }
                };
                var up = new MenuFlyoutItem { Text = "Move up" };
                up.Click += (_, _) => MoveBookmark(bookmark.Path, -1);
                var down = new MenuFlyoutItem { Text = "Move down" };
                down.Click += (_, _) => MoveBookmark(bookmark.Path, 1);
                var remove = new MenuFlyoutItem { Text = "Remove bookmark" };
                remove.Click += (_, _) => { Services.Bookmarks.Remove(bookmark.Path); Services.PlacesUpdated(); };
                foreach (var item in new MenuFlyoutItemBase[] { open, newTab, new MenuFlyoutSeparator(), rename, regroup, up, down, new MenuFlyoutSeparator(), remove }) menu.Items.Add(item);
                row.ContextFlyout = menu;
                SidebarPanel.Children.Add(row);
            }
        }

        if (_workspace is { } workspace)
        {
            SidebarPanel.Children.Add(Heading("Workspace · " + workspace.Name));
            foreach (var b in workspace.Bookmarks.Flat)
            {
                var row = NavRow(b.Name, b.Path, current, null, "");
                var bookmark = b;
                var remove = new MenuFlyoutItem { Text = "Remove from workspace" };
                remove.Click += (_, _) => { workspace.Bookmarks.Remove(bookmark.Path); SaveWorkspace(); RenderSidebar(); };
                var menu = new MenuFlyout();
                menu.Items.Add(remove);
                row.ContextFlyout = menu;
                SidebarPanel.Children.Add(row);
            }

            if (!workspace.Bookmarks.Flat.Any())
            {
                SidebarPanel.Children.Add(new TextBlock { Text = "Bookmark: Add … to Workspace", FontFamily = Mono, FontSize = 10.5, Foreground = Theme.Brush("PolluxMutedBrush"), Margin = new Thickness(8, 0, 8, 0) });
            }
        }

        if (s.Settings.Sidebar.ShowRecent)
        {
            var recent = s.Recent.Ranked(DateTime.Now).Where(r => !s.Bookmarks.Contains(r.Path)).Take(s.Settings.Sidebar.RecentCount).ToList();
            if (recent.Count > 0)
            {
                SidebarPanel.Children.Add(Heading("Recent"));
                foreach (var r in recent) SidebarPanel.Children.Add(NavRow(WinPath.Name(r.Path), r.Path, current, "↺", ""));
            }
        }

        if (s.Settings.Sidebar.ShowDrives)
        {
            SidebarPanel.Children.Add(Heading("Drives"));
            foreach (var d in Drives.Current)
            {
                var used = d.Total == 0 ? 0 : 1 - (double)d.Free / d.Total;
                var panel = new StackPanel { Spacing = 4 };
                var top = new Grid();
                top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                top.Children.Add(new TextBlock { Text = $"{d.Label} ({d.Root.TrimEnd('\\')})", FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis });
                var free = new TextBlock { Text = d.Ready ? Format.Size(d.Free) + " free" : "offline", FontFamily = Mono, FontSize = 10.5, Foreground = Theme.Brush("PolluxMutedBrush"), Margin = new Thickness(6, 0, 0, 0) };
                Grid.SetColumn(free, 1);
                top.Children.Add(free);
                panel.Children.Add(top);
                var bar = HomeView.Bar(used, Services.Settings.Sidebar.Width - 36, 5, used > 0.9 ? "PolluxBadBrush" : "PolluxAccentBrush");
                panel.Children.Add(bar);
                var b = new Button { Content = panel, Style = Ui.FlatButton, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(8, 4, 8, 6), IsTabStop = false, Opacity = d.Ready ? 1 : 0.6 };
                var root = d.Root;
                b.Click += (_, _) => Active.Navigate(root);
                var driveMenu = new MenuFlyout();
                var drive = d;
                if (DriveInput.CanEject(d.Type))
                {
                    var eject = new MenuFlyoutItem { Text = "Eject" };
                    eject.Click += (_, _) => _ = DriveActionAsync(drive, eject: true);
                    driveMenu.Items.Add(eject);
                }

                if (DriveInput.CanDisconnect(d.Type))
                {
                    var disconnect = new MenuFlyoutItem { Text = "Disconnect" };
                    disconnect.Click += (_, _) => _ = DriveActionAsync(drive, eject: false);
                    driveMenu.Items.Add(disconnect);
                }

                var map = new MenuFlyoutItem { Text = "Map network drive…" };
                map.Click += (_, _) => _ = MapNetworkDriveAsync();
                driveMenu.Items.Add(map);
                b.ContextFlyout = driveMenu;
                SidebarPanel.Children.Add(b);
            }
        }
    }

    private async Task MapNetworkDriveAsync()
    {
        var letterBox = new TextBox { Text = DriveInput.NextFreeLetter(Drives.Current.Select(d => d.Root)) ?? "", FontFamily = Mono, Width = 80, Header = "Drive letter" };
        var shareBox = new TextBox { PlaceholderText = @"\\server\share", FontFamily = Mono, MinWidth = 360, Header = "Folder" };
        var error = new TextBlock { Foreground = Theme.Brush("PolluxBadBrush"), Visibility = Visibility.Collapsed, TextWrapping = TextWrapping.Wrap };
        var form = new StackPanel { Spacing = 8 };
        form.Children.Add(shareBox);
        form.Children.Add(letterBox);
        form.Children.Add(error);
        var dialog = new ContentDialog
        {
            Title = "Map network drive",
            Content = form,
            PrimaryButtonText = "Connect",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot,
            RequestedTheme = Theme.Current,
        };
        string letter = "", share = "";
        dialog.PrimaryButtonClick += (_, e) =>
        {
            if (!DriveInput.TryNormalizeShare(shareBox.Text, out share)) error.Text = @"Enter the folder as \\server\share.";
            else if (!DriveInput.TryNormalizeLetter(letterBox.Text, out letter)) error.Text = "Enter a drive letter, for example Z:.";
            else if (Drives.Current.Any(d => d.Root.StartsWith(letter, StringComparison.OrdinalIgnoreCase))) error.Text = $"{letter} is already in use.";
            else return;
            error.Visibility = Visibility.Visible;
            e.Cancel = true;
        };
        shareBox.Loaded += (_, _) => shareBox.Focus(FocusState.Keyboard);
        var result = await dialog.ShowAsync();
        Active.FocusList();
        if (result != ContentDialogResult.Primary) return;

        ShowToast($"Connecting {letter} …");
        var hwnd = Hwnd;
        var message = await Task.Run(() => DriveActions.Map(hwnd, letter, share));
        Drives.Refresh();
        if (message is null) { ShowToast($"{letter} connected"); Active.Navigate(letter + "\\"); }
        else if (message.Length > 0) ShowToast("Could not connect: " + message, 6);
    }

    /// <summary>Ejects a removable or optical drive, or disconnects a network drive.</summary>
    private async Task DriveActionAsync(DriveSnapshot? drive, bool eject)
    {
        if (drive is null) { ShowToast("The current folder is not on a drive"); return; }
        if (eject ? !DriveInput.CanEject(drive.Type) : !DriveInput.CanDisconnect(drive.Type))
        {
            ShowToast(eject ? "This drive cannot be ejected" : "This is not a network drive");
            return;
        }

        var root = drive.Root;
        var letter = root.TrimEnd('\\');
        // Leave the drive first so Pollux itself does not hold it open.
        foreach (var pane in _panes)
        {
            if (pane.Folder is { } f && WinPath.IsSameOrUnder(f, root)) pane.Navigate(WinPath.Home);
        }

        var message = await Task.Run(() => eject ? DriveActions.Eject(letter) : DriveActions.Disconnect(letter));
        Drives.Refresh();
        ShowToast(message is null ? (eject ? $"{letter} ejected" : $"{letter} disconnected") : (eject ? "Could not eject: " : "Could not disconnect: ") + message, message is null ? 3 : 6);
    }

    private void OnDrivesChanged()
    {
        RenderSidebar();
        UpdateDisk();
        foreach (var p in _panes)
        {
            if (p.Tab.IsHome) p.ApplySettings();
        }
    }

    private void MoveBookmark(string path, int delta)
    {
        foreach (var g in Services.Bookmarks.Groups)
        {
            var i = g.Items.FindIndex(b => string.Equals(b.Path, path, StringComparison.OrdinalIgnoreCase));
            if (i < 0) continue;
            var j = Math.Clamp(i + delta, 0, g.Items.Count - 1);
            (g.Items[i], g.Items[j]) = (g.Items[j], g.Items[i]);
            Services.PlacesUpdated();
            return;
        }
    }

    private static FontFamily Mono => Ui.Mono;

    private static TextBlock Heading(string text) => new()
    {
        Text = text.ToUpperInvariant(),
        FontSize = 11,
        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        CharacterSpacing = 60,
        Foreground = Theme.Brush("PolluxMutedBrush"),
        Margin = new Thickness(8, 14, 8, 4),
    };

    private Button NavRow(string name, string target, string current, string? glyph, string hint)
    {
        var grid = new Grid { ColumnSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        FrameworkElement icon = glyph is not null
            ? new TextBlock { Text = glyph, FontSize = 12, Width = 16, TextAlignment = TextAlignment.Center, Foreground = Theme.Brush("PolluxMutedBrush"), VerticalAlignment = VerticalAlignment.Center }
            : File.Exists(target)
                ? new TextBlock { Text = "▤", FontSize = 12, Width = 16, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
                : new Border { Width = 16, Height = 12, CornerRadius = new CornerRadius(2, 4, 3, 3), Background = Theme.Brush("PolluxFolderBrush"), VerticalAlignment = VerticalAlignment.Center };
        grid.Children.Add(icon);
        var label = new TextBlock { Text = name, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(label, 1);
        grid.Children.Add(label);
        if (hint.Length > 0)
        {
            var k = new TextBlock { Text = hint, FontFamily = Mono, FontSize = 10.5, Foreground = Theme.Brush("PolluxMutedBrush"), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(k, 2);
            grid.Children.Add(k);
        }

        var on = string.Equals(target, current, StringComparison.OrdinalIgnoreCase);
        var b = new Button
        {
            Content = grid,
            Style = Ui.FlatButton,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(8, 5, 8, 5),
            Background = on ? Theme.Brush("PolluxAccentSoftBrush") : new SolidColorBrush(Colors.Transparent),
            IsTabStop = false,
        };
        ToolTipService.SetToolTip(b, target == WinPath.Home ? "Home" : target);
        b.Click += (_, _) =>
        {
            if (File.Exists(target)) Shell.Open(target);
            else Active.Navigate(target);
        };
        // Middle-click opens a folder in a background Tab of the active Pane, like a browser.
        b.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, e) =>
        {
            if (!e.GetCurrentPoint(b).Properties.IsMiddleButtonPressed || File.Exists(target)) return;
            Active.NewTab(target, activate: false);
            e.Handled = true;
        }), handledEventsToo: true);
        return b;
    }

    // ---------------- status bar ----------------

    private void UpdateStats()
    {
        var s = Services.Settings.StatusBar;
        if (!s.Visible || IsHidden) return;
        _stats ??= new SystemStats();
        var snap = _stats.Sample();
        StatsPanel.Children.Clear();
        if (s.Cpu) StatsPanel.Children.Add(Stat("CPU", $"{snap.Cpu:0}%", snap.Cpu / 100));
        if (s.Gpu) StatsPanel.Children.Add(Stat("GPU", snap.Gpu is { } g ? $"{g:0}%" : "–", (snap.Gpu ?? 0) / 100));
        if (s.Vram)
        {
            var text = snap.VramUsedGb is { } u ? snap.VramTotalGb is { } t ? $"{u:0.0}/{t:0}G" : $"{u:0.0}G" : "–";
            StatsPanel.Children.Add(Stat("VRAM", text, snap.VramUsedGb is { } uu && snap.VramTotalGb is { } tt && tt > 0 ? uu / tt : 0));
        }

        if (s.Ram) StatsPanel.Children.Add(Stat("RAM", $"{snap.RamUsedGb:0.0}/{snap.RamTotalGb:0}G", snap.RamUsedGb / snap.RamTotalGb));
        if (s.Disk) StatsPanel.Children.Add(_diskText);
        UpdateDisk();
    }

    private readonly TextBlock _diskText = new() { FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center };

    private void UpdateDisk()
    {
        _diskText.FontFamily = Mono;
        _diskText.Foreground = Theme.Brush("PolluxMutedBrush");
        var folder = Active.Folder ?? Environment.GetFolderPath(Environment.SpecialFolder.System);
        var d = Drives.For(folder);
        _diskText.Text = d is { Ready: true } x ? $"{x.Root[..2]} {Format.Size(x.Free)} free" : "";
    }

    private static StackPanel Stat(string label, string value, double fraction)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        p.Children.Add(new TextBlock { Text = label, FontFamily = Mono, FontSize = 11.5, Foreground = Theme.Brush("PolluxMutedBrush"), VerticalAlignment = VerticalAlignment.Center });
        p.Children.Add(HomeView.Bar(fraction, 36, 4, fraction > 0.9 ? "PolluxBadBrush" : "PolluxAccentBrush"));
        p.Children.Add(new TextBlock { Text = value, FontFamily = Mono, FontSize = 11.5, Foreground = Theme.Brush("PolluxMutedBrush"), VerticalAlignment = VerticalAlignment.Center });
        return p;
    }

    // ---------------- keyboard ----------------

    private void Root_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (PaletteOverlay.Visibility == Visibility.Visible) return;
        if (PreviewOverlay.Visibility == Visibility.Visible && HandlePreviewKey(e.Key))
        {
            e.Handled = true;
            return;
        }
        var chord = KeyInput.ToChord(e.Key);
        if (chord is null) return;

        var focused = FocusManager.GetFocusedElement(Content.XamlRoot);
        var inText = focused is TextBox or PasswordBox or RichEditBox;
        var context = BuildContext(focused);
        var command = Services.Keys.Resolve(chord.Value, context);

        // While typing, only modifier chords, function keys and Escape reach commands.
        if (inText && command is not null && chord.Value.Modifiers is KeyModifiers.None or KeyModifiers.Shift && !chord.Value.Key.StartsWith('f') && chord.Value.Key != "escape")
        {
            return;
        }

        if (command is not null)
        {
            e.Handled = true;
            Execute(command);
            return;
        }

        if (!inText && chord.Value.Modifiers is KeyModifiers.None or KeyModifiers.Shift && chord.Value.Key.Length == 1 && focused is ListViewItem or ListView)
        {
            e.Handled = true;
            var c = chord.Value.Key[0];
            Active.TypeAhead(chord.Value.Modifiers == KeyModifiers.Shift ? char.ToUpperInvariant(c) : c);
        }
    }

    private Context BuildContext(object? focused)
    {
        var keys = new List<string>();
        if (Active.IsFilterFocused) keys.Add("filterFocused");
        // Folder Search keys apply in its boxes and the result list, not while editing the path.
        if (Active.IsSearchActive && (focused is not TextBox || Active.IsSearchFocused)) keys.Add("searchActive");
        if (focused is ListViewItem or ListView) keys.Add("listFocused");
        if (focused is TextBox) keys.Add("inputFocused");
        return Context.Of([.. keys]);
    }

    // ---------------- commands ----------------

    public void Execute(string id)
    {
        if (!CopilotAvailability.AllowsCommand(id, _app.CopilotAvailable)) return;
        var p = Active;
        if (Core.Recycle.RecycleBinCommands.Route(id, p.Tab.IsRecycleBin) is not { } routed)
        {
            ShowToast(p.Tab.IsRecycleBin ? "Not available in the Recycle Bin" : "Only in the Recycle Bin");
            return;
        }

        id = routed;
        if ((LocationCommands.Refusal(id, p.Tab.Kind) ?? LocationCommands.VolumeRefusal(id, p.SelectedPaths)) is { } refusal)
        {
            ShowToast(refusal);
            return;
        }

        var command = CommandRegistry.Parse(id);
        switch (command.Kind)
        {
            case CommandKind.SelectionAction:
                RunSelectionAction(id);
                return;
            case CommandKind.OpenWorkspace:
                if (Services.Workspaces.Get(command.Workspace) is { } ws) SwitchWorkspace(ws);
                else ShowToast("No workspace named " + command.Workspace);
                return;
            case CommandKind.GoToBookmark:
                var b = Services.Bookmarks.Flat.ElementAtOrDefault(command.Bookmark - 1);
                if (b is not null) { if (File.Exists(b.Path)) Shell.Open(b.Path); else p.Navigate(b.Path); }
                return;
        }

        switch (id)
        {
            case "palette.show": ShowPalette(PaletteMode.Commands); break;
            case "search.quickOpen": ShowPalette(PaletteMode.Go); break;
            case "filter.show": p.ShowFilter(); break;
            case "filter.close": p.CloseFilter(); break;
            case "search.inFolder": p.ShowSearch(); break;
            case "search.cancel": p.CancelOrCloseSearch(); break;
            case "search.revealInFolder": p.RevealSearchResult(); break;
            case "view.toggleDual" or "view.toggleDualAlt": ToggleDual(); break;
            case "view.toggleSidebar": _sidebar = !_sidebar; if (_sidebar && !Services.Settings.Sidebar.Visible) { Services.Settings.Sidebar.Visible = true; Services.SaveSettings(); } ApplyLayout(); RenderSidebar(); break;
            case "view.toggleHidden": Services.Settings.Files.ShowHidden = !Services.Settings.Files.ShowHidden; Services.SaveSettings(); ShowToast(Services.Settings.Files.ShowHidden ? "Showing hidden files" : "Hiding hidden files"); break;
            case "view.toggleStatusBar": Services.Settings.StatusBar.Visible = !Services.Settings.StatusBar.Visible; Services.SaveSettings(); break;
            case "view.toggleTheme": Services.Settings.Appearance.Theme = (ThemeMode)(((int)Services.Settings.Appearance.Theme + 1) % 3); Services.SaveSettings(); ShowToast("Theme: " + Services.Settings.Appearance.Theme); break;
            case "view.refresh": p.Refresh(); break;
            case "view.calculateFolderSizes": p.CalculateFolderSizes(); break;
            case "view.sortName": p.SetSort(SortColumn.Name); break;
            case "view.sortModified": p.SetSort(SortColumn.Modified); break;
            case "view.sortSize": p.SetSort(SortColumn.Size); break;
            case "view.sortType": p.SetSort(SortColumn.Type); break;
            case "view.details" or "view.tiles" or "view.icons" or "view.cycleMode": p.SetViewMode(ViewModes.ForCommand(id, p.ViewMode) ?? p.ViewMode); ShowToast("View: " + ViewModes.Label(p.ViewMode)); break;
            case "view.sortCreated": p.SetSort(SortColumn.Created); break;
            case "view.sortExtension": p.SetSort(SortColumn.Extension); break;
            case "view.sortAttributes": p.SetSort(SortColumn.Attributes); break;
            case "view.sortDimensions": p.SetSort(SortColumn.Dimensions); break;
            case "view.sortDuration": p.SetSort(SortColumn.Duration); break;
            case "view.sortAuthors": p.SetSort(SortColumn.Authors); break;
            case "view.sortTitle": p.SetSort(SortColumn.Title); break;
            case "view.sortFileVersion": p.SetSort(SortColumn.FileVersion); break;
            case "view.sortCompany": p.SetSort(SortColumn.Company); break;
            case "view.sortDateTaken": p.SetSort(SortColumn.DateTaken); break;
            case "view.groupBy": ShowToast("Group by: " + Core.Files.EntryGrouping.Describe(p.CycleGrouping())); break;
            case "view.resetFolder": p.ResetFolderView(); break;
            case "view.applyToSubfolders": p.ApplyViewToSubfolders(); break;
            case "pane.focus1": SetActivePane(0); break;
            case "pane.focus2": if (_dual) SetActivePane(1); break;
            case "pane.switch": if (_dual) SetActivePane(1 - _activePane); break;
            case "pane.mirror": OpenInOtherPane(p.Tab.Location); break;
            case "pane.swap": SwapPanes(); break;
            case "pane.compare": ToggleCompare(); break;
            case "pane.compareRecursive": ToggleCompareSetting(recursive: true); break;
            case "pane.compareContent": ToggleCompareSetting(recursive: false); break;
            case "pane.selectDifferences": _ = SelectDifferencesAsync(); break;
            case "pane.syncToOther": _ = SyncToOtherAsync(); break;
            case "tab.new": p.NewTab(); break;
            case "tab.close": p.CloseTab(p.ActiveTabIndex); break;
            case "tab.reopen": p.ReopenClosedTab(); break;
            case "tab.next": p.NextTab(1); break;
            case "tab.prev": p.NextTab(-1); break;
            case "tab.duplicate": p.NewTab(p.Tab.Location); break;
            case "tab.moveToNewWindow":
                {
                    var location = p.Tab.Location;
                    p.DetachTab(p.ActiveTabIndex);
                    _app.OpenNewWindow(location == WinPath.Home ? null : location);
                    break;
                }
            case "nav.back": p.Back(); break;
            case "nav.forward": p.Forward(); break;
            case "nav.up" or "nav.upBackspace": p.Up(); break;
            case "nav.home": p.Navigate(WinPath.Home); break;
            case "nav.editPath": p.EditPath(); break;
            case "nav.downloads": p.Navigate(KnownFolders.Downloads); break;
            case "nav.desktop": p.Navigate(KnownFolders.Desktop); break;
            case "nav.thisPc": p.Navigate(WinPath.ThisPc); break;
            case "nav.network": p.Navigate(WinPath.Network); break;
            case "nav.libraries": p.Navigate(WinPath.Libraries); break;
            case "nav.controlPanel": p.Navigate(WinPath.ControlPanel); break;
            case "drive.mapNetwork": _ = MapNetworkDriveAsync(); break;
            case "drive.disconnect": _ = DriveActionAsync(p.Folder is { } df ? Drives.For(df) : null, eject: false); break;
            case "drive.eject": _ = DriveActionAsync(p.Folder is { } ef ? Drives.For(ef) : null, eject: true); break;
            case "nav.recycleBin": p.Navigate(WinPath.RecycleBin); break;
            case "recycle.restore": _ = RestoreRecycledAsync(); break;
            case "recycle.deletePermanent": _ = DeleteRecycledAsync(); break;
            case "recycle.empty": _ = EmptyRecycleBinAsync(); break;
            case "file.open": p.OpenSelected(); break;
            case "file.preview": TogglePreview(); break;
            case "copilot.togglePanel": ToggleCopilotPanel(); break;
            case "view.togglePreviewPane": Services.Settings.Preview.PaneVisible = !Services.Settings.Preview.PaneVisible; Services.SaveSettings(); break;
            case "file.openWith": _ = OpenWithAsync(p); break;
            case "file.openExternal": p.OpenSelectedExternally(); break;
            case "file.rename": _ = RenameAsync(); break;
            case "file.batchRename": _ = BatchRenameAsync(); break;
            case "file.delete": Delete(permanent: false); break;
            case "file.deletePermanent": Delete(permanent: true); break;
            case "file.copy": _ = p.IsCopyOutOnly(p.SelectedPaths) ? CopyOutToClipboardAsync(p) : SetClipboardAsync(move: false); break;
            case "file.cut": _ = SetClipboardAsync(move: true); break;
            case "file.paste": _ = PasteAsync(); break;
            case "edit.undo": RunUndo(redo: false); break;
            case "edit.redo": RunUndo(redo: true); break;
            case "file.copyToOther": if (p.IsCopyOutOnly(p.SelectedPaths)) CopyOutToOther(p); else TransferToOther(move: false); break;
            case "file.moveToOther": TransferToOther(move: true); break;
            case "file.newFolder": _ = NewItemAsync(folder: true); break;
            case "file.newFile": _ = NewItemAsync(folder: false); break;
            case "file.newFromTemplate": _ = ShowTemplatePaletteAsync(); break;
            case "file.createShortcut": _ = CreateLinkAsync(LinkKind.Shortcut, inOther: false); break;
            case "file.createShortcutInOther": _ = CreateLinkAsync(LinkKind.Shortcut, inOther: true); break;
            case "file.createSymlink": _ = CreateLinkAsync(LinkKind.Symlink, inOther: true); break;
            case "file.createJunction": _ = CreateLinkAsync(LinkKind.Junction, inOther: true); break;
            case "file.properties": ShowProperties(p); break;
            case "file.copyPath": CopyText(string.Join(Environment.NewLine, TargetPaths())); break;
            case "file.copyPathQuoted": CopyText(string.Join(" ", TargetPaths().Select(x => $"\"{x}\""))); break;
            case "file.copyPathForward": CopyText(string.Join(Environment.NewLine, TargetPaths().Select(x => x.Replace('\\', '/')))); break;
            case "file.copyName": CopyText(string.Join(Environment.NewLine, p.SelectedItems.Count > 0 ? p.SelectedItems.Select(i => i.Entry.Name) : TargetPaths().Select(Path.GetFileName))); break;
            case "file.selectAll": p.SelectAll(); break;
            case "file.selectNone": p.SelectNone(); break;
            case "file.invertSelection": p.InvertSelection(); break;
            case "file.selectByPattern": _ = SelectByPatternAsync(p, select: true); break;
            case "file.deselectByPattern": _ = SelectByPatternAsync(p, select: false); break;
            case "file.shellMenu": ShowShellMenu(); break;
            case "transfers.pauseAll": foreach (var j in _app.Transfers.Jobs.Where(j => !j.IsFinished)) j.Pause(); UpdateTransfersUi(); break;
            case "transfers.resumeAll": foreach (var j in _app.Transfers.Jobs.Where(j => !j.IsFinished)) j.Resume(); UpdateTransfersUi(); break;
            case "transfers.cancelAll": foreach (var j in _app.Transfers.Jobs.Where(j => !j.IsFinished)) j.Cancel(); break;
            case "transfers.show": TransfersBadge.Visibility = Visibility.Visible; RenderTransfersPanel(); FlyoutBase.ShowAttachedFlyout(TransfersBadge); break;
            case "file.compress": StartArchive(compress: true, here: false); break;
            case "file.extract": StartArchive(compress: false, here: false); break;
            case "file.extractHere": StartArchive(compress: false, here: true); break;
            case "file.archiveCancel": _archiveCts?.Cancel(); break;
            case "file.showInExplorer": Shell.RevealInExplorer(Locations.ParsingName(p.SelectedPaths.FirstOrDefault() ?? p.Folder ?? (p.Tab.IsHome ? "" : p.Tab.Location))); break;
            case "bookmark.add": AddBookmark(p.Folder); break;
            case "bookmark.addSelection": AddBookmark(p.SelectedPaths.FirstOrDefault() ?? p.Folder); break;
            case "bookmark.remove": if (p.Folder is { } bf && Services.Bookmarks.Remove(bf)) { Services.PlacesUpdated(); ShowToast("Bookmark removed"); } break;
            case "terminal.toggle" or "terminal.toggleAlt": ToggleTerminal(); break;
            case "terminal.cdHere": if (_terminal is not null) { _terminal.ChangeDirectory(TerminalFolder()); _terminal.Focus(); } break;
            case "terminal.open": ShowLaunchError(Shell.OpenTerminal(TerminalFolder(), Services.Settings.Terminal)); break;
            case "terminal.openPowerShell": ShowLaunchError(Shell.OpenShell(TerminalFolder(), "pwsh")); break;
            case "integration.vscode": ShowLaunchError(Shell.OpenInVsCode(p.SelectedPaths.FirstOrDefault() ?? TerminalFolder(), Services.Settings.Integration.VsCodeCommand)); break;
            case "integration.copilot": ShowLaunchError(Shell.OpenInCopilot(p.SelectedPaths.FirstOrDefault(Directory.Exists) ?? TerminalFolder(), Services.Settings.Integration.CopilotAppCommand)); break;
            case "recent.clear": Services.Recent.Clear(); Services.PlacesUpdated(); break;
            case "workspace.switch": ShowPalette(PaletteMode.Workspaces); break;
            case "workspace.autosave": SaveWorkspace(); break;
            case "workspace.saveAs": _ = SaveWorkspaceAsAsync(); break;
            case "workspace.save": if (_workspace is null) _ = SaveWorkspaceAsAsync(); else { SaveWorkspace(); ShowToast("Workspace saved: " + _workspace.Name); } break;
            case "workspace.rename": _ = RenameWorkspaceAsync(); break;
            case "workspace.delete": _ = DeleteWorkspaceAsync(); break;
            case "workspace.detach": _workspace = null; OnPaneStateChanged(Active); RenderSidebar(); ShowToast("Left workspace"); break;
            case "workspace.setTerminalFolder": if (_workspace is not null && p.Folder is { } tf) { _workspace.TerminalFolder = tf; SaveWorkspace(); ShowToast("Terminal folder: " + tf); } break;
            case "bookmark.addToWorkspace":
                if (_workspace is null) { ShowToast("No workspace active (Ctrl+Alt+W)"); break; }
                if (p.Folder is { } wf) { _workspace.Bookmarks.Add(wf, WinPath.Name(wf)); SaveWorkspace(); RenderSidebar(); ShowToast("Added to workspace " + _workspace.Name); }
                break;
            case "window.new": _app.OpenNewWindow(); break;
            case "window.close": AppWindow_CloseRequested(); break;
            case "settings.open": _app.ShowSettings(); break;
            case "settings.openJson": Shell.Open(Services.SettingsFile); break;
            case "keybindings.openJson": Shell.Open(Services.KeybindingsFile); break;
            case "app.openConfigFolder": Active.Navigate(Services.Paths.ConfigDirectory); break;
            case "app.quit": _app.Quit(); break;
            default:
                if (!_app.ExecuteAppCommand(id, this)) ShowToast("Unknown command: " + id);
                break;
        }
    }

    private void AppWindow_CloseRequested()
    {
        _app.OnWindowClosing(this);
        if (_app.KeepResident(this)) HideWindow();
        else Close();
    }

    private void ToggleDual()
    {
        _dual = !_dual;
        ApplyLayout();
        if (_dual) SetActivePane(1);
        CompareLayoutChanged();
    }

    private void SwapPanes()
    {
        var a = Pane1.PaneTabs.ToSession();
        var b = Pane2.PaneTabs.ToSession();
        PaneView.EditTabs([Pane1, Pane2], () =>
        {
            Pane1.PaneTabs.Load(b);
            Pane2.PaneTabs.Load(a);
        });
    }

    /// <summary>Shows a location in the pane that is not active, turning dual pane on if needed.</summary>
    private void OpenInOtherPane(string location)
    {
        var target = Other;
        if (!_dual)
        {
            _dual = true;
            ApplyLayout();
        }

        target.Navigate(location);
        SetActivePane(target.Index);
    }

    private string TerminalFolder() =>
        Active.Folder
        ?? (_workspace is { TerminalFolder.Length: > 0 } w && Directory.Exists(w.TerminalFolder) ? w.TerminalFolder : null)
        ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    // ---------------- embedded terminal ----------------

    private void ToggleTerminal()
    {
        var t = Services.Settings.Terminal;
        if (TerminalHost.Visibility == Visibility.Visible)
        {
            if (_terminalFocused)
            {
                TerminalHost.Visibility = Visibility.Collapsed;
                TerminalRow.Height = new GridLength(0);
                _terminalFocused = false;
                Active.FocusList();
            }
            else
            {
                _terminalFocused = true;
                _terminal?.RestartIfExited(t.EmbeddedShell);
                _terminal?.Focus();
            }

            return;
        }

        TerminalHost.Visibility = Visibility.Visible;
        TerminalRow.Height = new GridLength(t.PanelHeight);
        _terminal ??= new TerminalController(TerminalHost, TerminalFolder, ToggleTerminal);
        _terminalFocused = true;
        _lastTerminalFolder = TerminalFolder();
        _ = _terminal.EnsureStartedAsync(t.EmbeddedShell, t.FontSize);
    }

    private bool _terminalFocused;

    // ---------------- Copilot Panel (one per window, created on first open) ----------------

    private const double CopilotPanelWidth = 380;
    private CopilotPanel? _copilotPanel;
    private bool _copilotPanelOpen;

    private void ToggleCopilotPanel()
    {
        _copilotPanelOpen = !_copilotPanelOpen;
        ApplyCopilotPanel();
        if (!_copilotPanelOpen) Active.FocusList();
    }

    /// <summary>Shows the Panel when it is open and Copilot is available; disabling Copilot closes it (no Copilot UI anywhere).</summary>
    private void ApplyCopilotPanel()
    {
        if (!_app.CopilotAvailable) _copilotPanelOpen = false;
        CopilotPane.Visibility = _copilotPanelOpen ? Visibility.Visible : Visibility.Collapsed;
        CopilotPaneColumn.Width = _copilotPanelOpen ? new GridLength(CopilotPanelWidth) : new GridLength(0);
        if (_copilotPanelOpen) _copilotPanel ??= new CopilotPanel(CopilotPane, _app, () => { _copilotPanelOpen = false; ApplyCopilotPanel(); }, CopilotContext,
            new WindowPolluxActions(DispatcherQueue, _panes, () => _activePane, () => _dual, Hwnd, AddBookmark, _app.Undo.Record), PickFolderAsync);
    }

    /// <summary>A folder dialog owned by this window (Copilot Working Folder); null when cancelled.</summary>
    private async Task<string?> PickFolderAsync()
    {
        var picker = new Microsoft.Windows.Storage.Pickers.FolderPicker(AppWindow.Id) { SuggestedStartLocation = Microsoft.Windows.Storage.Pickers.PickerLocationId.ComputerFolder };
        var result = await picker.PickSingleFolderAsync();
        return result?.Path;
    }

    /// <summary>Selection actions (context menu "Copilot", palette): a new Chat about the selected entries.</summary>
    private void RunSelectionAction(string id)
    {
        if (SelectionActions.Find(Services.Settings.Copilot, id) is not { } action) return;
        var chips = Pollux.Copilot.Context.ContextChips.Defaults(CopilotContext());
        if (!chips.Any(c => c.Kind == Pollux.Copilot.Context.ContextChipKind.Selection)) { ShowToast("Select something first"); return; }
        _copilotPanelOpen = true;
        ApplyCopilotPanel();
        _copilotPanel?.StartChat(action.Prompt, chips);
    }

    /// <summary>What the next Copilot message may carry: the active Pane, the other one (when dual) and the folders of all Tabs.</summary>
    private Pollux.Copilot.Context.ContextSnapshot CopilotContext()
    {
        static Pollux.Copilot.Context.PaneContext Of(PaneView p) =>
            new(p.Folder, p.SelectedPaths, [.. p.VisibleItems.Select(i => i.Path)]);
        IEnumerable<PaneView> panes = _dual ? _panes : [Active];
        var tabs = panes.SelectMany(p => p.Tabs).Where(t => t.Folder is not null).Select(t => t.Location).ToList();
        return new(Of(Active), _dual ? Of(Other) : null, tabs);
    }

    private void FollowPaneInTerminal()
    {
        if (_terminal is null || !Services.Settings.Terminal.FollowPane || TerminalHost.Visibility != Visibility.Visible) return;
        var f = Active.Folder;
        if (f is null || string.Equals(f, _lastTerminalFolder, StringComparison.OrdinalIgnoreCase)) return;
        _lastTerminalFolder = f;
        _terminal.ChangeDirectory(f);
    }

    // ---------------- transfers (copy / move) ----------------

    private readonly SemaphoreSlim _dialogGate = new(1, 1);
    private readonly List<TaskCompletionSource<Core.Operations.ConflictDecision>> _pendingAsks = [];
    private bool _closed;
    private bool _transferUiPending;

    /// <summary>Copies or moves through Pollux's queue (progress in the status bar), or through Windows when configured.</summary>
    public void Transfer(bool move, IReadOnlyList<string> sources, string targetDirectory)
    {
        if (sources.Count == 0) return;
        if (string.Equals(Services.Settings.Files.OperationEngine, "windows", StringComparison.OrdinalIgnoreCase))
        {
            // Names taken before the shell ran: an entry that replaced or merged into one of them is not undoable.
            var before = sources.Select(s => Path.Combine(targetDirectory, Path.GetFileName(s.TrimEnd('\\'))))
                .Where(t => File.Exists(t) || Directory.Exists(t)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            IReadOnlyList<(string OldPath, string NewPath)> renamed;
            if (move) Shell.Move(Hwnd, sources, targetDirectory, out renamed);
            else Shell.Copy(Hwnd, sources, targetDirectory, out renamed);
            _app.Undo.Record(Core.Operations.UndoEntry.ShellTransfer(
                move ? Core.Operations.TransferKind.Move : Core.Operations.TransferKind.Copy, sources, targetDirectory, renamed, before.Contains, FileUndo.Target(Hwnd)));
            foreach (var p in _panes) p.Refresh();
            return;
        }

        var request = new Core.Operations.TransferRequest(move ? Core.Operations.TransferKind.Move : Core.Operations.TransferKind.Copy, sources, targetDirectory, Services.Settings.Files.ConflictPolicy);
        _app.Transfers.Enqueue(request, AskConflictAsync);
    }

    /// <summary>Called from the transfer worker; shows one conflict dialog at a time on this window.</summary>
    private Task<Core.Operations.ConflictDecision> AskConflictAsync(Core.Operations.TransferConflict c)
    {
        var tcs = new TaskCompletionSource<Core.Operations.ConflictDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new Core.Operations.ConflictDecision(Core.Operations.ConflictChoice.Cancel, false);
        if (_closed)
        {
            // The window that started the job is gone: cancel the job instead of blocking the shared queue.
            tcs.TrySetResult(cancelled);
            return tcs.Task;
        }

        lock (_pendingAsks) _pendingAsks.Add(tcs);
        tcs.Task.ContinueWith(_ => { lock (_pendingAsks) _pendingAsks.Remove(tcs); }, TaskScheduler.Default);
        var queued = DispatcherQueue.TryEnqueue(async () =>
        {
            await _dialogGate.WaitAsync();
            try
            {
                if (IsHidden) ShowWindow();
                var all = new CheckBox { Content = "Do this for all conflicts in this job" };
                var info = new TextBlock { FontFamily = Mono, FontSize = 12, TextWrapping = TextWrapping.Wrap };
                info.Text = Core.Operations.TransferBoard.ConflictText(c);
                var content = new StackPanel { Spacing = 12 };
                content.Children.Add(info);
                content.Children.Add(all);
                var dialog = new ContentDialog
                {
                    Title = "File already exists",
                    Content = content,
                    PrimaryButtonText = "Replace",
                    SecondaryButtonText = "Keep both",
                    CloseButtonText = "Skip",
                    DefaultButton = ContentDialogButton.Close,
                    XamlRoot = Content.XamlRoot,
                    RequestedTheme = Theme.Current,
                };
                var cancel = new HyperlinkButton { Content = "Cancel the whole job", Padding = new Thickness(0) };
                content.Children.Add(cancel);
                var cancelled = false;
                cancel.Click += (_, _) => { cancelled = true; dialog.Hide(); };
                var result = await dialog.ShowAsync();
                var choice = cancelled ? Core.Operations.ConflictChoice.Cancel : result switch
                {
                    ContentDialogResult.Primary => Core.Operations.ConflictChoice.Overwrite,
                    ContentDialogResult.Secondary => Core.Operations.ConflictChoice.Rename,
                    _ => Core.Operations.ConflictChoice.Skip,
                };
                tcs.TrySetResult(new Core.Operations.ConflictDecision(choice, all.IsChecked == true));
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
            {
                tcs.TrySetResult(new Core.Operations.ConflictDecision(Core.Operations.ConflictChoice.Skip, false));
            }
            finally
            {
                _dialogGate.Release();
            }
        });
        if (!queued) tcs.TrySetResult(cancelled);
        return tcs.Task;
    }

    /// <summary>Worker-thread callback; coalesces UI updates to at most ~10 per second.</summary>
    private void OnTransferChanged(Core.Operations.TransferJob job)
    {
        if (job.IsFinished)
        {
            DispatcherQueue.TryEnqueue(() => OnTransferFinished(job));
        }

        if (_transferUiPending) return;
        _transferUiPending = true;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, async () =>
        {
            await Task.Delay(100);
            _transferUiPending = false;
            UpdateTransfersUi();
        });
    }

    private readonly HashSet<int> _announced = [];

    private void OnTransferFinished(Core.Operations.TransferJob job)
    {
        UpdateTransfersUi();
        foreach (var p in _panes)
        {
            if (p.Folder is { } f && Core.Operations.TransferBoard.Affects(job, f)) p.Refresh();
        }

        if (!_announced.Add(job.Id) || _app.LastActive != this) return;
        var (text, seconds) = Core.Operations.TransferBoard.FinishedToast(job);
        ShowToast(text, seconds);
    }

    private void UpdateTransfersUi()
    {
        var jobs = _app.Transfers.Jobs;
        TransfersBadge.Visibility = Core.Operations.TransferBoard.BadgeVisible(jobs) ? Visibility.Visible : Visibility.Collapsed;
        if (jobs.FirstOrDefault(j => !j.IsFinished) is { } current)
        {
            var p = current.Progress;
            TransfersBar.Value = p.Fraction;
            TransfersBar.IsIndeterminate = p.TotalFiles == 0;
            TransfersBar.ShowPaused = current.State == Core.Operations.TransferState.Paused;
        }
        else
        {
            TransfersBar.Value = 1;
            TransfersBar.IsIndeterminate = false;
        }

        TransfersText.Text = Core.Operations.TransferBoard.StatusText(jobs);

        if (TransfersFlyout.IsOpen) RefreshTransfersPanel();
    }

    private void TransfersBadge_Tapped(object sender, TappedRoutedEventArgs e)
    {
        RenderTransfersPanel();
        FlyoutBase.ShowAttachedFlyout(TransfersBadge);
    }

    private sealed class TransferRow
    {
        public required ProgressBar Bar { get; init; }
        public required TextBlock Status { get; init; }
        public required TextBlock Current { get; init; }
        public Button? Pause { get; init; }
    }

    private readonly Dictionary<int, TransferRow> _transferRows = [];
    private readonly Dictionary<int, Core.Operations.TransferBoard.RowSnapshot> _shownRows = [];

    /// <summary>
    /// Updates the open panel in place. Rebuilding it on every progress tick replaced the buttons under the mouse,
    /// so a click (press + release) never reached them.
    /// </summary>
    private void RefreshTransfersPanel()
    {
        var jobs = VisibleTransferJobs();
        if (Core.Operations.TransferBoard.NeedsRebuild(_shownRows, jobs))
        {
            RenderTransfersPanel();
            return;
        }

        foreach (var job in jobs) FillRow(_transferRows[job.Id], job);
    }

    private List<Core.Operations.TransferJob> VisibleTransferJobs() => Core.Operations.TransferBoard.VisibleJobs(_app.Transfers.Jobs);

    private static void FillRow(TransferRow row, Core.Operations.TransferJob job)
    {
        var p = job.Progress;
        row.Bar.Value = job.State == Core.Operations.TransferState.Completed ? 1 : p.Fraction;
        row.Status.Text = Core.Operations.TransferBoard.RowStatus(job);
        row.Current.Text = job.IsFinished ? "" : p.CurrentFile;
        row.Current.Visibility = row.Current.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RenderTransfersPanel()
    {
        TransfersPanel.Children.Clear();
        _transferRows.Clear();
        _shownRows.Clear();
        foreach (var job in VisibleTransferJobs())
        {
            var box = new StackPanel { Spacing = 3 };
            var head = new Grid { ColumnSpacing = 8 };
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.Children.Add(new TextBlock { Text = job.Title, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
            Button? pause = null;
            if (!job.IsFinished)
            {
                var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
                pause = new Button { Content = job.State == Core.Operations.TransferState.Paused ? "Resume" : "Pause", FontSize = 11, Padding = new Thickness(8, 2, 8, 2) };
                pause.Click += (_, _) =>
                {
                    if (job.State == Core.Operations.TransferState.Paused) job.Resume();
                    else job.Pause();
                    RenderTransfersPanel();
                };
                var cancel = new Button { Content = "Cancel", FontSize = 11, Padding = new Thickness(8, 2, 8, 2) };
                cancel.Click += (_, _) => job.Cancel();
                buttons.Children.Add(pause);
                buttons.Children.Add(cancel);
                Grid.SetColumn(buttons, 1);
                head.Children.Add(buttons);
            }

            box.Children.Add(head);
            var bar = new ProgressBar { Maximum = 1, ShowPaused = job.State == Core.Operations.TransferState.Paused, ShowError = job.State == Core.Operations.TransferState.Failed };
            var status = new TextBlock { FontFamily = Mono, FontSize = 11, Foreground = Theme.Brush("PolluxMutedBrush"), TextTrimming = TextTrimming.CharacterEllipsis };
            var current = new TextBlock { FontFamily = Mono, FontSize = 10.5, Foreground = Theme.Brush("PolluxMutedBrush"), TextTrimming = TextTrimming.CharacterEllipsis };
            box.Children.Add(bar);
            box.Children.Add(status);
            box.Children.Add(current);
            var errors = job.Errors;
            foreach (var err in errors.Take(5))
                box.Children.Add(new TextBlock { Text = "⚠ " + err, FontFamily = Mono, FontSize = 10.5, Foreground = Theme.Brush("PolluxBadBrush"), TextWrapping = TextWrapping.Wrap });
            var row = new TransferRow { Bar = bar, Status = status, Current = current, Pause = pause };
            FillRow(row, job);
            _transferRows[job.Id] = row;
            _shownRows[job.Id] = new Core.Operations.TransferBoard.RowSnapshot(job.State, errors.Count);
            TransfersPanel.Children.Add(box);
        }

        var clear = new Button { Content = "Clear finished", FontSize = 11, HorizontalAlignment = HorizontalAlignment.Right };
        clear.Click += (_, _) => { _app.Transfers.ClearFinished(); RenderTransfersPanel(); UpdateTransfersUi(); };
        TransfersPanel.Children.Add(clear);
    }

    // ---------------- native context menu ----------------

    /// <summary>The full Windows context menu for the selection (or the current folder), at the mouse position.</summary>
    public void ShowShellMenu()
    {
        if (!Active.Tab.Access.IsFileSystem && !Active.Tab.IsRecycleBin && !Active.Tab.IsHome)
        {
            ShowVirtualShellMenu(Active);
            return;
        }

        var paths = Active.SelectedPaths;
        if (paths.Count == 0 && Active.Folder is { } f) paths = [f];
        if (paths.Count == 0) return;
        Interop.Native.GetCursorPos(out var pt);
        var extended = KeyInput.IsDown(Windows.System.VirtualKey.Shift);
        if (Interop.ShellContextMenu.Show(Hwnd, paths, pt.X, pt.Y, extended)) Active.Refresh();
    }

    // ---------------- preview ----------------

    private void TogglePreview()
    {
        if (PreviewOverlay.Visibility == Visibility.Visible)
        {
            ClosePreview();
            return;
        }

        var item = Active.SelectedItems.FirstOrDefault();
        if (item is null) return;
        if (_preview is null)
        {
            _preview = new PreviewController(PreviewHost, PreviewTitle, PreviewMeta);
            _preview.ForwardedKey += key => HandlePreviewKey(key);
        }

        _preview.MaxText = Services.Settings.Preview.MaxTextMegabytes * 1024L * 1024;
        PreviewOverlay.Visibility = Visibility.Visible;
        var pane = Active;
        _ = ShowPreviewAsync(_preview, pane, item, () => PreviewOverlay.Visibility == Visibility.Visible && pane == Active);
    }

    /// <summary>Debounced: arrowing through a list only renders the item you stop on.</summary>
    private void SchedulePreviewPane()
    {
        if (!Services.Settings.Preview.PaneVisible) return;
        if (_previewPaneTimer is null)
        {
            _previewPaneTimer = DispatcherQueue.CreateTimer();
            _previewPaneTimer.Interval = TimeSpan.FromMilliseconds(120);
            _previewPaneTimer.IsRepeating = false;
            _previewPaneTimer.Tick += (_, _) => UpdatePreviewPane();
        }

        _previewPaneTimer.Stop();
        _previewPaneTimer.Start();
    }

    private void UpdatePreviewPane()
    {
        var item = Active.SelectedItems.Count == 1 ? Active.SelectedItems[0] : null;
        if (item is null)
        {
            _previewPanePath = null;
            PreviewPaneContent.Children.Clear();
            PreviewPaneTitle.Text = Active.SelectedItems.Count > 1 ? $"{Active.SelectedItems.Count} items" : "Preview";
            PreviewPaneMeta.Text = Active.SelectedItems.Count > 1 ? Format.Size(Active.SelectedItems.Where(i => !i.IsDirectory).Sum(i => i.Entry.Size)) : "select a file";
            return;
        }

        if (string.Equals(item.Path, _previewPanePath, StringComparison.OrdinalIgnoreCase)) return;
        _previewPanePath = item.Path;
        if (_previewPane is null)
        {
            _previewPane = new PreviewController(PreviewPaneContent, PreviewPaneTitle, PreviewPaneMeta) { Compact = true };
            _previewPane.ForwardedKey += _ => Active.FocusList();
        }

        _previewPane.MaxText = Services.Settings.Preview.MaxTextMegabytes * 1024L * 1024;
        var path = item.Path;
        _ = ShowPreviewAsync(_previewPane, Active, item, () => string.Equals(_previewPanePath, path, StringComparison.OrdinalIgnoreCase));
    }

    private void ClosePreview()
    {
        _preview?.Unload();
        PreviewOverlay.Visibility = Visibility.Collapsed;
        Active.FocusList();
    }

    private bool HandlePreviewKey(Windows.System.VirtualKey key)
    {
        switch (key)
        {
            case Windows.System.VirtualKey.Escape or Windows.System.VirtualKey.Space:
                ClosePreview();
                return true;
            case Windows.System.VirtualKey.Down or Windows.System.VirtualKey.Up:
                Active.MoveSelection(key == Windows.System.VirtualKey.Down ? 1 : -1);
                if (Active.SelectedItems.FirstOrDefault() is { } next && _preview is { } quickLook)
                {
                    var pane = Active;
                    _ = ShowPreviewAsync(quickLook, pane, next, () => PreviewOverlay.Visibility == Visibility.Visible && pane.SelectedItems.FirstOrDefault() == next);
                }
                return true;
            case Windows.System.VirtualKey.Enter:
                ClosePreview();
                Active.OpenSelected();
                return true;
            default:
                return false;
        }
    }

    // ---------------- workspaces ----------------

    private void SaveWorkspace()
    {
        if (_workspace is null) return;
        _workspace.Window = ToSession();
        _workspace.LastUsed = DateTime.Now;
        Services.Workspaces.Save(_workspace);
    }

    private void SwitchWorkspace(Core.Workspaces.Workspace target)
    {
        SaveWorkspace();
        _workspace = target;
        var s = target.Window;
        _dual = s.DualPane;
        _sidebar = s.SidebarVisible;
        Pane1.LoadSession(s.Panes.ElementAtOrDefault(0) ?? new PaneSession());
        Pane2.LoadSession(s.Panes.ElementAtOrDefault(1) ?? new PaneSession());
        ApplyLayout();
        SetActivePane(Math.Clamp(s.ActivePane, 0, _dual ? 1 : 0));
        target.LastUsed = DateTime.Now;
        Services.Workspaces.Save(target);
        ShowToast("Workspace: " + target.Name);
    }

    private async Task SaveWorkspaceAsAsync()
    {
        var name = await PromptAsync("Save window as workspace", _workspace?.Name ?? (Active.Folder is { } f ? WinPath.Name(f) : "Workspace"));
        if (string.IsNullOrWhiteSpace(name)) return;
        _workspace = Services.Workspaces.Get(name) ?? new Core.Workspaces.Workspace { Name = name.Trim() };
        SaveWorkspace();
        OnPaneStateChanged(Active);
        RenderSidebar();
        ShowToast("Workspace saved: " + _workspace.Name);
    }

    private async Task RenameWorkspaceAsync()
    {
        if (_workspace is null) return;
        var name = await PromptAsync("Rename workspace", _workspace.Name);
        if (string.IsNullOrWhiteSpace(name) || name == _workspace.Name) return;
        Services.Workspaces.Rename(_workspace.Name, name.Trim());
        _workspace = Services.Workspaces.Get(name.Trim());
        OnPaneStateChanged(Active);
        RenderSidebar();
    }

    private async Task DeleteWorkspaceAsync()
    {
        if (_workspace is null) return;
        var dialog = new ContentDialog
        {
            Title = $"Delete workspace \"{_workspace.Name}\"?",
            Content = "The window keeps its tabs. Only the saved workspace is removed.",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            XamlRoot = Content.XamlRoot,
            RequestedTheme = Theme.Current,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        Services.Workspaces.Delete(_workspace.Name);
        _workspace = null;
        OnPaneStateChanged(Active);
        RenderSidebar();
    }

    private List<PaletteItem> WorkspaceItems()
    {
        var items = Services.Workspaces.List()
            .Select(w => new PaletteItem("⧉ " + w.Name + (w.Name == _workspace?.Name ? "  (current)" : ""), string.Join("  |  ", w.Window.Panes.Take(w.Window.DualPane ? 2 : 1).Select(p => string.Join(", ", p.Tabs.Select(t => t.Location == WinPath.Home ? "home" : WinPath.Name(t.Location))))), Format.Relative(w.LastUsed, DateTime.Now), () => SwitchWorkspace(w)))
            .ToList();
        items.Add(new PaletteItem("+ Save this window as new workspace…", "", "", () => _ = SaveWorkspaceAsAsync()));
        return items;
    }

    private IReadOnlyList<string> TargetPaths()
    {
        // Shell items as their parsing names, which Explorer's address bar and Win+R understand.
        var sel = Active.SelectedPaths;
        var shown = Active.Tab.IsHome || Active.Tab.IsRecycleBin ? null : Active.Tab.Location;
        return sel.Count > 0 ? [.. sel.Select(Locations.ParsingName)] : shown is not null ? [Locations.ParsingName(shown)] : [];
    }

    private void AddBookmark(string? path) => AddBookmark(path, null, null);

    /// <summary>Adds a Bookmark (Copilot's pollux_add_bookmark can name it and put it in a group).</summary>
    private void AddBookmark(string? path, string? name, string? group)
    {
        if (path is null) return;
        Services.Bookmarks.Add(path, string.IsNullOrWhiteSpace(name) ? WinPath.Name(path) : name, group ?? "");
        Services.PlacesUpdated();
        var n = Services.Bookmarks.Flat.ToList().FindIndex(b => string.Equals(b.Path, path, StringComparison.OrdinalIgnoreCase)) + 1;
        ShowToast(n is > 0 and <= 9 ? $"Bookmarked {WinPath.Name(path)} — Ctrl+Shift+{n}" : $"Bookmarked {WinPath.Name(path)}");
    }

    // ---------------- file operations ----------------

    private void Delete(bool permanent)
    {
        var paths = Active.SelectedPaths;
        if (paths.Count == 0) return;
        var confirm = permanent ? Services.Settings.Files.ConfirmPermanentDelete : Services.Settings.Files.ConfirmDelete;
        var index = Active.VisibleItems.ToList().FindIndex(i => i.Path == paths[0]);
        var since = DateTime.UtcNow;
        if (Shell.Delete(Hwnd, paths, permanent, confirm))
        {
            if (!permanent) RefreshRecycleBins();
            var next = Active.VisibleItems.Where(i => !paths.Contains(i.Path)).ElementAtOrDefault(Math.Max(0, index));
            if (next is not null) Active.SelectByName(next.Entry.Name);
            else Active.Refresh();
        }

        // Also after a partial delete; a permanent delete cannot be undone and is never recorded.
        if (!permanent) _app.Undo.Record(Core.Operations.UndoEntry.Recycle(paths, since, FileUndo.Target(Hwnd)));
    }

    /// <summary>Undoes (or redoes) the newest file operation of any window; not while the queue still copies or moves.</summary>
    private void RunUndo(bool redo)
    {
        if (_app.Transfers.IsBusy)
        {
            ShowToast("Wait until the running copy or move has finished");
            return;
        }

        var target = FileUndo.Target(Hwnd);
        var result = redo ? _app.Undo.Redo(target) : _app.Undo.Undo(target);
        if (result.Ok)
        {
            foreach (var p in _panes) p.Refresh();
        }

        ShowToast(result.Message, result.Ok ? 3 : 5);
    }

    private CancellationTokenSource? _archiveCts;

    /// <summary>Zips the selection or extracts the selected .zip files on a worker thread, with a toast for progress. One archive job at a time.</summary>
    private void StartArchive(bool compress, bool here)
    {
        var pane = Active;
        if (pane.Folder is not { } folder) return;
        var paths = pane.SelectedPaths;
        if (!compress) paths = paths.Where(Pollux.Core.Archives.ZipPath.IsZipFile).ToList();
        if (paths.Count == 0)
        {
            ShowToast(compress ? "Select something to compress" : "Select a .zip file to extract");
            return;
        }

        if (_archiveCts is not null)
        {
            ShowToast("Another zip job is still running (Cancel Zip Compress/Extract in the palette)");
            return;
        }

        var cts = _archiveCts = new CancellationTokenSource();
        var verb = compress ? "Compressing" : "Extracting";
        ShowToast(verb + "…", 60);
        var progress = new Progress<Pollux.Core.Archives.ZipProgress>(p =>
        {
            if (!_closed) ShowToast($"{verb} {p.DoneFiles}/{p.TotalFiles} {p.CurrentFile} (cancel: palette)", 60);
        });
        _ = Task.Run(() =>
        {
            string? selectName = null;
            var skipped = 0;
            if (compress)
            {
                selectName = Path.GetFileName(Pollux.Core.Archives.ZipArchiver.Compress(paths, folder, progress, cts.Token));
            }
            else
            {
                foreach (var zip in paths)
                {
                    var target = Pollux.Core.Archives.ZipArchiver.ExtractTargetFor(zip, here);
                    // A breach of the zip bomb limits asks first (S-19); refusing throws, which ends in the error toast.
                    var result = Pollux.Core.Archives.ZipArchiver.Extract(zip, target, progress,
                        confirm: breach => AskExtractLimitAsync(breach).GetAwaiter().GetResult(), ct: cts.Token);
                    skipped += result.SkippedUnsafe + result.SkippedExisting;
                    if (!here && selectName is null) selectName = Path.GetFileName(target);
                }
            }

            return (selectName, skipped);
        }, cts.Token).ContinueWith(t => DispatcherQueue.TryEnqueue(() =>
        {
            _archiveCts = null;
            cts.Dispose();
            if (_closed) return;
            if (t.IsCanceled || t.Exception?.GetBaseException() is OperationCanceledException) ShowToast("Zip cancelled");
            else if (t.Exception?.GetBaseException() is { } ex) ShowToast(ex.Message, 6);
            else
            {
                var (name, skippedCount) = t.Result;
                ShowToast((compress ? "Created " : "Extracted ") + name + (skippedCount > 0 ? $" ({skippedCount} skipped)" : ""), skippedCount > 0 ? 6 : 3);
                if (pane.Folder == folder) { pane.Refresh(); }
            }
        }));
    }

    /// <summary>
    /// Asks on the UI thread whether to extract an archive that breaks the size or ratio limits. Called from the
    /// archive worker thread, which waits for the answer; Cancel is the default, and a closed window answers no.
    /// </summary>
    private Task<bool> AskExtractLimitAsync(Pollux.Core.Archives.ExtractLimitBreach breach)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (_closed)
        {
            tcs.TrySetResult(false);
            return tcs.Task;
        }

        var queued = DispatcherQueue.TryEnqueue(async () =>
        {
            await _dialogGate.WaitAsync();
            try
            {
                if (_closed)
                {
                    tcs.TrySetResult(false);
                    return;
                }

                var dialog = new ContentDialog
                {
                    Title = "Extract " + Path.GetFileName(breach.ZipPath) + "?",
                    Content = new TextBlock { Text = breach.Describe(), TextWrapping = TextWrapping.Wrap },
                    PrimaryButtonText = "Extract anyway",
                    CloseButtonText = "Cancel",
                    DefaultButton = ContentDialogButton.Close,
                    XamlRoot = Content.XamlRoot,
                    RequestedTheme = Theme.Current,
                };
                tcs.TrySetResult(await dialog.ShowAsync() == ContentDialogResult.Primary);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
            {
                tcs.TrySetResult(false);
            }
            finally
            {
                _dialogGate.Release();
            }
        });
        if (!queued) tcs.TrySetResult(false);
        return tcs.Task;
    }

    private void TransferToOther(bool move)
    {
        var paths = Active.SelectedPaths;
        if (paths.Count == 0 || Other.Folder is not { } target)
        {
            ShowToast(_dual ? "Other pane shows no folder" : "Dual pane is off (Ctrl+<)");
            return;
        }

        Transfer(move, paths, target);
    }

    private async Task SetClipboardAsync(bool move)
    {
        var paths = Active.SelectedPaths;
        if (paths.Count == 0) return;
        var data = new DataPackage { RequestedOperation = move ? DataPackageOperation.Move : DataPackageOperation.Copy };
        data.SetStorageItems(await PaneView.StorageItemsFor(paths));
        data.SetText(string.Join(Environment.NewLine, paths));
        Clipboard.SetContent(data);
        Clipboard.Flush();
        ShowToast($"{(move ? "Cut" : "Copied")} {paths.Count} item(s)");
    }

    private async Task PasteAsync()
    {
        if (Active.Folder is not { } target) return;
        var content = Clipboard.GetContent();
        var paths = new List<string>();
        if (content.Contains(StandardDataFormats.StorageItems))
        {
            var items = await content.GetStorageItemsAsync();
            paths = items.Select(i => i.Path).Where(x => !string.IsNullOrEmpty(x)).ToList();
        }

        if (paths.Count == 0)
        {
            if (Services.Settings.Clipboard.PasteAsFile) await PasteClipAsFileAsync(content, target);
            return;
        }

        var move = content.RequestedOperation.HasFlag(DataPackageOperation.Move);
        Transfer(move, paths, target);
        if (move) Clipboard.Clear();
    }

    /// <summary>Ctrl+V with text or an image (and no files) on the clipboard: creates a .txt or .png in the folder.</summary>
    private async Task PasteClipAsFileAsync(DataPackageView content, string target)
    {
        try
        {
            string name;
            if (content.Contains(StandardDataFormats.Text))
            {
                var text = await content.GetTextAsync();
                if (string.IsNullOrWhiteSpace(text)) return;
                var now = DateTime.Now;
                name = await CreateNewFileAsync(target, exists => Pollux.Core.Clipboard.ClipFileName.ForText(text, now, exists), System.Text.Encoding.UTF8.GetBytes(text));
            }
            else if (content.Contains(StandardDataFormats.Bitmap))
            {
                var png = await ClipboardImage.ToPngAsync(content);
                var now = DateTime.Now;
                name = await CreateNewFileAsync(target, exists => Pollux.Core.Clipboard.ClipFileName.ForImage(now, exists), png);
            }
            else
            {
                return;
            }

            _app.Undo.Record(Core.Operations.UndoEntry.NewFile(Path.Combine(target, name), FileUndo.Target(Hwnd)));
            Active.SelectByName(name);
            ShowToast("Created " + name);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            ShowToast(ex.Message);
        }
    }

    /// <summary>
    /// Writes <paramref name="bytes"/> to a new file named by <paramref name="pickName"/> (given an "exists" check) and returns
    /// the name. Never overwrites: the file is created with <see cref="FileMode.CreateNew"/>, and when another process created
    /// that name in the meantime, the next keep-both name is tried.
    /// </summary>
    private static async Task<string> CreateNewFileAsync(string target, Func<Func<string, bool>, string> pickName, byte[] bytes)
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool Exists(string n) => taken.Contains(n) || File.Exists(Path.Combine(target, n)) || Directory.Exists(Path.Combine(target, n));
        for (var attempt = 0; ; attempt++)
        {
            var name = pickName(Exists);
            var path = Path.Combine(target, name);
            try
            {
                await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, useAsync: true);
                await stream.WriteAsync(bytes);
                return name;
            }
            catch (IOException) when (attempt < 10 && (File.Exists(path) || Directory.Exists(path)))
            {
                taken.Add(name);
            }
        }
    }

    private async Task SelectByPatternAsync(PaneView p, bool select)
    {
        var patterns = await PromptAsync(select ? "Select by pattern" : "Deselect by pattern", "*");
        if (!string.IsNullOrWhiteSpace(patterns)) p.SelectByPattern(patterns, select);
    }

    private async Task RenameAsync()
    {
        var sel = Active.SelectedItems;
        if (sel.Count != 1) return;
        var item = sel[0];
        var name = await PromptAsync("Rename", item.Entry.Name, selectStem: !item.IsDirectory);
        if (string.IsNullOrWhiteSpace(name) || name == item.Entry.Name) return;
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            ShowToast("Invalid file name");
            return;
        }

        if (Shell.Rename(Hwnd, item.Path, name))
        {
            _app.Undo.Record(Core.Operations.UndoEntry.Rename(item.Path, Path.Combine(Path.GetDirectoryName(item.Path)!, name)));
            Active.SelectByName(name);
        }
    }

    private async Task BatchRenameAsync()
    {
        var sel = Active.SelectedItems;
        if (sel.Count < 2 || Active.Folder is not { } dir) return;
        if (Active.HasSearchResults)
        {
            // Result names are bare, but the results live in different folders: the folder-based plan would hit other files.
            ShowToast("Batch rename is not available for search results");
            return;
        }

        string PathOf(string name) => Path.Combine(dir, name);
        bool Exists(string name) => File.Exists(PathOf(name)) || Directory.Exists(PathOf(name));
        var plan = await BatchRenameDialog.ShowAsync(Content.XamlRoot, [.. sel.Select(i => new Core.Files.RenameSource(i.Entry.Name, i.IsDirectory))], Exists);
        Active.FocusList();
        if (plan is null) return;

        var done = new List<Core.Files.RenameStep>();
        try
        {
            foreach (var step in plan.Steps(Exists))
            {
                if (Directory.Exists(PathOf(step.From))) Directory.Move(PathOf(step.From), PathOf(step.To));
                else File.Move(PathOf(step.From), PathOf(step.To));
                done.Add(step);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Undo what already happened so a half-renamed batch never stays behind.
            foreach (var step in Enumerable.Reverse(done))
            {
                try
                {
                    if (Directory.Exists(PathOf(step.To))) Directory.Move(PathOf(step.To), PathOf(step.From));
                    else File.Move(PathOf(step.To), PathOf(step.From));
                }
                catch (Exception inner) when (inner is IOException or UnauthorizedAccessException) { }
            }

            ShowToast("Rename failed: " + ex.Message + " (changes were rolled back)", 6);
            Active.Refresh();
            return;
        }

        var renamed = plan.Items.Count(i => i.IsChanged);
        _app.Undo.Record(Core.Operations.UndoEntry.BatchRename(dir, done, renamed));
        Active.Refresh();
        ShowToast($"Renamed {renamed} items");
    }

    private async Task NewItemAsync(bool folder)
    {
        if (Active.Folder is not { } dir) return;
        var suggested = folder ? Shell.UniqueName(dir, "New folder") : Shell.UniqueName(dir, "new", ".txt");
        var name = await PromptAsync(folder ? "New folder" : "New file", suggested, selectStem: !folder);
        if (string.IsNullOrWhiteSpace(name)) return;
        try
        {
            var path = Path.Combine(dir, name);
            if (folder)
            {
                var existed = Directory.Exists(path);
                Directory.CreateDirectory(path);
                if (!existed) _app.Undo.Record(Core.Operations.UndoEntry.NewFolder(path));
            }
            else if (!File.Exists(path))
            {
                await File.WriteAllTextAsync(path, "");
                _app.Undo.Record(Core.Operations.UndoEntry.NewFile(path, FileUndo.Target(Hwnd)));
            }

            Active.SelectByName(name);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowToast(ex.Message);
        }
    }

    /// <summary>Creates a shortcut, symlink or junction for the selection. Symlinks and junctions go to the other pane when dual pane is on.</summary>
    private async Task CreateLinkAsync(LinkKind kind, bool inOther)
    {
        var paths = Active.SelectedPaths;
        if (paths.Count == 0)
        {
            ShowToast("Nothing selected");
            return;
        }

        var useOther = inOther && (_dual || kind == LinkKind.Shortcut);
        if (inOther && !_dual && kind == LinkKind.Shortcut)
        {
            ShowToast("Dual pane is off (Ctrl+<)");
            return;
        }

        var pane = useOther ? Other : Active;
        if (pane.Folder is not { } dir)
        {
            ShowToast("Target pane shows no folder");
            return;
        }

        string? lastName = null;
        foreach (var source in paths)
        {
            var isDir = Directory.Exists(source);
            if (Links.Validate(kind, source, isDir, dir) is { } problem)
            {
                ShowToast(problem);
                return;
            }

            var name = Links.SuggestName(kind, Path.GetFileName(source.TrimEnd('\\')), isDir, n => File.Exists(Path.Combine(dir, n)) || Directory.Exists(Path.Combine(dir, n)));
            if (paths.Count == 1)
            {
                name = await PromptAsync(kind switch { LinkKind.Shortcut => "Create shortcut", LinkKind.Symlink => "Create symbolic link", _ => "Create junction" }, name, selectStem: true);
                if (string.IsNullOrWhiteSpace(name)) return;
                if (Links.ValidateName(name) is { } bad)
                {
                    ShowToast(bad);
                    return;
                }

                if (File.Exists(Path.Combine(dir, name)) || Directory.Exists(Path.Combine(dir, name)))
                {
                    ShowToast("Already exists: " + name);
                    return;
                }
            }

            if (LinkTools.Create(kind, source, isDir, Path.Combine(dir, name)) is { } error)
            {
                ShowToast(error);
                return;
            }

            lastName = name;
        }

        if (lastName is not null)
        {
            pane.Refresh();
            if (pane == Active) pane.SelectByName(lastName);
        }
    }

    public void CreateShortcuts(IReadOnlyList<string> sources, string targetDirectory)
    {
        foreach (var source in sources)
        {
            var isDir = Directory.Exists(source);
            // A drive root ("C:\") has no file name; its letter stands in, without the colon.
            var name = Links.SuggestName(LinkKind.Shortcut, WinPath.Name(source).Replace(":", "", StringComparison.Ordinal), isDir,
                n => File.Exists(Path.Combine(targetDirectory, n)) || Directory.Exists(Path.Combine(targetDirectory, n)));
            if (LinkTools.Create(LinkKind.Shortcut, source, isDir, Path.Combine(targetDirectory, name)) is { } error)
            {
                ShowToast(error);
                break;
            }
        }

        foreach (var p in _panes)
        {
            if (string.Equals(p.Folder, WinPath.Normalize(targetDirectory), StringComparison.OrdinalIgnoreCase)) p.Refresh();
        }
    }

    private void CopyText(string text)
    {
        if (text.Length == 0) return;
        var data = new DataPackage();
        data.SetText(text);
        Clipboard.SetContent(data);
        Clipboard.Flush();
        ShowToast("Copied: " + (text.Length > 80 ? text[..80] + "…" : text));
    }

    public async Task<string?> PromptAsync(string title, string initial, bool selectStem = false)
    {
        var box = new TextBox { Text = initial, FontFamily = Mono, MinWidth = 360 };
        var dialog = new ContentDialog
        {
            Title = title,
            Content = box,
            PrimaryButtonText = "OK",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot,
            RequestedTheme = Theme.Current,
        };
        box.Loaded += (_, _) =>
        {
            box.Focus(FocusState.Keyboard);
            var dot = initial.LastIndexOf('.');
            if (selectStem && dot > 0) box.Select(0, dot);
            else box.SelectAll();
        };
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                e.Handled = true;
                dialog.Hide();
                box.Tag = "ok";
            }
        };
        var result = await dialog.ShowAsync();
        Active.FocusList();
        return result == ContentDialogResult.Primary || (string?)box.Tag == "ok" ? box.Text.Trim() : null;
    }

    /// <summary>Shows why a tool did not start (it names the tool); nothing when it did.</summary>
    private void ShowLaunchError(string? error)
    {
        if (error is not null) ShowToast(error, 6);
    }

    public void ShowToast(string text, int seconds = 3)
    {
        ToastText.Text = text;
        Toast.Visibility = Visibility.Visible;
        if (_toastTimer is null)
        {
            _toastTimer = DispatcherQueue.CreateTimer();
            _toastTimer.IsRepeating = false;
            _toastTimer.Tick += (_, _) => Toast.Visibility = Visibility.Collapsed;
        }

        _toastTimer.Stop();
        _toastTimer.Interval = TimeSpan.FromSeconds(seconds);
        _toastTimer.Start();
    }

    // ---------------- palette ----------------

    private void ShowPalette(PaletteMode mode)
    {
        _paletteMode = mode;
        _paletteSource = mode switch { PaletteMode.Commands => CommandItems(), PaletteMode.Workspaces => WorkspaceItems(), PaletteMode.Templates => _templateItems, _ => GoItems() };
        PaletteBox.PlaceholderText = mode switch { PaletteMode.Commands => "> type a command", PaletteMode.Workspaces => "switch workspace…", PaletteMode.Templates => "new file from template…", _ => "go to bookmark, recent, file or folder…" };
        PaletteBox.Text = "";
        PaletteOverlay.Visibility = Visibility.Visible;
        FilterPalette();
        PaletteBox.Focus(FocusState.Keyboard);
    }

    private void HidePalette(bool refocus = true)
    {
        _searchCts?.Cancel();
        PaletteOverlay.Visibility = Visibility.Collapsed;
        if (refocus) Active.FocusList();
    }

    private List<PaletteItem> CommandItems()
    {
        var recent = _app.RecentCommands;
        return recent.Palette(CommandRegistry.Commands(Services.Settings.Copilot, _app.CopilotAvailable))
            .Select(c => new PaletteItem(c.Title, "", Services.Keys.KeyFor(c.Id)?.ToString() ?? "", () => { recent.Note(c.Id); Execute(c.Id); }))
            .ToList();
    }

    private List<PaletteItem> GoItems()
    {
        var items = new List<PaletteItem>();
        var n = 0;
        foreach (var b in Services.Bookmarks.Flat)
        {
            n++;
            var path = b.Path;
            items.Add(new PaletteItem("★ " + b.Name, path, n <= 9 ? $"Ctrl+Shift+{n}" : "", () => Active.Navigate(path)));
        }

        foreach (var r in Services.Recent.Ranked(DateTime.Now).Take(30))
        {
            var path = r.Path;
            if (!Services.Bookmarks.Contains(path)) items.Add(new PaletteItem("↺ " + WinPath.Name(path), path, "", () => Active.Navigate(path)));
        }

        return items;
    }

    private void FilterPalette()
    {
        var q = PaletteBox.Text.Trim();
        if (_paletteMode == PaletteMode.Commands && q.StartsWith('>')) q = q[1..].Trim();
        var matches = _paletteSource
            .Select(i => (Item: i, M: FilterMatcher.Match(i.Title.TrimStart('★', '↺', '⧉', '+', ' '), q), D: FilterMatcher.Match(i.Detail, q)))
            .Where(x => x.M.IsMatch || (q.Length > 0 && x.D.IsMatch && x.Item.Detail.Length > 0))
            .Select(x => x.Item)
            .Take(200)
            .ToList();

        if (_paletteMode == PaletteMode.Go && q.Length >= 2)
        {
            _ = SearchFilesAsync(q, matches);
        }

        PaletteList.ItemsSource = matches;
        PaletteList.SelectedIndex = matches.Count > 0 ? 0 : -1;
        PaletteFooter.Text = _paletteMode switch { PaletteMode.Commands => $"{matches.Count} commands · Enter run · Esc close", PaletteMode.Workspaces => $"{matches.Count} workspaces · Enter switch · Esc close", PaletteMode.Templates => $"{matches.Count} templates · Enter create · Esc close", _ => $"{matches.Count} results · Enter open · Ctrl+Enter open in other pane" };
    }

    private async Task SearchFilesAsync(string query, List<PaletteItem> baseItems)
    {
        _searchCts?.Cancel();
        var cts = _searchCts = new CancellationTokenSource();
        var hits = await _app.Search.FindAsync(query, Services.Settings.Search.MaxResults, cts.Token);
        if (cts.IsCancellationRequested || PaletteOverlay.Visibility != Visibility.Visible) return;
        var combined = baseItems.ToList();
        foreach (var h in hits)
        {
            var path = h.Path;
            var isDir = h.IsDirectory;
            combined.Add(new PaletteItem((isDir ? "▸ " : "  ") + h.Name, path, "", () =>
            {
                if (isDir) Active.Navigate(path);
                else if (Path.GetDirectoryName(path) is { } dir) Active.Navigate(dir, Path.GetFileName(path));
            }));
        }

        PaletteList.ItemsSource = combined;
        PaletteList.SelectedIndex = combined.Count > 0 ? 0 : -1;
        PaletteFooter.Text = $"{combined.Count} results · {_app.Search.StatusText} · Enter open · Shift+Enter launch file";
    }

    private void PaletteBox_TextChanged(object sender, TextChangedEventArgs e) => FilterPalette();

    private void PaletteBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        var count = (PaletteList.ItemsSource as IList<PaletteItem>)?.Count ?? 0;
        switch (e.Key)
        {
            case Windows.System.VirtualKey.Escape:
                e.Handled = true;
                HidePalette();
                break;
            case Windows.System.VirtualKey.Down:
                e.Handled = true;
                if (count > 0) PaletteList.SelectedIndex = (PaletteList.SelectedIndex + 1) % count;
                PaletteList.ScrollIntoView(PaletteList.SelectedItem);
                break;
            case Windows.System.VirtualKey.Up:
                e.Handled = true;
                if (count > 0) PaletteList.SelectedIndex = (PaletteList.SelectedIndex - 1 + count) % count;
                PaletteList.ScrollIntoView(PaletteList.SelectedItem);
                break;
            case Windows.System.VirtualKey.Enter:
                e.Handled = true;
                if (PaletteList.SelectedItem is PaletteItem item)
                {
                    var ctrl = KeyInput.IsDown(Windows.System.VirtualKey.Control);
                    var shift = KeyInput.IsDown(Windows.System.VirtualKey.Shift);
                    HidePalette();
                    if (_paletteMode == PaletteMode.Go && ctrl)
                    {
                        var target = Other;
                        if (!_dual) { _dual = true; ApplyLayout(); }
                        SetActivePane(target.Index);
                    }

                    if (_paletteMode == PaletteMode.Go && shift && File.Exists(item.Detail)) Shell.Open(item.Detail);
                    else item.Run();
                }

                break;
        }
    }

    private void PaletteList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is PaletteItem item)
        {
            HidePalette();
            item.Run();
        }
    }

    private void PaletteOverlay_Tapped(object sender, TappedRoutedEventArgs e) => HidePalette();

    private void PaletteCard_Tapped(object sender, TappedRoutedEventArgs e) => e.Handled = true;
}

/// <summary>Small repeating timer on the UI thread that can be started/stopped cheaply.</summary>
internal sealed class DispatcherTimerLite
{
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _timer;

    public DispatcherTimerLite(Microsoft.UI.Dispatching.DispatcherQueue queue, TimeSpan interval, Action tick)
    {
        _timer = queue.CreateTimer();
        _timer.Interval = interval;
        _timer.IsRepeating = true;
        _timer.Tick += (_, _) => tick();
    }

    public void Start() { if (!_timer.IsRunning) _timer.Start(); }
    public void Stop() => _timer.Stop();
    public void SetInterval(TimeSpan interval) => _timer.Interval = interval;
}

internal static class KnownFolders
{
    public static string Profile => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    public static string Downloads => Path.Combine(Profile, "Downloads");
    public static string Desktop => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
    public static string Documents => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
}
