using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Pollux.App.Services;
using Pollux.Core.Commands;
using Pollux.Core.Settings;

namespace Pollux.App.Views;

/// <summary>
/// Settings page generated from a list of setting descriptors. Edits settings.json directly;
/// the JSON file stays the source of truth (comments in it are not preserved when saving from here).
/// </summary>
internal sealed partial class SettingsWindow : Window
{
    private readonly App _app;
    private readonly Grid _root = new();
    private readonly StackPanel _content = new() { Spacing = 4, Padding = new Thickness(28, 16, 28, 40), MaxWidth = 860, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly ListView _nav = new() { SelectionMode = ListViewSelectionMode.Single, Width = 200, Padding = new Thickness(6, 8, 6, 8) };
    private readonly TextBox _search = new() { PlaceholderText = "Search settings", Margin = new Thickness(12, 8, 12, 4) };
    private List<Setting> _all = [];

    private sealed record Setting(string Section, string Key, string Label, string Help, Func<FrameworkElement> Editor);

    public SettingsWindow(App app)
    {
        _app = app;
        Title = "Pollux Settings";
        ExtendsContentIntoTitleBar = true;
        SystemBackdrop = new MicaBackdrop();
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "pollux.ico"));
        var scale = Interop.Native.GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
        AppWindow.Resize(new Windows.Graphics.SizeInt32((int)(1040 * scale), (int)(760 * scale)));

        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(34) });
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var title = new TextBlock { Text = "pollux_ settings", FontFamily = Mono, FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0) };
        Grid.SetColumnSpan(title, 2);
        _root.Children.Add(title);
        SetTitleBar(title);

        var left = new StackPanel();
        left.Children.Add(_search);
        left.Children.Add(_nav);
        Grid.SetRow(left, 1);
        _root.Children.Add(left);

        var scroll = new ScrollViewer { Content = _content };
        Grid.SetRow(scroll, 1);
        Grid.SetColumn(scroll, 1);
        _root.Children.Add(scroll);
        Content = _root;

        _search.TextChanged += (_, _) => Render();
        _nav.SelectionChanged += (_, _) => Render();
        _root.RequestedTheme = Theme.Current;
        app.Services.SettingsChanged += OnExternalChange;
        Closed += (_, _) => app.Services.SettingsChanged -= OnExternalChange;

        Build();
        _nav.ItemsSource = _all.Select(s => s.Section).Distinct().Append("Keyboard").ToList();
        _nav.SelectedIndex = 0;
    }

    private bool _saving;

    private void OnExternalChange()
    {
        _root.RequestedTheme = Theme.Current;
        if (!_saving) Render();
    }

    private PolluxSettings S => _app.Services.Settings;

    private static FontFamily Mono => Ui.Mono;

    private void Save()
    {
        _saving = true;
        try { _app.Services.SaveSettings(); }
        finally { _saving = false; }
    }

    private void Render()
    {
        _content.Children.Clear();
        var q = _search.Text.Trim();
        var section = _nav.SelectedItem as string ?? "Appearance";
        if (q.Length == 0 && section == "Keyboard")
        {
            RenderKeyboard();
            return;
        }

        var items = q.Length > 0
            ? _all.Where(s => s.Label.Contains(q, StringComparison.OrdinalIgnoreCase) || s.Key.Contains(q, StringComparison.OrdinalIgnoreCase) || s.Help.Contains(q, StringComparison.OrdinalIgnoreCase))
            : _all.Where(s => s.Section == section);

        _content.Children.Add(new TextBlock { Text = q.Length > 0 ? $"Results for \"{q}\"" : section, FontSize = 24, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 10) });
        foreach (var s in items) _content.Children.Add(Row(s));

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 24, 0, 0) };
        buttons.Children.Add(LinkButton("Open settings.json", () => Shell.Open(_app.Services.SettingsFile)));
        buttons.Children.Add(LinkButton("Open config folder", () => Shell.Open(_app.Services.Paths.ConfigDirectory)));
        buttons.Children.Add(LinkButton("Reset all settings", async () =>
        {
            var dialog = new ContentDialog { Title = "Reset all settings to defaults?", PrimaryButtonText = "Reset", CloseButtonText = "Cancel", XamlRoot = _root.XamlRoot, RequestedTheme = Theme.Current };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                SettingsStore.Save(_app.Services.SettingsFile, new PolluxSettings());
                _app.Services.ReloadConfig();
                Build();
                Render();
            }
        }));
        _content.Children.Add(buttons);
    }

    private static Button LinkButton(string text, Action click)
    {
        var b = new Button { Content = text };
        b.Click += (_, _) => click();
        return b;
    }

    private static FrameworkElement Row(Setting s)
    {
        var grid = new Grid { ColumnSpacing = 24, Padding = new Thickness(16, 12, 16, 12), CornerRadius = new CornerRadius(6), Background = Theme.Brush("PolluxSurfaceBrush"), Margin = new Thickness(0, 0, 0, 2) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = s.Label, FontSize = 14, TextWrapping = TextWrapping.Wrap });
        if (s.Help.Length > 0) text.Children.Add(new TextBlock { Text = s.Help, FontSize = 12, Foreground = Theme.Brush("PolluxMutedBrush"), TextWrapping = TextWrapping.Wrap });
        text.Children.Add(new TextBlock { Text = s.Key, FontFamily = Mono, FontSize = 10.5, Foreground = Theme.Brush("PolluxMutedBrush"), IsTextSelectionEnabled = true });
        grid.Children.Add(text);
        var editor = s.Editor();
        editor.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(editor, 1);
        grid.Children.Add(editor);
        return grid;
    }

    // ---------------- editors ----------------

    private Func<FrameworkElement> Bool(Func<bool> get, Action<bool> set) => () =>
    {
        var t = new ToggleSwitch { IsOn = get(), OnContent = "On", OffContent = "Off", MinWidth = 0 };
        t.Toggled += (_, _) => { if (t.IsOn != get()) { set(t.IsOn); Save(); } };
        return t;
    };

    private Func<FrameworkElement> Choice<T>(Func<T> get, Action<T> set) where T : struct, Enum => () =>
    {
        var c = new ComboBox { ItemsSource = Enum.GetNames<T>(), SelectedItem = get().ToString(), MinWidth = 160 };
        c.SelectionChanged += (_, _) =>
        {
            if (c.SelectedItem is string v && Enum.TryParse<T>(v, out var parsed) && !parsed.Equals(get())) { set(parsed); Save(); }
        };
        return c;
    };

    private Func<FrameworkElement> Options(string[] options, Func<string> get, Action<string> set) => () =>
    {
        var c = new ComboBox { ItemsSource = options, SelectedItem = options.FirstOrDefault(o => o.Equals(get(), StringComparison.OrdinalIgnoreCase)) ?? options[0], MinWidth = 160 };
        c.SelectionChanged += (_, _) => { if (c.SelectedItem is string v && v != get()) { set(v); Save(); } };
        return c;
    };

    private Func<FrameworkElement> Number(Func<double> get, Action<double> set, double min, double max, double step = 1) => () =>
    {
        var n = new NumberBox { Value = get(), Minimum = min, Maximum = max, SmallChange = step, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline, MinWidth = 140 };
        n.ValueChanged += (_, e) => { if (!double.IsNaN(e.NewValue) && e.NewValue != get()) { set(e.NewValue); Save(); } };
        return n;
    };

    private Func<FrameworkElement> Text(Func<string> get, Action<string> set, string placeholder = "") => () =>
    {
        var t = new TextBox { Text = get(), PlaceholderText = placeholder, MinWidth = 260, FontFamily = Mono, FontSize = 12 };
        t.LostFocus += (_, _) => { if (t.Text != get()) { set(t.Text.Trim()); Save(); } };
        t.KeyDown += (_, e) => { if (e.Key == Windows.System.VirtualKey.Enter && t.Text != get()) { set(t.Text.Trim()); Save(); } };
        return t;
    };

    private Func<FrameworkElement> TextList(Func<List<string>> get, Action<List<string>> set, string placeholder) => () =>
    {
        var t = new TextBox { Text = string.Join(Environment.NewLine, get()), PlaceholderText = placeholder, AcceptsReturn = true, MinWidth = 320, MinHeight = 70, FontFamily = Mono, FontSize = 12, TextWrapping = TextWrapping.NoWrap };
        t.LostFocus += (_, _) =>
        {
            var list = t.Text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();
            if (!list.SequenceEqual(get())) { set(list); Save(); }
        };
        return t;
    };

    private void Build()
    {
        _all =
        [
            new("Appearance", "appearance.theme", "Theme", "System follows the Windows light/dark setting.", Choice(() => S.Appearance.Theme, v => S.Appearance.Theme = v)),
            new("Appearance", "appearance.accentColor", "Accent color", "Hex like #4cc2ff. Empty follows the Windows accent color.", Text(() => S.Appearance.AccentColor ?? "", v => S.Appearance.AccentColor = v.Length == 0 ? null : v, "#4cc2ff")),
            new("Appearance", "appearance.mica", "Mica background", "Translucent Windows 11 window background.", Bool(() => S.Appearance.Mica, v => S.Appearance.Mica = v)),
            new("Appearance", "appearance.density", "Density", "", Choice(() => S.Appearance.Density, v => S.Appearance.Density = v)),
            new("Appearance", "appearance.uiFont", "UI font", "", Text(() => S.Appearance.UiFont, v => S.Appearance.UiFont = v)),
            new("Appearance", "appearance.monoFont", "Monospace font", "Paths, sizes, status bar and palette.", Text(() => S.Appearance.MonoFont, v => S.Appearance.MonoFont = v)),
            new("Appearance", "appearance.fontSize", "Font size", "", Number(() => S.Appearance.FontSize, v => S.Appearance.FontSize = v, 9, 24, 0.5)),

            new("Startup", "app.resident", "Keep running in the background", "Tray icon; windows open instantly. Off: Pollux quits when the last window closes (unless the Clipboard History is on).", Bool(() => S.App.Resident, v => S.App.Resident = v)),
            new("Startup", "app.startWithWindows", "Start with Windows", "Starts hidden in the tray at login.", Bool(() => S.App.StartWithWindows, v => S.App.StartWithWindows = v)),
            new("Startup", "app.restoreWindows", "Restore windows", "Reopen windows with their panes and tabs.", Bool(() => S.App.RestoreWindows, v => S.App.RestoreWindows = v)),
            new("Startup", "app.takeOverWinE", "Open Pollux with Win+E", "Replaces Explorer's Win+E while Pollux runs. Turn off to restore Explorer.", Bool(() => S.App.TakeOverWinE, v => S.App.TakeOverWinE = v)),
            new("Startup", "app.globalHotkey", "Global hotkey: new window", "Works from any app while Pollux runs.", Text(() => S.App.GlobalHotkey, v => S.App.GlobalHotkey = v, "win+alt+e")),
            new("Startup", "app.startupLog", "Write startup timing log", "%LocalAppData%\\Pollux\\logs\\startup.log (local only).", Bool(() => S.App.StartupLog, v => S.App.StartupLog = v)),

            new("Files", "files.showHidden", "Show hidden files", "Ctrl+H toggles.", Bool(() => S.Files.ShowHidden, v => S.Files.ShowHidden = v)),
            new("Files", "files.showSystem", "Show protected system files", "", Bool(() => S.Files.ShowSystem, v => S.Files.ShowSystem = v)),
            new("Files", "files.showExtensions", "Show file extensions", "", Bool(() => S.Files.ShowExtensions, v => S.Files.ShowExtensions = v)),
            new("Files", "files.operationEngine", "Copy / move engine", "pollux: queue with progress in the status bar, pause and cancel. windows: the classic Windows copy dialogs.", Options(["pollux", "windows"], () => S.Files.OperationEngine, v => S.Files.OperationEngine = v)),
            new("Files", "files.conflictPolicy", "When a file already exists", "Ask, or decide automatically.", Choice(() => S.Files.ConflictPolicy, v => S.Files.ConflictPolicy = v)),
            new("Files", "files.gitStatus", "Git status in file lists", "Letters M/A/U/R/D next to changed files; ignored files dimmed. Runs git locally.", Bool(() => S.Files.GitStatus, v => S.Files.GitStatus = v)),
            new("Files", "files.foldersFirst", "Folders first", "", Bool(() => S.Files.FoldersFirst, v => S.Files.FoldersFirst = v)),
            new("Files", "files.sortBy", "Sort by", "", Choice(() => S.Files.SortBy, v => S.Files.SortBy = v)),
            new("Files", "files.sortDescending", "Sort descending", "", Bool(() => S.Files.SortDescending, v => S.Files.SortDescending = v)),
            new("Files", "files.dateFormat", "Date format", ".NET format string.", Text(() => S.Files.DateFormat, v => S.Files.DateFormat = v, "yyyy-MM-dd HH:mm")),
            new("Files", "files.confirmDelete", "Confirm delete to recycle bin", "", Bool(() => S.Files.ConfirmDelete, v => S.Files.ConfirmDelete = v)),
            new("Files", "files.confirmPermanentDelete", "Confirm permanent delete", "Shift+Del.", Bool(() => S.Files.ConfirmPermanentDelete, v => S.Files.ConfirmPermanentDelete = v)),
            new("Files", "files.slowFolderSeconds", "Slow folder hint after (seconds)", "For network paths that hang.", Number(() => S.Files.SlowFolderSeconds, v => S.Files.SlowFolderSeconds = (int)v, 1, 60)),

            new("Tabs & Panes", "tabs.newTabLocation", "New tab opens", "\"home\" or a folder path.", Text(() => S.Tabs.NewTabLocation, v => S.Tabs.NewTabLocation = v, "home")),
            new("Tabs & Panes", "tabs.dualPaneOnStart", "Dual pane in new windows", "", Bool(() => S.Tabs.DualPaneOnStart, v => S.Tabs.DualPaneOnStart = v)),
            new("Tabs & Panes", "tabs.showTabCloseButtons", "Show close buttons on tabs", "Middle-click always closes.", Bool(() => S.Tabs.ShowTabCloseButtons, v => S.Tabs.ShowTabCloseButtons = v)),

            new("Sidebar", "sidebar.visible", "Show sidebar", "Ctrl+B toggles.", Bool(() => S.Sidebar.Visible, v => S.Sidebar.Visible = v)),
            new("Sidebar", "sidebar.width", "Width", "", Number(() => S.Sidebar.Width, v => S.Sidebar.Width = v, 140, 480, 10)),
            new("Sidebar", "sidebar.showRecent", "Show Recent", "", Bool(() => S.Sidebar.ShowRecent, v => S.Sidebar.ShowRecent = v)),
            new("Sidebar", "sidebar.recentCount", "Recent entries in sidebar", "", Number(() => S.Sidebar.RecentCount, v => S.Sidebar.RecentCount = (int)v, 0, 20)),
            new("Sidebar", "sidebar.showDrives", "Show drives", "", Bool(() => S.Sidebar.ShowDrives, v => S.Sidebar.ShowDrives = v)),
            new("Sidebar", "recent.maxItems", "Remember up to N recent locations", "", Number(() => S.Recent.MaxItems, v => S.Recent.MaxItems = (int)v, 0, 500)),
            new("Sidebar", "recent.exclude", "Never remember these folders", "One path per line; subfolders included.", TextList(() => S.Recent.Exclude, v => S.Recent.Exclude = v, @"C:\Temp")),

            new("Status Bar", "statusBar.visible", "Show status bar", "", Bool(() => S.StatusBar.Visible, v => S.StatusBar.Visible = v)),
            new("Status Bar", "statusBar.refreshSeconds", "Refresh interval (seconds)", "Only while a window is visible.", Number(() => S.StatusBar.RefreshSeconds, v => S.StatusBar.RefreshSeconds = (int)v, 1, 60)),
            new("Status Bar", "statusBar.cpu", "CPU", "", Bool(() => S.StatusBar.Cpu, v => S.StatusBar.Cpu = v)),
            new("Status Bar", "statusBar.gpu", "GPU", "", Bool(() => S.StatusBar.Gpu, v => S.StatusBar.Gpu = v)),
            new("Status Bar", "statusBar.vram", "Video memory", "", Bool(() => S.StatusBar.Vram, v => S.StatusBar.Vram = v)),
            new("Status Bar", "statusBar.ram", "System memory", "", Bool(() => S.StatusBar.Ram, v => S.StatusBar.Ram = v)),
            new("Status Bar", "statusBar.disk", "Drive free space", "", Bool(() => S.StatusBar.Disk, v => S.StatusBar.Disk = v)),
            new("Status Bar", "statusBar.selection", "Selection info", "", Bool(() => S.StatusBar.Selection, v => S.StatusBar.Selection = v)),

            new("Search", "search.indexRoots", "Index roots", "Folders Quick Search (Ctrl+P) covers, one per line, e.g. your repos folder.", TextList(() => S.Search.IndexRoots, v => S.Search.IndexRoots = v, @"C:\dev")),
            new("Search", "search.indexBookmarks", "Index bookmarked folders", "", Bool(() => S.Search.IndexBookmarks, v => S.Search.IndexBookmarks = v)),
            new("Search", "search.indexDownloads", "Index Downloads", "", Bool(() => S.Search.IndexDownloads, v => S.Search.IndexDownloads = v)),
            new("Search", "search.indexDesktop", "Index Desktop", "", Bool(() => S.Search.IndexDesktop, v => S.Search.IndexDesktop = v)),
            new("Search", "search.indexDocuments", "Index Documents", "", Bool(() => S.Search.IndexDocuments, v => S.Search.IndexDocuments = v)),
            new("Search", "search.indexNetworkPaths", "Index network paths", "Can be slow.", Bool(() => S.Search.IndexNetworkPaths, v => S.Search.IndexNetworkPaths = v)),
            new("Search", "search.excludeNames", "Skip folders named", "One per line.", TextList(() => S.Search.ExcludeNames, v => S.Search.ExcludeNames = v, "node_modules")),
            new("Search", "search.maxResults", "Max results", "", Number(() => S.Search.MaxResults, v => S.Search.MaxResults = (int)v, 10, 500, 10)),
            new("Search", "filter.matchInitials", "Filter matches word initials", "\"dbp\" finds Directory.Build.props.", Bool(() => S.Filter.MatchInitials, v => S.Filter.MatchInitials = v)),

            new("Tools", "terminal.app", "Terminal", "", Options(["wt", "pwsh", "powershell", "cmd"], () => S.Terminal.App, v => S.Terminal.App = v)),
            new("Tools", "terminal.windowsTerminalProfile", "Windows Terminal profile", "Empty = default profile.", Text(() => S.Terminal.WindowsTerminalProfile, v => S.Terminal.WindowsTerminalProfile = v, "PowerShell")),
            new("Tools", "terminal.fallbackShell", "Fallback shell", "Used when Windows Terminal is missing.", Options(["pwsh", "powershell", "cmd"], () => S.Terminal.FallbackShell, v => S.Terminal.FallbackShell = v)),
            new("Tools", "terminal.embeddedShell", "Embedded terminal shell", "Ctrl+ö toggles the panel.", Options(["pwsh", "powershell", "cmd"], () => S.Terminal.EmbeddedShell, v => S.Terminal.EmbeddedShell = v)),
            new("Tools", "terminal.panelHeight", "Embedded terminal height", "", Number(() => S.Terminal.PanelHeight, v => S.Terminal.PanelHeight = v, 120, 900, 10)),
            new("Tools", "terminal.fontSize", "Embedded terminal font size", "", Number(() => S.Terminal.FontSize, v => S.Terminal.FontSize = v, 8, 28)),
            new("Tools", "terminal.followPane", "Terminal follows active pane", "cd into the folder whenever the active pane changes location.", Bool(() => S.Terminal.FollowPane, v => S.Terminal.FollowPane = v)),
            new("Tools", "integration.vsCodeCommand", "VS Code command", "", Text(() => S.Integration.VsCodeCommand, v => S.Integration.VsCodeCommand = v, "code")),
            new("Tools", "integration.copilotAppCommand", "GitHub Copilot app command", "Command to open a folder; {path} is replaced.", Text(() => S.Integration.CopilotAppCommand, v => S.Integration.CopilotAppCommand = v, "copilot {path}")),
        ];
        _all.AddRange(ExtraSettings());
    }

    /// <summary>Settings for Notes, Downloads, Updates, Inbox and the Clipboard History.</summary>
    private IEnumerable<Setting> ExtraSettings() =>
    [
        new("Notes", "notes.folder", "Notes folder", "Empty = Documents\\Pollux\\Notes. Plain Markdown files.", Text(() => S.Notes.Folder, v => S.Notes.Folder = v, @"Documents\Pollux\Notes")),
        new("Notes", "notes.globalHotkey", "Global hotkey: new note", "", Text(() => S.Notes.GlobalHotkey, v => S.Notes.GlobalHotkey = v, "win+alt+n")),
        new("Notes", "notes.editorFontSize", "Editor font size", "", Number(() => S.Notes.EditorFontSize, v => S.Notes.EditorFontSize = v, 9, 32)),
        new("Notes", "notes.wordWrap", "Word wrap", "", Bool(() => S.Notes.WordWrap, v => S.Notes.WordWrap = v)),
        new("Downloads", "downloads.globalHotkey", "Global hotkey: Downloads Stack", "Also: left-click the tray icon.", Text(() => S.Downloads.GlobalHotkey, v => S.Downloads.GlobalHotkey = v, "win+alt+d")),
        new("Downloads", "downloads.maxItems", "Items in the Downloads Stack", "", Number(() => S.Downloads.MaxItems, v => S.Downloads.MaxItems = (int)v, 5, 200)),
        new("Downloads", "downloads.gridView", "Grid view", "Off = list.", Bool(() => S.Downloads.GridView, v => S.Downloads.GridView = v)),
        new("Updates", "updates.checkAutomatically", "Check for app updates with winget", "Opt-in. winget contacts the internet; Pollux itself never does.", Bool(() => S.Updates.CheckAutomatically, v => S.Updates.CheckAutomatically = v)),
        new("Updates", "updates.checkIntervalHours", "Check interval (hours)", "", Number(() => S.Updates.CheckIntervalHours, v => S.Updates.CheckIntervalHours = (int)v, 1, 168)),
        new("Updates", "updates.ignoredPackages", "Ignored packages", "winget package ids, one per line.", TextList(() => S.Updates.IgnoredPackages, v => S.Updates.IgnoredPackages = v, "Microsoft.Teams")),
        new("Preview", "preview.paneVisible", "Show preview pane", "Alt+P toggles. Follows the selection in the active pane.", Bool(() => S.Preview.PaneVisible, v => S.Preview.PaneVisible = v)),
        new("Preview", "preview.paneWidth", "Preview pane width", "", Number(() => S.Preview.PaneWidth, v => S.Preview.PaneWidth = v, 220, 1200, 20)),
        new("Preview", "preview.maxTextMegabytes", "Largest file shown as text (MB)", "Bigger files are shown as hex.", Number(() => S.Preview.MaxTextMegabytes, v => S.Preview.MaxTextMegabytes = (int)v, 1, 64)),
        new("Inbox", "inbox.hideDesktopIcons", "Hide desktop icons", "Keeps the desktop clean; use Pollux's Inbox to see its files.", Bool(() => S.Inbox.HideDesktopIcons, v => S.Inbox.HideDesktopIcons = v)),
        new("Inbox", "inbox.sortTarget", "Sort Inbox into", "Folder that \"Sort Inbox\" moves files into (by type). Empty = Documents\\Inbox.", Text(() => S.Inbox.SortTarget, v => S.Inbox.SortTarget = v, @"Documents\Inbox")),
        new("Clipboard", "clipboard.enabled", "Record Clipboard History", "Off by default. Keeps every Clip (text, images, file lists) encrypted on this PC only. While on, Pollux keeps running in the background even if that setting is off.", Bool(() => S.Clipboard.Enabled, v => S.Clipboard.Enabled = v)),
        new("Clipboard", "clipboard.globalHotkey", "Global hotkey: Clipboard Stack", "Works from any app while Pollux runs.", Text(() => S.Clipboard.GlobalHotkey, v => S.Clipboard.GlobalHotkey = v, "win+alt+v")),
        new("Clipboard", "clipboard.takeOverWinV", "Open the Clipboard Stack with Win+V", "Turns off Windows' own clipboard history while Pollux runs and takes over Win+V. Turn off to restore Windows (also on quit and uninstall).", Bool(() => S.Clipboard.TakeOverWinV, v => S.Clipboard.TakeOverWinV = v)),
        new("Clipboard", "clipboard.paused", "Pause", "Copies are not recorded while paused. Also in the tray menu and the command palette.", Bool(() => S.Clipboard.Paused, v => S.Clipboard.Paused = v)),
        new("Clipboard", "clipboard.excludeProcesses", "Never record copies from", "Process names, one per line. Password managers that mark their copies as private are always skipped.", TextList(() => S.Clipboard.ExcludeProcesses, v => S.Clipboard.ExcludeProcesses = v, "KeePass")),
        new("Clipboard", "clipboard.maxTextKilobytes", "Largest text Clip (KB)", "Larger copies are not recorded (noted in logs\\pollux.log). 0 = no limit.", Number(() => S.Clipboard.MaxTextKilobytes, v => S.Clipboard.MaxTextKilobytes = (int)v, 0, 102400, 64)),
        new("Clipboard", "clipboard.maxImageMegabytes", "Largest image Clip (MB)", "Larger copies are not recorded. 0 = no limit.", Number(() => S.Clipboard.MaxImageMegabytes, v => S.Clipboard.MaxImageMegabytes = (int)v, 0, 500)),
        new("Clipboard", "clipboard.maxClips", "Keep up to N Clips", "Older Clips rotate out; Kept Clips never do. 0 = no limit.", Number(() => S.Clipboard.MaxClips, v => S.Clipboard.MaxClips = (int)v, 0, 10000, 50)),
        new("Clipboard", "clipboard.maxAgeDays", "Keep Clips for (days)", "Whichever limit hits first. 0 = no limit.", Number(() => S.Clipboard.MaxAgeDays, v => S.Clipboard.MaxAgeDays = (int)v, 0, 3650)),
        new("Clipboard", "clipboard.pasteAsFile", "Ctrl+V creates a file", "In a folder, Ctrl+V with text or an image (and no files) on the clipboard creates a .txt or .png. Files still paste as a Transfer.", Bool(() => S.Clipboard.PasteAsFile, v => S.Clipboard.PasteAsFile = v)),
    ];

    // ---------------- keyboard ----------------

    private void RenderKeyboard()
    {
        _content.Children.Add(new TextBlock { Text = "Keyboard", FontSize = 24, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
        _content.Children.Add(new TextBlock { Text = "Click a shortcut field and press the new keys. Backspace clears. Stored in keybindings.json (VS Code format).", Foreground = Theme.Brush("PolluxMutedBrush"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) });
        var open = LinkButton("Open keybindings.json", () => Shell.Open(_app.Services.KeybindingsFile));
        open.Margin = new Thickness(0, 0, 0, 12);
        _content.Children.Add(open);

        var keys = _app.Services.Keys;
        foreach (var c in CommandCatalog.All.Concat(ExtraFeatures.Commands))
        {
            var grid = new Grid { ColumnSpacing = 16, Padding = new Thickness(12, 6, 12, 6), Background = Theme.Brush("PolluxSurfaceBrush"), CornerRadius = new CornerRadius(4), Margin = new Thickness(0, 0, 0, 1) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
            grid.Children.Add(new TextBlock { Text = c.Title, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
            var id = new TextBlock { Text = c.Id, FontFamily = Mono, FontSize = 11, Foreground = Theme.Brush("PolluxMutedBrush"), VerticalAlignment = VerticalAlignment.Center, IsTextSelectionEnabled = true };
            Grid.SetColumn(id, 1);
            grid.Children.Add(id);
            var box = new TextBox { Text = keys.KeyFor(c.Id)?.ToString() ?? "", FontFamily = Mono, FontSize = 12, IsReadOnly = true, PlaceholderText = "press keys…" };
            var command = c;
            box.PreviewKeyDown += (_, e) => RecordShortcut(box, command, e);
            Grid.SetColumn(box, 2);
            grid.Children.Add(box);
            _content.Children.Add(grid);
        }
    }

    private void RecordShortcut(TextBox box, CommandDefinition command, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Tab) return;
        e.Handled = true;
        var user = _app.Services.ReadUserKeybindings();
        var current = _app.Services.Keys.KeyFor(command.Id);
        if (e.Key == Windows.System.VirtualKey.Back)
        {
            if (current is not null) user.Add(new KeybindingEntry(current.Value.ToString().ToLowerInvariant(), "-" + command.Id));
            box.Text = "";
            _app.Services.SaveKeybindings(user);
            return;
        }

        var chord = KeyInput.ToChord(e.Key);
        if (chord is null) return;
        var text = chord.Value.ToString().ToLowerInvariant();
        if (current is not null) user.Add(new KeybindingEntry(current.Value.ToString().ToLowerInvariant(), "-" + command.Id));
        user.Add(new KeybindingEntry(text, command.Id, command.When));
        box.Text = chord.Value.ToString();
        _app.Services.SaveKeybindings(user);
    }
}
