using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.Web.WebView2.Core;
using Pollux.App.Services;
using Pollux.Core.Files;
using Pollux.Core.Notes;

namespace Pollux.App.Views;

internal sealed record NoteInfo(string Path, string Title, IReadOnlyList<string> Tags, DateTime Modified, string Text);

/// <summary>
/// Notes: plain Markdown files in the Notes folder, edited with Monaco (VS Code keys), saved automatically.
/// The file name follows the first line; Tags come from front matter or inline #tags.
/// </summary>
internal sealed partial class NotesWindow : Window
{
    private readonly App _app;
    private readonly Grid _root = new();
    private readonly TextBox _search = new() { PlaceholderText = "Search notes or #tag  (Ctrl+P)" };
    private readonly ListView _list = new() { SelectionMode = ListViewSelectionMode.Single, IsItemClickEnabled = true };
    private readonly StackPanel _tagPanel = new() { Orientation = Orientation.Horizontal, Spacing = 4 };
    private readonly WebView2 _web = new();
    private readonly TextBlock _status = new() { FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _saveTimer;
    private List<NoteInfo> _notes = [];
    private CoreWebView2? _core;
    private bool _ready;
    private string? _currentPath;
    private string _currentText = "";
    private string? _pendingText;
    private DateTime _currentCreated = DateTime.Now;
    private bool _newNotePending;
    private FileSystemWatcher? _watcher;

    public NotesWindow(App app)
    {
        _app = app;
        Title = "Pollux Notes";
        ExtendsContentIntoTitleBar = true;
        SystemBackdrop = new MicaBackdrop();
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "pollux.ico"));
        var scale = Interop.Native.GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
        AppWindow.Resize(new Windows.Graphics.SizeInt32((int)(1100 * scale), (int)(720 * scale)));
        _saveTimer = DispatcherQueue.CreateTimer();
        _saveTimer.Interval = TimeSpan.FromMilliseconds(400);
        _saveTimer.IsRepeating = false;
        _saveTimer.Tick += (_, _) => SavePending();
        Build();
        Content = _root;
        Closed += (_, _) => SavePending();
        AppWindow.Closing += (_, e) =>
        {
            SavePending();
            if (!_app.Services.Settings.KeepsResident) return;
            e.Cancel = true;
            AppWindow.Hide();
        };
        _ = InitWebAsync();
        _app.Services.SettingsChanged += OnThemeChanged;
        Closed += (_, _) => _app.Services.SettingsChanged -= OnThemeChanged;
        Directory.CreateDirectory(Folder);
        Watch();
        Reload();
    }

    private void OnThemeChanged()
    {
        _root.RequestedTheme = Theme.Current;
        Render();
        PushOptions();
    }

    /// <summary>Flushes unsaved typing (used before the app quits).</summary>
    public void SaveNow() => SavePending();

    private string Folder => SearchService.NotesFolder(_app.Services.Settings);

    public void ShowNotes(bool newNote)
    {
        _root.RequestedTheme = Theme.Current;
        AppWindow.Show(true);
        Activate();
        Interop.Native.SetForegroundWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
        if (newNote) NewNote();
        else if (_currentPath is null && _notes.Count > 0) Open(_notes[0]);
        PushOptions();
    }

    // ---------------- layout ----------------

    private void Build()
    {
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(34) });
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(280) });
        _root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        title.Children.Add(new TextBlock { Text = "pollux_ notes", FontFamily = Ui.Mono, FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        _status.FontFamily = Ui.Mono;
        title.Children.Add(_status);
        Grid.SetColumnSpan(title, 2);
        _root.Children.Add(title);
        SetTitleBar(title);

        var left = new Grid { Padding = new Thickness(10, 0, 6, 10), RowSpacing = 6 };
        left.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        left.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        left.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        left.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _search.FontFamily = Ui.Mono;
        _search.FontSize = 12;
        _search.TextChanged += (_, _) => Render();
        _search.KeyDown += Search_KeyDown;
        left.Children.Add(_search);
        var tagsScroll = new ScrollViewer { Content = _tagPanel, HorizontalScrollMode = ScrollMode.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollMode = ScrollMode.Disabled };
        Grid.SetRow(tagsScroll, 1);
        left.Children.Add(tagsScroll);
        _list.ItemClick += (_, e) => { if (e.ClickedItem is FrameworkElement { Tag: NoteInfo n }) Open(n); };
        _list.KeyDown += List_KeyDown;
        Grid.SetRow(_list, 2);
        left.Children.Add(_list);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var add = new Button { Content = "+ New note", FontSize = 12 };
        ToolTipService.SetToolTip(add, "Ctrl+N");
        add.Click += (_, _) => NewNote();
        var folder = new Button { Content = "Folder", FontSize = 12 };
        folder.Click += (_, _) => _app.OpenNewWindow(Folder);
        buttons.Children.Add(add);
        buttons.Children.Add(folder);
        Grid.SetRow(buttons, 3);
        left.Children.Add(buttons);
        Grid.SetRow(left, 1);
        _root.Children.Add(left);

        var editorHost = new Border { CornerRadius = new CornerRadius(8, 0, 0, 0), Child = _web, Margin = new Thickness(0, 0, 0, 0) };
        Grid.SetRow(editorHost, 1);
        Grid.SetColumn(editorHost, 1);
        _root.Children.Add(editorHost);

        _root.KeyDown += (_, e) =>
        {
            if (!KeyInput.IsDown(Windows.System.VirtualKey.Control)) return;
            if (e.Key == Windows.System.VirtualKey.N) { e.Handled = true; NewNote(); }
            else if (e.Key == Windows.System.VirtualKey.P) { e.Handled = true; FocusSearch(); }
            else if (e.Key == Windows.System.VirtualKey.W) { e.Handled = true; SavePending(); AppWindow.Hide(); }
        };
    }

    private void FocusSearch()
    {
        _search.Focus(FocusState.Keyboard);
        _search.SelectAll();
    }

    private void Search_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Down && _list.Items.Count > 0)
        {
            e.Handled = true;
            _list.SelectedIndex = 0;
            (_list.ContainerFromIndex(0) as ListViewItem)?.Focus(FocusState.Keyboard);
        }
        else if (e.Key == Windows.System.VirtualKey.Enter)
        {
            e.Handled = true;
            if (_list.Items.Count > 0 && _list.Items[0] is FrameworkElement { Tag: NoteInfo n }) Open(n);
        }
        else if (e.Key == Windows.System.VirtualKey.Escape)
        {
            e.Handled = true;
            _search.Text = "";
            FocusEditor();
        }
    }

    private void List_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_list.SelectedItem is not FrameworkElement { Tag: NoteInfo n }) return;
        if (e.Key == Windows.System.VirtualKey.Enter) { e.Handled = true; Open(n); }
        else if (e.Key == Windows.System.VirtualKey.Delete) { e.Handled = true; Delete(n); }
    }

    // ---------------- notes ----------------

    private void Reload()
    {
        var list = new List<NoteInfo>();
        try
        {
            foreach (var file in Directory.EnumerateFiles(Folder, "*.md", SearchOption.AllDirectories))
            {
                try
                {
                    var text = File.ReadAllText(file);
                    list.Add(new NoteInfo(file, NoteText.Title(text) is { Length: > 0 } t ? t : Path.GetFileNameWithoutExtension(file), NoteText.Tags(text), File.GetLastWriteTime(file), text));
                }
                catch (IOException)
                {
                }
            }
        }
        catch (DirectoryNotFoundException)
        {
        }

        _notes = list.OrderByDescending(n => n.Modified).ToList();
        Render();
        PushOptions();
    }

    private void Render()
    {
        var q = _search.Text.Trim();
        var tagFilter = q.StartsWith('#') ? q[1..].ToLowerInvariant() : null;
        var visible = _notes.Where(n =>
            q.Length == 0 ||
            (tagFilter is not null ? n.Tags.Any(t => t.StartsWith(tagFilter, StringComparison.Ordinal)) :
             n.Title.Contains(q, StringComparison.OrdinalIgnoreCase) || n.Text.Contains(q, StringComparison.OrdinalIgnoreCase))).ToList();

        var items = new List<FrameworkElement>();
        foreach (var n in visible)
        {
            var p = new StackPanel { Spacing = 1, Padding = new Thickness(2, 6, 2, 6), Tag = n };
            p.Children.Add(new TextBlock { Text = n.Title, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis, FontWeight = n.Path == _currentPath ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal });
            var meta = Format.Relative(n.Modified, DateTime.Now) + (n.Tags.Count > 0 ? "  " + string.Join(' ', n.Tags.Select(t => "#" + t)) : "");
            p.Children.Add(new TextBlock { Text = meta, FontFamily = Ui.Mono, FontSize = 10.5, Foreground = Theme.Brush("PolluxMutedBrush"), TextTrimming = TextTrimming.CharacterEllipsis });
            var menu = new MenuFlyout();
            var reveal = new MenuFlyoutItem { Text = "Show in Pollux" };
            reveal.Click += (_, _) => _app.OpenNewWindow().Reveal(n.Path);
            var code = new MenuFlyoutItem { Text = "Open in VS Code" };
            code.Click += (_, _) => Shell.OpenInVsCode(n.Path, _app.Services.Settings.Integration.VsCodeCommand);
            var del = new MenuFlyoutItem { Text = "Delete (recycle bin)" };
            del.Click += (_, _) => Delete(n);
            menu.Items.Add(reveal);
            menu.Items.Add(code);
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(del);
            p.ContextFlyout = menu;
            items.Add(p);
        }

        _list.ItemsSource = items;

        _tagPanel.Children.Clear();
        foreach (var (tag, count) in _notes.SelectMany(n => n.Tags).GroupBy(t => t).OrderByDescending(g => g.Count()).Select(g => (g.Key, g.Count())).Take(30))
        {
            var b = new Button { Content = $"#{tag} {count}", FontFamily = Ui.Mono, FontSize = 11, Padding = new Thickness(6, 2, 6, 2), Style = Ui.FlatButton, Background = Theme.Brush(tagFilter == tag ? "PolluxAccentSoftBrush" : "PolluxSurface2Brush") };
            var t = tag;
            b.Click += (_, _) => _search.Text = tagFilter == t ? "" : "#" + t;
            _tagPanel.Children.Add(b);
        }
    }

    private void NewNote()
    {
        SavePending();
        _currentPath = null;
        _currentText = "";
        _currentCreated = DateTime.Now;
        _newNotePending = true;
        Post(WebHost.Json(("type", "load"), ("text", ""), ("cursorEnd", true)));
        FocusEditor();
        _status.Text = "new note";
        Render();
    }

    private void Open(NoteInfo n)
    {
        SavePending();
        _currentPath = n.Path;
        _newNotePending = false;
        _currentCreated = File.GetCreationTime(n.Path);
        try { _currentText = File.ReadAllText(n.Path); }
        catch (IOException) { _currentText = n.Text; }
        Post(WebHost.Json(("type", "load"), ("text", _currentText), ("cursorEnd", false)));
        FocusEditor();
        _status.Text = Path.GetFileName(n.Path);
        Render();
    }

    private void Delete(NoteInfo n)
    {
        if (n.Path == _currentPath)
        {
            // Drop pending edits of the deleted note, otherwise the save timer would write it back as a new note.
            _saveTimer.Stop();
            _pendingText = null;
            _currentPath = null;
            Post(WebHost.Json(("type", "load"), ("text", "")));
        }

        Shell.Delete(WinRT.Interop.WindowNative.GetWindowHandle(this), [n.Path], permanent: false, confirm: false);
        Reload();
    }

    private void OnEditorChanged(string text)
    {
        _pendingText = text;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    /// <summary>Writes the pending text; renames the file when the first line changed.</summary>
    private void SavePending()
    {
        _saveTimer.Stop();
        if (_pendingText is null) return;
        var text = _pendingText;
        _pendingText = null;
        if (_currentPath is null && text.Trim().Length == 0) return;
        try
        {
            Directory.CreateDirectory(Folder);
            var desired = Path.Combine(_currentPath is null ? Folder : Path.GetDirectoryName(_currentPath)!, NoteText.FileName(text, _currentCreated));
            if (_currentPath is null)
            {
                _currentPath = Unique(desired);
            }
            else if (!File.Exists(_currentPath))
            {
                // Deleted or renamed outside Pollux: save under the current title instead of failing on every edit.
                _currentPath = Unique(desired);
            }
            else if (!string.Equals(desired, _currentPath, StringComparison.OrdinalIgnoreCase) && !File.Exists(desired))
            {
                File.Move(_currentPath, desired);
                _currentPath = desired;
            }

            Core.AtomicFile.WriteAllText(_currentPath, text);
            _currentText = text;
            _newNotePending = false;
            _status.Text = $"{Path.GetFileName(_currentPath)} · saved {DateTime.Now:HH:mm:ss}";
            ReloadSoon();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Keep the text pending so the next attempt (timer, switch, close) retries instead of losing it.
            _pendingText ??= text;
            _status.Text = "save failed (will retry): " + ex.Message;
            _saveTimer.Interval = TimeSpan.FromSeconds(2);
            _saveTimer.Start();
            return;
        }

        _saveTimer.Interval = TimeSpan.FromMilliseconds(400);
    }

    private static string Unique(string path)
    {
        if (!File.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        for (var i = 2; ; i++)
        {
            var p = Path.Combine(dir, $"{stem} ({i}).md");
            if (!File.Exists(p)) return p;
        }
    }

    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _reloadTimer;

    private void ReloadSoon()
    {
        if (_reloadTimer is null)
        {
            _reloadTimer = DispatcherQueue.CreateTimer();
            _reloadTimer.Interval = TimeSpan.FromMilliseconds(300);
            _reloadTimer.IsRepeating = false;
            _reloadTimer.Tick += (_, _) => Reload();
        }

        _reloadTimer.Start();
    }

    private void Watch()
    {
        try
        {
            _watcher = new FileSystemWatcher(Folder, "*.md") { IncludeSubdirectories = true, EnableRaisingEvents = true };
            FileSystemEventHandler h = (_, _) => DispatcherQueue.TryEnqueue(ReloadSoon);
            _watcher.Created += h;
            _watcher.Deleted += h;
            _watcher.Renamed += (_, _) => DispatcherQueue.TryEnqueue(ReloadSoon);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException)
        {
            _watcher = null;
        }
    }

    // ---------------- web ----------------

    private async Task InitWebAsync()
    {
        try
        {
            _core = await WebHost.InitializeAsync(_web, "notes.html");
            _core.WebMessageReceived += Core_WebMessageReceived;
            _core.NavigationCompleted += (_, e) => { if (!e.IsSuccess) CrashLog.Write(new InvalidOperationException($"notes.html navigation failed: {e.WebErrorStatus} {e.HttpStatusCode}")); };
        }
        catch (Exception ex)
        {
            _status.Text = "editor unavailable: " + ex.Message;
            CrashLog.Write(ex);
        }
    }

    private void Core_WebMessageReceived(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        using var doc = JsonDocument.Parse(args.TryGetWebMessageAsString());
        var type = doc.RootElement.GetProperty("type").GetString();
        switch (type)
        {
            case "ready":
                _ready = true;
                PushOptions();
                if (_newNotePending) { Post(WebHost.Json(("type", "load"), ("text", ""), ("cursorEnd", true))); FocusEditor(); }
                else if (_currentPath is not null) Post(WebHost.Json(("type", "load"), ("text", _currentText)));
                else if (_notes.Count > 0) Open(_notes[0]);
                break;
            case "changed":
                OnEditorChanged(doc.RootElement.GetProperty("text").GetString() ?? "");
                break;
            case "save":
                SavePending();
                break;
            case "new":
                NewNote();
                break;
            case "focusSearch":
                FocusSearch();
                break;
            case "escape":
                SavePending();
                break;
            case "error":
                CrashLog.Write(new InvalidOperationException("notes.html: " + doc.RootElement.GetProperty("message").GetString()));
                break;
        }
    }

    private void FocusEditor()
    {
        _web.Focus(FocusState.Programmatic);
        Post(WebHost.Json(("type", "focus")));
    }

    private void PushOptions()
    {
        var s = _app.Services.Settings.Notes;
        Post(WebHost.Json(("type", "options"), ("dark", Theme.Current == ElementTheme.Dark), ("fontSize", s.EditorFontSize), ("wordWrap", s.WordWrap),
            ("tags", (IEnumerable<string>)_notes.SelectMany(n => n.Tags).Distinct().ToList())));
    }

    private void Post(string json)
    {
        if (_ready) _core?.PostWebMessageAsString(json);
    }
}
