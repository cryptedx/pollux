using Microsoft.UI.Dispatching;
using Pollux.Core;
using Pollux.Core.Commands;
using Pollux.Core.Places;
using Pollux.Core.Session;
using Pollux.Core.Settings;

namespace Pollux.App.Services;

/// <summary>App-wide state shared by all windows: settings, keybindings, Bookmarks, Recent, session. Lives as long as the process.</summary>
internal sealed class AppServices
{
    private readonly DispatcherQueue _dispatcher;
    private readonly List<FileSystemWatcher> _watchers = [];
    private DispatcherQueueTimer? _reloadDebounce;
    private DispatcherQueueTimer? _saveDebounce;

    public AppServices(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
        Paths = PolluxPaths.Current();
        Directory.CreateDirectory(Paths.ConfigDirectory);
        Directory.CreateDirectory(Paths.DataDirectory);

        SettingsFile = Path.Combine(Paths.ConfigDirectory, "settings.json");
        KeybindingsFile = Path.Combine(Paths.ConfigDirectory, "keybindings.json");
        BookmarksFile = Path.Combine(Paths.ConfigDirectory, "bookmarks.json");
        RecentFile = Path.Combine(Paths.DataDirectory, "recent.json");
        SessionFile = Path.Combine(Paths.DataDirectory, "session.json");

        var settings = SettingsStore.Load(SettingsFile);
        Settings = settings.Settings;
        SettingsError = settings.Error;
        Keys = LoadKeybindings();
        Bookmarks = File.Exists(BookmarksFile) ? JsonFile.Load(BookmarksFile, StateJson.Default.BookmarkList) : DefaultBookmarks();
        Recent = JsonFile.Load(RecentFile, StateJson.Default.RecentList);
        Session = JsonFile.Load(SessionFile, StateJson.Default.SessionState);
        Workspaces = new Core.Workspaces.WorkspaceStore(Path.Combine(Paths.ConfigDirectory, "workspaces"));
    }

    public PolluxPaths Paths { get; }
    public string SettingsFile { get; }
    public string KeybindingsFile { get; }
    public string BookmarksFile { get; }
    public string RecentFile { get; }
    public string SessionFile { get; }

    public PolluxSettings Settings { get; private set; }
    public string? SettingsError { get; private set; }
    public KeybindingMap Keys { get; private set; }
    public BookmarkList Bookmarks { get; }
    public RecentList Recent { get; }
    public SessionState Session { get; }
    public Core.Workspaces.WorkspaceStore Workspaces { get; }

    /// <summary>Raised on the UI thread after settings.json or keybindings.json changed.</summary>
    public event Action? SettingsChanged;

    /// <summary>Raised on the UI thread after Bookmarks or Recent changed.</summary>
    public event Action? PlacesChanged;

    public void StartWatching()
    {
        var w = new FileSystemWatcher(Paths.ConfigDirectory)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            EnableRaisingEvents = true,
        };
        w.Changed += OnConfigFileChanged;
        w.Created += OnConfigFileChanged;
        w.Renamed += OnConfigFileChanged;
        _watchers.Add(w);
    }

    private void OnConfigFileChanged(object sender, FileSystemEventArgs e)
    {
        var name = Path.GetFileName(e.FullPath);
        if (name is not ("settings.json" or "keybindings.json"))
        {
            return;
        }

        _dispatcher.TryEnqueue(() =>
        {
            _reloadDebounce ??= CreateTimer(TimeSpan.FromMilliseconds(150), ReloadConfig);
            _reloadDebounce.Start();
        });
    }

    public void ReloadConfig()
    {
        var result = SettingsStore.Load(SettingsFile, Settings);
        Settings = result.Settings;
        SettingsError = result.Error;
        Keys = LoadKeybindings();
        SettingsChanged?.Invoke();
    }

    public void SaveSettings()
    {
        SettingsStore.Save(SettingsFile, Settings);
        SettingsChanged?.Invoke();
    }

    public void SaveKeybindings(IEnumerable<KeybindingEntry> entries)
    {
        AtomicFile.WriteAllText(KeybindingsFile, KeybindingMap.SerializeUserFile(entries));
        Keys = LoadKeybindings();
        SettingsChanged?.Invoke();
    }

    public List<KeybindingEntry> ReadUserKeybindings()
    {
        try
        {
            return File.Exists(KeybindingsFile) ? KeybindingMap.ParseUserFile(File.ReadAllText(KeybindingsFile)) : [];
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException)
        {
            return [];
        }
    }

    private KeybindingMap LoadKeybindings()
    {
        if (!File.Exists(KeybindingsFile))
        {
            AtomicFile.WriteAllText(KeybindingsFile, """
                // Pollux keybindings. Same format as VS Code's keybindings.json:
                //   { "key": "ctrl+n", "command": "tab.new" }     add a binding
                //   { "key": "ctrl+t", "command": "-tab.new" }    remove a default binding
                // All command ids are listed in the Settings page (Keyboard section) and the command palette.
                []
                """);
        }

        try
        {
            return KeybindingMap.Build(CommandCatalog.All, ReadUserKeybindingsStrict());
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException)
        {
            SettingsError = "keybindings.json: " + ex.Message;
            return KeybindingMap.Build(CommandCatalog.All, []);
        }
    }

    private List<KeybindingEntry> ReadUserKeybindingsStrict() => KeybindingMap.ParseUserFile(File.ReadAllText(KeybindingsFile));

    public void PlacesUpdated()
    {
        PlacesChanged?.Invoke();
        _saveDebounce ??= CreateTimer(TimeSpan.FromSeconds(1), SavePlaces);
        _saveDebounce.Start();
    }

    public void SavePlaces()
    {
        JsonFile.Save(BookmarksFile, Bookmarks, StateJson.Default.BookmarkList);
        JsonFile.Save(RecentFile, Recent, StateJson.Default.RecentList);
    }

    public void SaveSession() => JsonFile.Save(SessionFile, Session, StateJson.Default.SessionState);

    public void VisitLocation(string path)
    {
        Recent.Visit(path, DateTime.Now, Settings.Recent.MaxItems, Settings.Recent.Exclude);
        PlacesUpdated();
    }

    private static BookmarkList DefaultBookmarks()
    {
        var list = new BookmarkList();
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        list.Add(Path.Combine(profile, "Downloads"), "Downloads", "Daily");
        list.Add(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Desktop", "Daily");
        list.Add(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Documents", "Daily");
        foreach (var candidate in new[] { Path.Combine(profile, "source", "repos"), Path.Combine(profile, "repos"), @"C:\dev" })
        {
            if (Directory.Exists(candidate))
            {
                list.Add(candidate, Path.GetFileName(candidate), "Code");
                break;
            }
        }

        return list;
    }

    private DispatcherQueueTimer CreateTimer(TimeSpan interval, Action action)
    {
        var t = _dispatcher.CreateTimer();
        t.Interval = interval;
        t.IsRepeating = false;
        t.Tick += (_, _) => action();
        return t;
    }
}
