using System.ComponentModel;
using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Pollux.App.Views;
using Pollux.Core.Clipboard;
using Pollux.Core.Files;
using Pollux.Core.Inbox;

namespace Pollux.App.Services;

/// <summary>Features beyond the core file manager: Downloads Stack, Notes, Snap, updates, Admin Instance, Inbox, Workspaces.</summary>
internal static class ExtraFeatures
{
    private static DownloadsStack? _downloads;
    private static ClipboardStack? _clipboardStack;
    private static NotesWindow? _notes;
    private static UpdatesWindow? _updates;

    /// <summary>The commands run here rather than by a window, by id (titles and default keys are in Core's <c>CommandCatalog</c>).</summary>
    private static readonly Dictionary<string, Action<App, MainWindow>> Handlers = new(StringComparer.Ordinal)
    {
        ["downloads.show"] = (app, _) => ShowDownloads(app),
        ["notes.show"] = (app, _) => ShowNotes(app, newNote: false),
        ["notes.new"] = (app, _) => ShowNotes(app, newNote: true),
        // Through the App: while the Clipboard History is off it asks once whether to turn it on.
        ["clipboard.show"] = (app, window) => app.ShowClipboardStack(window),
        ["clipboard.clear"] = (app, window) => WithClipboardHistory(app, window, s => s.ClearHistory()),
        ["clipboard.pause"] = (app, window) => WithClipboardHistory(app, window, s => s.SetPaused(true)),
        ["clipboard.resume"] = (app, window) => WithClipboardHistory(app, window, s => s.SetPaused(false)),
        // Through the App: while Snap is off it asks once whether to turn it on.
        ["snap.area"] = (app, window) => app.StartSnap(window),
        ["snap.window"] = (app, window) => app.StartSnap(window, SnapMode.Window),
        ["snap.screen"] = (app, window) => app.StartSnap(window, SnapMode.Screen),
        ["snap.repeat"] = (app, window) => app.StartSnap(window, SnapMode.Repeat),
        ["snap.areaAfterDelay"] = (app, window) => app.StartSnap(window, SnapMode.AreaAfterDelay),
        ["snap.copyText"] = (app, window) => app.StartSnap(window, SnapMode.CopyText),
        ["snap.openClipboardImage"] = (app, window) => app.StartSnap(window, SnapMode.ClipboardImage),
        ["snap.openHistory"] = (app, window) => SnapService.OpenHistory(app, window),
        ["snap.releaseFloats"] = (_, _) => SnapService.ReleaseFloats(),
        ["updates.show"] = (app, _) => ShowUpdates(app),
        ["app.restartAsAdmin"] = (_, window) => OpenAdminInstance(window),
        ["inbox.open"] = (_, window) => window.Execute("nav.desktop"),
        ["inbox.toggleDesktopIcons"] = ToggleDesktopIcons,
        ["inbox.sort"] = (app, window) => _ = SortInboxAsync(app, window),
    };

    public static void ApplySettings(App app)
    {
        if (MainWindow.IsElevated) return;
        if (app.Services.Settings.Inbox.HideDesktopIcons) DesktopIcons.SetVisible(false);
        UpdatesWindow.Schedule(app);
    }

    public static void Shutdown()
    {
        _downloads?.Close();
        _clipboardStack?.Close();
        ClipboardPasteBack.Shutdown();
        SnapService.Shutdown();
        _notes?.SaveNow();
        _notes?.Close();
        _updates?.Close();
    }

    public static void ShowDownloads(App app)
    {
        _downloads ??= new DownloadsStack(app);
        _downloads.Toggle();
    }

    /// <summary>
    /// The Clipboard Stack over <paramref name="history"/>, created once and kept hidden between uses.
    /// <paramref name="readImage"/> loads an image Clip's bytes; <paramref name="changed"/> runs after the Stack changed the history (save it).
    /// </summary>
    public static ClipboardStack GetClipboardStack(App app, ClipboardHistory history, Func<Clip, byte[]?> readImage, Action changed)
    {
        if (_clipboardStack is null)
        {
            _clipboardStack = new ClipboardStack(app, history, readImage, changed);
            _clipboardStack.PasteRequested += (clip, onlyCopy) => _ = ClipboardPasteBack.PasteAsync(clip, onlyCopy, history, readImage, changed);
        }

        return _clipboardStack;
    }

