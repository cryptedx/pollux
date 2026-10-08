using System.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Pollux.App.Services;
using Pollux.App.Views;
using Pollux.Core.Commands;
using Pollux.Core.Session;

namespace Pollux.App;

/// <summary>The resident process: owns windows, tray, hotkeys and shared services (ADR 0003).</summary>
public partial class App : Application
{
    private readonly List<MainWindow> _windows = [];
    private readonly string[] _args;
    private TrayHost? _tray;
    private SettingsWindow? _settingsWindow;
    private bool _quitting;
    private DispatcherQueueTimer? _indexTimer;

    public App(string[] args)
    {
        _args = args;
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            CrashLog.Write(e.Exception);
            e.Handled = true;
        };
    }

    internal AppServices Services { get; private set; } = null!;
    internal SearchService Search { get; private set; } = null!;
    internal List<string> RecentCommands { get; } = [];
    internal MainWindow? LastActive { get; set; }
    internal DispatcherQueue Dispatcher { get; private set; } = null!;
    internal static App Instance => (App)Current;

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            Launch();
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex);
            throw;
        }
    }

    private void Launch()
    {
        StartupLog.Mark("onLaunched");
        Dispatcher = DispatcherQueue.GetForCurrentThread();
        Services = new AppServices(Dispatcher);
        Drives.Start(Dispatcher);
        Theme.Apply(Services.Settings.Appearance.Theme, Services.Settings.Appearance.AccentColor);
        StartupLog.Configure(Services.Paths.LogsDirectory, Services.Settings.App.StartupLog);
        StartupLog.Mark("services");

        var background = _args.Contains("--background") || _args.Contains("--notes") || _args.Contains("--downloads");
        var target = TargetFromArgs(_args);
        if (!background || target is not null)
        {
            var w = OpenWindow(restore: true, navigateTo: target);
            w.Activate();
            StartupLog.Mark("windowActivated");
        }
        else if (Services.Settings.App.Resident)
        {
            // Pre-create a hidden window so the first Win+E/tray open is instant.
            var w = OpenWindow(restore: true);
            w.HideWindow();
        }

        if (_args.Contains("--notes")) Dispatcher.TryEnqueue(() => ShowNotes(newNote: true));
        if (ValueAfter(_args, "--workspace") is { } ws) Dispatcher.TryEnqueue(() => _windows.FirstOrDefault()?.Execute("workspace.open:" + ws));
        if (_args.Contains("--downloads")) Dispatcher.TryEnqueue(() => ShowDownloadsStack());

        // Everything below is not needed for the first frame.
        Dispatcher.TryEnqueue(DispatcherQueuePriority.Low, StartBackgroundServices);
        AppInstance.GetCurrent().Activated += OnRedirectedActivation;
    }

    private void StartBackgroundServices()
    {
        Services.StartWatching();
        Services.SettingsChanged += ApplyAppSettings;
        Search = new SearchService(Services);
        Search.Rebuild();
        _indexTimer = Dispatcher.CreateTimer();
        _indexTimer.Interval = TimeSpan.FromMinutes(30);
        _indexTimer.Tick += (_, _) => Search.Rebuild();
        _indexTimer.Start();
        ApplyAppSettings();
        StartupLog.Mark("background");
        StartupLog.Flush(_args.Contains("--background") ? "background-start" : "cold-start");
    }

    private void ApplyAppSettings()
    {
        var s = Services.Settings;
        if (MainWindow.IsElevated)
        {
            // Admin Instance: no tray, no autostart, no hotkeys, no hooks.
            ExtraFeatures.ApplySettings(this);
            return;
        }

        try { Autostart.Apply(s.App.Resident && s.App.StartWithWindows); }
        catch (UnauthorizedAccessException) { }

        if (s.App.Resident && _tray is null)
        {
            _tray = new TrayHost(Dispatcher, Path.Combine(AppContext.BaseDirectory, "Assets", "pollux.ico"));
            _tray.ShowIcon(MainWindow.IsElevated ? "Pollux (Admin)" : "Pollux");
            _tray.OpenWindowRequested += () => OpenNewWindow();
            _tray.DownloadsRequested += () => ShowDownloadsStack();
            _tray.NotesRequested += () => ShowNotes();
            _tray.SettingsRequested += ShowSettings;
            _tray.UpdatesRequested += ShowUpdates;
            _tray.QuitRequested += Quit;
        }
        else if (!s.App.Resident && _tray is not null)
        {
            _tray.Dispose();
            _tray = null;
        }

        if (_tray is not null)
        {
            _tray.UnregisterAllHotkeys();
            TryHotkey(s.App.GlobalHotkey, () => OpenNewWindow());
            TryHotkey(s.Downloads.GlobalHotkey, () => ShowDownloadsStack());
            TryHotkey(s.Notes.GlobalHotkey, () => ShowNotes(newNote: true));
        }

        if (s.App.TakeOverWinE) WinEHook.Enable(Dispatcher, () => OpenNewWindow());
        else WinEHook.Disable();

        ExtraFeatures.ApplySettings(this);
    }

    private void TryHotkey(string chord, Action action)
    {
        if (!string.IsNullOrWhiteSpace(chord)) _tray?.RegisterHotkey(chord, action);
    }

    private void OnRedirectedActivation(object? sender, AppActivationArguments e)
    {
        var requested = Stopwatch.GetTimestamp();
        string[] args = [];
        if (e.Kind == ExtendedActivationKind.Launch && e.Data is Windows.ApplicationModel.Activation.ILaunchActivatedEventArgs l)
        {
            args = CommandLine.Split(l.Arguments);
            // The redirected command line starts with the program path.
            if (args.Length > 0 && args[0].EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) args = args[1..];
        }

        Dispatcher.TryEnqueue(() =>
        {
            if (args.Contains("--quit")) { Quit(); return; }
            if (args.Contains("--background")) return;
            if (args.Contains("--downloads")) { ShowDownloadsStack(); return; }
            if (args.Contains("--notes")) { ShowNotes(newNote: true); return; }
            if (ValueAfter(args, "--workspace") is { } workspace)
            {
                OpenNewWindow().Execute("workspace.open:" + workspace);
                return;
            }

            if (CommandsFromArgs(args) is { Count: > 0 } commands)
            {
                var target = (LastActive is { IsHidden: false } la && _windows.Contains(la) ? la : _windows.FirstOrDefault(w => !w.IsHidden)) ?? OpenNewWindow();
                foreach (var c in commands) target.Execute(c);
                return;
            }

            OpenNewWindow(TargetFromArgs(args));
            StartupLog.Warm("window", requested);
        });
    }

    private static string? ValueAfter(string[] args, string flag)
    {
        var i = Array.IndexOf(args, flag);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    /// <summary>"--command id[,id…]" runs commands in the frontmost window (scripting and automation).</summary>
    private static List<string> CommandsFromArgs(string[] args)
    {
        var i = Array.IndexOf(args, "--command");
        return i >= 0 && i + 1 < args.Length ? args[i + 1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList() : [];
    }

    private static string? TargetFromArgs(string[] args)
    {
        var path = args.Where((a, i) => !a.StartsWith("--", StringComparison.Ordinal) && (i == 0 || (args[i - 1] != "--command" && args[i - 1] != "--workspace"))).FirstOrDefault();
        return path is not null && (Directory.Exists(path) || File.Exists(path)) ? Path.GetFullPath(path) : null;
    }

    // ---------------- windows ----------------

    private MainWindow OpenWindow(bool restore, string? navigateTo = null)
    {
        WindowSession session;
        if (restore && Services.Settings.App.RestoreWindows && Services.Session.Windows.Count > _windows.Count)
        {
            session = Services.Session.Windows[_windows.Count];
        }
        else
        {
            session = new WindowSession { DualPane = Services.Settings.Tabs.DualPaneOnStart };
            if (Services.Session.Windows.Count > 0)
            {
                var last = Services.Session.Windows[^1];
                session.Width = last.Width;
                session.Height = last.Height;
            }
        }

        if (navigateTo is not null)
        {
            var dir = Directory.Exists(navigateTo) ? navigateTo : Path.GetDirectoryName(navigateTo)!;
            session.Panes[session.ActivePane].Tabs.Insert(0, new TabSession { Location = dir });
            session.Panes[session.ActivePane].ActiveTab = 0;
        }

        var w = new MainWindow(this, session);
        _windows.Add(w);
        w.Closed += (_, _) =>
        {
            _windows.Remove(w);
            if (_windows.Count == 0 && (!Services.Settings.App.Resident || MainWindow.IsElevated)) Exit();
        };
        return w;
    }

    /// <summary>Opens a window: reuses a hidden one (instant) or creates a new one.</summary>
    internal MainWindow OpenNewWindow(string? navigateTo = null)
    {
        if (navigateTo is not null && File.Exists(navigateTo))
        {
            // A file: open its folder with the file selected.
            var folderWindow = OpenNewWindow(Path.GetDirectoryName(navigateTo));
            folderWindow.Reveal(navigateTo);
            return folderWindow;
        }

        var hidden = _windows.FirstOrDefault(w => w.IsHidden);
        if (hidden is not null)
        {
            hidden.ShowWindow(navigateTo);
            return hidden;
        }

        var w = OpenWindow(restore: false, navigateTo);
        w.Activate();
        return w;
    }

    /// <summary>The Admin Instance never stays resident: it exists only while its windows are open.</summary>
    internal bool KeepResident(MainWindow w) => !_quitting && Services.Settings.App.Resident && !MainWindow.IsElevated;

    internal void OnWindowClosing(MainWindow w)
    {
        w.Execute("workspace.autosave");
        // Session = all visible windows, plus the one being closed.
        Services.Session.Windows = _windows.Where(x => !x.IsHidden || x == w).Select(x => x.ToSession()).ToList();
        Services.SaveSession();
        Services.SavePlaces();
    }

    internal void Quit()
    {
        _quitting = true;
        Services.Session.Windows = _windows.Where(x => !x.IsHidden).Select(x => x.ToSession()).ToList();
        if (Services.Session.Windows.Count == 0 && _windows.Count > 0) Services.Session.Windows = [_windows[0].ToSession()];
        Services.SaveSession();
        Services.SavePlaces();
        WinEHook.Disable();
        _tray?.Dispose();
        Search?.Dispose();
        foreach (var w in _windows.ToList()) w.Close();
        _settingsWindow?.Close();
        ExtraFeatures.Shutdown();
        Exit();
    }

    internal void ShowSettings()
    {
        if (_settingsWindow is null)
        {
            _settingsWindow = new SettingsWindow(this);
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        }

        _settingsWindow.Activate();
    }

    internal void ShowDownloadsStack() => ExtraFeatures.ShowDownloads(this);

    internal void ShowNotes(bool newNote = false) => ExtraFeatures.ShowNotes(this, newNote);

    internal void ShowUpdates() => ExtraFeatures.ShowUpdates(this);

    internal void NoteCommand(string id)
    {
        RecentCommands.Remove(id);
        RecentCommands.Insert(0, id);
        if (RecentCommands.Count > 10) RecentCommands.RemoveAt(10);
    }

    internal IEnumerable<CommandDefinition> ExtraCommands() => ExtraFeatures.Commands;

    internal bool ExecuteAppCommand(string id, MainWindow window) => ExtraFeatures.Execute(this, id, window);
}
