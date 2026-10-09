using System.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Pollux.App.Interop;
using Pollux.App.Services;
using Pollux.App.Views;
using Pollux.Copilot;
using Pollux.Copilot.Chat;
using Pollux.Copilot.Connection;
using Pollux.Copilot.Models;
using Pollux.Core.Clipboard;
using Pollux.Core.Commands;
using Pollux.Core.Copilot;
using Pollux.Core.Session;

namespace Pollux.App;

/// <summary>The resident process: owns windows, tray, hotkeys and shared services (ADR 0003).</summary>
public partial class App : Application
{
    private readonly List<MainWindow> _windows = [];
    private readonly string[] _args;
    private TrayHost? _tray;
    private bool _trayIconShown;
    private SettingsWindow? _settingsWindow;
    private bool _quitting;
    private bool _clipboardAsked;
    private WinVTakeover? _winV;
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

    /// <summary>The one Copilot CLI of this process, shared by all windows; nothing runs until first Copilot use.</summary>
    internal CopilotHost Copilot { get; private set; } = null!;

    /// <summary>The Chat list, global for all windows and Workspaces (%LocalAppData%\Pollux\copilot\chats.json); the file is read on first use.</summary>
    internal ChatStore CopilotChats => _copilotChats ??= new ChatStore(Path.Combine(Services.Paths.DataDirectory, "copilot"));

    private ChatStore? _copilotChats;

    /// <summary>The Action Logs of the Chats, kept next to <c>chats.json</c>.</summary>
    internal ActionLogStore CopilotActionLogs => _copilotActionLogs ??= new ActionLogStore(Path.Combine(Services.Paths.DataDirectory, "copilot"));

    private ActionLogStore? _copilotActionLogs;

    /// <summary>Works out the Copilot Panel's connection and sign-in state.</summary>
    internal CopilotConnectionMonitor CopilotMonitor { get; private set; } = null!;

    /// <summary>The latest known premium-request quota, shared by all Panels and the status bar; only the Panels refresh it.</summary>
    internal CopilotQuotaTracker CopilotQuotaState { get; private set; } = null!;

    /// <summary>Asks the running CLI for the quota; never starts the CLI.</summary>
    private async Task<CopilotQuota?> FetchCopilotQuotaAsync(CancellationToken cancellationToken) =>
        Copilot.State.Status == CopilotHostStatus.Running
        && await Copilot.ConnectAsync(cancellationToken).ConfigureAwait(false) is ICopilotCatalog catalog
            ? await catalog.GetQuotaAsync(cancellationToken).ConfigureAwait(false)
            : null;

    /// <summary>A new CLI connection; proxy and CA certificates from the settings go to the CLI process only.</summary>
    private CopilotCliConnection CreateCopilotConnection()
    {
        var c = Services.Settings.Copilot;
        var cli = CopilotCliLocator.Locate(c.CliPath);
        return new CopilotCliConnection(cli, environment: CopilotCliEnvironment.Build(c.Proxy, c.CaCertificates, CopilotCliEnvironment.Current()));
    }

    /// <summary>The CLI whose login the Panel's "Sign in" runs in a terminal.</summary>
    internal CopilotCli CurrentCopilotCli => CopilotCliLocator.Locate(Services.Settings.Copilot.CliPath);

    /// <summary>"Enable Copilot" is on and this is not the Admin Instance; gates every Copilot entry point.</summary>
    internal bool CopilotAvailable => CopilotAvailability.IsAvailable(Services.Settings.Copilot, MainWindow.IsElevated);
    internal SearchService Search { get; private set; } = null!;
    internal ClipboardService ClipboardService { get; private set; } = null!;
    internal List<string> RecentCommands { get; } = [];
    internal MainWindow? LastActive { get; set; }

    /// <summary>All copies and moves of all windows, run one after another.</summary>
    internal Core.Operations.Transfers Transfers { get; } = new();
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
        // Cheap: no SDK client and no CLI process until ConnectAsync (first Copilot use).
        Copilot = new CopilotHost(CreateCopilotConnection, CopilotAvailable);
        CopilotMonitor = new CopilotConnectionMonitor(Copilot);
        CopilotQuotaState = new CopilotQuotaTracker(FetchCopilotQuotaAsync);
        ClipboardService = new ClipboardService(Services, Dispatcher);
        ClipboardService.Changed += ExtraFeatures.RefreshClipboardStack;
        Drives.Start(Dispatcher);
        Theme.Apply(Services.Settings.Appearance.Theme, Services.Settings.Appearance.AccentColor);
        StartupLog.Configure(Services.Paths.LogsDirectory, Services.Settings.App.StartupLog);
        StartupLog.Mark("services");