    /// <summary>Opens the Clipboard Stack, or closes it when it is open. Remembers the active window first: Enter pastes there.</summary>
    public static void ShowClipboardStack(App app, ClipboardHistory history, Func<Clip, byte[]?> readImage, Action changed)
    {
        var stack = GetClipboardStack(app, history, readImage, changed);
        if (!stack.IsOpen) ClipboardPasteBack.RememberTarget();
        stack.Toggle();
    }

    /// <summary>Re-renders an open Clipboard Stack after a new Clip arrived.</summary>
    public static void RefreshClipboardStack() => _clipboardStack?.Refresh();

    public static void ShowNotes(App app, bool newNote)
    {
        if (_notes is null)
        {
            _notes = new NotesWindow(app);
            _notes.Closed += (_, _) => _notes = null;
        }

        _notes.ShowNotes(newNote);
    }

    public static void ShowUpdates(App app)
    {
        if (_updates is null)
        {
            _updates = new UpdatesWindow(app);
            _updates.Closed += (_, _) => _updates = null;
        }

        _updates.ShowWindow();
    }

    /// <summary>Runs <paramref name="id"/> when it is one of the commands above; false for any other id.</summary>
    public static bool Execute(App app, string id, MainWindow window)
    {
        if (!Handlers.TryGetValue(id, out var run)) return false;
        run(app, window);
        return true;
    }

    /// <summary>Clipboard History commands work only while it is on; otherwise they toast "Clipboard History is off".</summary>
    private static void WithClipboardHistory(App app, MainWindow window, Action<ClipboardService> action)
    {
        if (app.ClipboardCommandsOn) action(app.ClipboardService);
        else window.ShowToast("Clipboard History is off");
    }

    private static void ToggleDesktopIcons(App app, MainWindow window)
    {
        var visible = DesktopIcons.AreVisible();
        if (visible is null) { window.ShowToast("Desktop not found"); return; }
        DesktopIcons.SetVisible(!visible.Value);
        app.Services.Settings.Inbox.HideDesktopIcons = visible.Value;
        app.Services.SaveSettings();
        window.ShowToast(visible.Value ? "Desktop icons hidden — Inbox: Ctrl+Shift+P › Inbox" : "Desktop icons visible");
    }

    /// <summary>Starts an elevated Pollux next to the normal one (UAC / Delinea prompt). The normal instance keeps running.</summary>
    private static void OpenAdminInstance(MainWindow window)
    {
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!, Shell.Quote(window.CurrentFolder))
            {
                UseShellExecute = true,
                Verb = "runas",
            });
        }
        catch (Win32Exception)
        {
            window.ShowToast("Elevation was cancelled.");
        }
    }

    public static string InboxTarget(App app) =>
        string.IsNullOrWhiteSpace(app.Services.Settings.Inbox.SortTarget)
            ? Path.Combine(KnownFolders.Documents, "Inbox")
            : app.Services.Settings.Inbox.SortTarget;

    private static async Task SortInboxAsync(App app, MainWindow window)
    {
        var target = InboxTarget(app);
        var plan = InboxSorter.Plan(DirectoryReader.Enumerate(KnownFolders.Desktop), target);
        if (plan.Count == 0)
        {
            window.ShowToast("Inbox is empty — nothing to sort.");
            return;
        }

        var text = new TextBlock { FontFamily = Ui.Mono, FontSize = 12, TextWrapping = TextWrapping.Wrap };
        text.Text = string.Join(Environment.NewLine, plan.GroupBy(p => p.Category).Select(g => $"{g.Key} ({g.Count()}): {string.Join(", ", g.Take(6).Select(m => Path.GetFileName(m.Source)))}{(g.Count() > 6 ? ", …" : "")}"));
        var content = new StackPanel { Spacing = 10 };
        content.Children.Add(new TextBlock { Text = $"Move {plan.Count} item(s) from the Desktop into {target}\\<category>? Shortcuts and folders stay. Undo with Ctrl+Z in Explorer.", TextWrapping = TextWrapping.Wrap });
        content.Children.Add(new ScrollViewer { Content = text, MaxHeight = 300 });
        var dialog = new ContentDialog
        {
            Title = "Sort Inbox",
            Content = content,
            PrimaryButtonText = "Move",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = window.Content.XamlRoot,
            RequestedTheme = Theme.Current,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        foreach (var group in plan.GroupBy(p => p.TargetDirectory))
        {
            Directory.CreateDirectory(group.Key);
            Shell.Move(window.Hwnd, group.Select(m => m.Source).ToList(), group.Key);
        }

        window.ShowToast($"Sorted {plan.Count} item(s) into {target}");
    }
}