        var background = _args.Contains("--background") || _args.Contains("--notes") || _args.Contains("--downloads") || _args.Contains("--clipboard");
        var target = TargetFromArgs(_args);
        if (!background || target is not null)
        {
            var w = OpenWindow(restore: true, navigateTo: target);
            w.Activate();
            StartupLog.Mark("windowActivated");
        }
        else if (Services.Settings.KeepsResident)
        {
            // Pre-create a hidden window so the first Win+E/tray open is instant.
            var w = OpenWindow(restore: true);
            w.HideWindow();
        }

        if (_args.Contains("--notes")) Dispatcher.TryEnqueue(() => ShowNotes(newNote: true));
        if (ValueAfter(_args, "--workspace") is { } ws) Dispatcher.TryEnqueue(() => _windows.FirstOrDefault()?.Execute("workspace.open:" + ws));
        if (CommandsFromArgs(_args) is { Count: > 0 } startCommands)
        {
            // Same as for a running instance: commands go to the (first) window once it exists.
            Dispatcher.TryEnqueue(() =>
            {
                var target = _windows.FirstOrDefault(w => !w.IsHidden) ?? OpenNewWindow();
                foreach (var c in startCommands) target.Execute(c);
            });
        }
        if (_args.Contains("--downloads")) Dispatcher.TryEnqueue(() => ShowDownloadsStack());
        if (_args.Contains("--clipboard")) Dispatcher.TryEnqueue(() => ShowClipboardStack());

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
        _ = Copilot.SetAvailableAsync(CopilotAvailable); // off: stops the CLI; on: waits for first use
        if (!CopilotAvailable) CopilotQuotaState.Clear();
        if (MainWindow.IsElevated)
        {
            // Admin Instance: no tray, no autostart, no hotkeys, no hooks, no clipboard capture.
            ClipboardService.Apply(elevated: true);
            ExtraFeatures.ApplySettings(this);
            return;
        }

        try { Autostart.Apply(s.KeepsResident && s.App.StartWithWindows); }
        catch (UnauthorizedAccessException) { }

        // The message window always exists (it owns the global hotkeys); the tray icon only while Pollux stays resident.
        // The Clipboard History keeps Pollux resident, with its tray icon (Pause lives in the tray menu).
        if (_tray is null)
        {
            _tray = new TrayHost(Dispatcher, Path.Combine(AppContext.BaseDirectory, "Assets", "pollux.ico"));
            _tray.OpenWindowRequested += () => OpenNewWindow();
            _tray.DownloadsRequested += () => ShowDownloadsStack();
            _tray.NotesRequested += () => ShowNotes();
            _tray.SettingsRequested += ShowSettings;
            _tray.UpdatesRequested += ShowUpdates;
            _tray.ClipboardPauseRequested += () => ClipboardService.SetPaused(!Services.Settings.Clipboard.Paused);
            _tray.QuitRequested += Quit;
        }

        if (s.KeepsResident && !_trayIconShown)
        {
            _tray.ShowIcon("Pollux");
            _trayIconShown = true;
        }
        else if (!s.KeepsResident && _trayIconShown)
        {
            _tray.HideIcon();
            _trayIconShown = false;
        }

        _tray.UnregisterAllHotkeys();
        if (s.KeepsResident)
        {
            TryHotkey(s.App.GlobalHotkey, () => OpenNewWindow());
            TryHotkey(s.Downloads.GlobalHotkey, () => ShowDownloadsStack());
            TryHotkey(s.Notes.GlobalHotkey, () => ShowNotes(newNote: true));
        }

        // Also while not resident (Clipboard History off): the first press asks whether to turn it on.
        TryHotkey(s.Clipboard.GlobalHotkey, () => ShowClipboardStack());

        WinEHook.Apply(Dispatcher, s.App.TakeOverWinE ? () => OpenNewWindow() : null, s.Clipboard.TakeOverWinV ? () => ShowClipboardStack() : null);
        ApplyWinVTakeover(s.Clipboard.TakeOverWinV);

        ClipboardService.Apply(elevated: false);
        WireClipboardCommands(s.Clipboard.Enabled);
        if (_tray is not null)
        {
            _tray.ClipboardHistoryOn = s.Clipboard.Enabled;
            _tray.ClipboardPaused = s.Clipboard.Paused;
        }

        ExtraFeatures.ApplySettings(this);
    }

    /// <summary>Turns Windows' own Win+V clipboard history off while Pollux owns Win+V, and puts it back when the takeover ends.</summary>
    private void ApplyWinVTakeover(bool takeOver)
    {
        try
        {
            _winV ??= new WinVTakeover(new WinVRegistry(), Path.Combine(ClipStore.DirectoryFor(Services.Paths), "winv-restore.json"));
            if (takeOver) _winV.Engage();
            else _winV.Restore();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            CrashLog.Write(ex);
        }
    }

    /// <summary>
    /// Opens the Clipboard Stack; while the Clipboard History is off, asks once per run whether to turn it on
    /// (afterwards <paramref name="window"/>, when given, shows "Clipboard History is off").
    /// Works before the background services started (--clipboard on a cold start). The Admin Instance has no Clipboard Stack.
    /// </summary>
    internal void ShowClipboardStack(MainWindow? window = null)
    {
        if (MainWindow.IsElevated)
        {
            window?.ShowToast("Clipboard History is off");
            return;
        }

        if (Services.Settings.Clipboard.Enabled)
        {
            OpenClipboardStack();
            return;
        }

        if (_clipboardAsked)
        {
            window?.ShowToast("Clipboard History is off");
            return;
        }

        _clipboardAsked = true;
        var answer = Native.MessageBoxW(0, "The Clipboard History is off. Turn it on to record what you copy?\n\nPollux then keeps running in the background.", "Pollux", Native.MB_YESNO | Native.MB_ICONQUESTION | Native.MB_SETFOREGROUND | Native.MB_TOPMOST);
        if (answer != Native.IDYES) return;
        Services.Settings.Clipboard.Enabled = true;
        Services.SaveSettings();
        // Finish what the hotkey or --clipboard asked for.
        OpenClipboardStack();
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
            if (args.Contains("--clipboard")) { ShowClipboardStack(); return; }
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
        var restored = false;
        if (restore && Services.Settings.App.RestoreWindows && Services.Session.Windows.Count > _windows.Count)
        {
            session = Services.Session.Windows[_windows.Count];
            restored = true;
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
            var tabs = session.Panes[session.ActivePane].Tabs;
            // A fresh window opened for a folder shows just that folder; a restored one keeps its tabs.
            if (!restored) tabs.Clear();
            tabs.Insert(0, new TabSession { Location = dir });
            session.Panes[session.ActivePane].ActiveTab = 0;
        }

        var w = new MainWindow(this, session);
        _windows.Add(w);
        w.Closed += (_, _) =>
        {
            _windows.Remove(w);
            if (_windows.Count == 0 && (!Services.Settings.KeepsResident || MainWindow.IsElevated) && !_quitting)
            {
                // Not resident: the last window ends the process. Undo hooks and the Win+V takeover like Quit does.
                _quitting = true;
                StopServices();
                StopCopilot();
                Exit();
            }
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
    internal bool KeepResident(MainWindow w) => !_quitting && Services.Settings.KeepsResident && !MainWindow.IsElevated;

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
        StopServices();
        foreach (var w in _windows.ToList()) w.Close();
        _settingsWindow?.Close();
        ExtraFeatures.Shutdown();
        StopCopilot();
        Exit();
    }

    /// <summary>Pollux exits: stop the CLI child. Bounded, so a hung CLI cannot keep Pollux alive.</summary>
    private void StopCopilot()
    {
        try
        {
            Task.Run(() => Copilot.DisposeAsync().AsTask()).Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException ex)
        {
            CrashLog.Write(ex);
        }
    }

    /// <summary>Hooks, Win+V takeover, clipboard capture and tray: everything that must not outlive the process.</summary>
    private void StopServices()
    {
        WinEHook.Disable();
        ClipboardService.Shutdown();
        // Only the main instance engages the Win+V takeover; the Admin Instance must not undo it.
        if (!MainWindow.IsElevated) ApplyWinVTakeover(false);
        _tray?.Dispose();
        _tray = null;
        Search?.Dispose();
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

    /// <summary>Palette commands of the Clipboard History work only while it is on (otherwise they toast "Clipboard History is off").</summary>
    private void WireClipboardCommands(bool on)
    {
        ClipboardCommands.ClearHistory = on ? ClipboardService.ClearHistory : null;
        ClipboardCommands.SetPaused = on ? ClipboardService.SetPaused : null;
    }

    private void OpenClipboardStack() => ExtraFeatures.ShowClipboardStack(this, ClipboardService.History, ClipboardService.ReadImage, ClipboardService.SaveSoon);

    internal void NoteCommand(string id)
    {
        RecentCommands.Remove(id);
        RecentCommands.Insert(0, id);
        if (RecentCommands.Count > 10) RecentCommands.RemoveAt(10);
    }

    internal IEnumerable<CommandDefinition> ExtraCommands() => ExtraFeatures.Commands;

    internal bool ExecuteAppCommand(string id, MainWindow window) => ExtraFeatures.Execute(this, id, window);
}
