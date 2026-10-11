using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Pollux.App.Services;
using Pollux.Copilot.Chat;
using Pollux.Copilot.Connection;
using Pollux.Copilot.Context;
using Pollux.Copilot.Models;
using Pollux.Copilot.Panel;
using Pollux.Copilot.Tools;

namespace Pollux.App.Views;

/// <summary>
/// The Copilot Panel of one window: the Panel page (copilot.html) in WebView2 behind the JSON bridge of
/// <see cref="PanelMessages"/> (ADR 0004, ADR 0005). Created when the Panel is first opened, never at startup.
/// Holds one open Chat (<see cref="CopilotChat"/>) and offers the global Chat list; the SDK session of a Chat is opened with
/// its first message, or resumed when it is picked from the list.
/// </summary>
internal sealed class CopilotPanel : IDisposable
{
    private readonly WebView2 _web = new();
    private readonly DispatcherQueue _ui;
    private readonly PanelChannel _channel;
    private readonly CopilotChat _chat;
    private readonly Action _close;
    private readonly Func<ContextSnapshot> _context;
    private readonly ContextRegistry _chips = new();
    private readonly App _app;
    private CancellationTokenSource? _check;
    private string? _activeFolder; // read by the Chat on SDK threads; Pane state may only be read on the UI thread
    private CoreWebView2? _core;
    private bool _disposed;

    private readonly IPolluxActions _actions;
    private readonly Func<Task<string?>> _pickFolder;

    /// <param name="pickFolder">Shows a folder dialog for the Working Folder; null when cancelled.</param>
    public CopilotPanel(Border host, App app, Action close, Func<ContextSnapshot> context, IPolluxActions actions, Func<Task<string?>> pickFolder)
    {
        _pickFolder = pickFolder;
        _context = context;
        _actions = actions;
        _app = app;
        _close = close;
        _ui = DispatcherQueue.GetForCurrentThread();
        _activeFolder = context().Active.Folder;
        _channel = new PanelChannel(m => _core?.PostWebMessageAsString(m));
        // Chat messages arrive on SDK threads; WebView2 must be called on the UI thread.
        _chat = new CopilotChat(app.CopilotChats, OpenChatAsync, DeleteChatAsync, Post, ct => _app.CopilotQuotaState.RefreshAsync(ct),
            defaultModes: () => ChatModes.Of(app.Services.Settings.Copilot),
            activeFolder: () => Volatile.Read(ref _activeFolder), // a new Chat works in the active Pane's folder
            guardRules: () => app.Services.Settings.Copilot.GuardRules,
            actionLogs: app.CopilotActionLogs);
        host.Child = _web;
        _ = InitializeAsync();
    }

    /// <summary>Sends a Panel message from any thread (WebView2 must be called on the UI thread).</summary>
    private void Post(string message) => _ui.TryEnqueue(() => { if (!_disposed) _channel.Send(message); });

    private async Task InitializeAsync()
    {
        _core = await WebHost.InitializeAsync(_web, "copilot.html", Pollux.Core.Files.WebViewKind.CopilotPanel);
        if (_disposed) return;
        _core.WebMessageReceived += (_, e) => OnMessage(WebHost.OwnMessageText(e, Pollux.Core.Files.WebViewKind.CopilotPanel), e);
        // Queued until the page reports "ready".
        SetTheme();
        _chat.PublishHeader();
        _chat.PublishChats();
        _chat.PublishModes();
        RefreshContext();
        _ = CheckAsync();
    }

    /// <summary>Starts the CLI on first use, asks who is signed in and shows the matching state (or the Chat).</summary>
    private async Task CheckAsync()
    {
        _check?.Cancel();
        var cts = _check = new CancellationTokenSource();
        ShowState(new PanelStateView("Checking", "Connecting to Copilot", "Starting the Copilot CLI and checking your sign-in."));
        try
        {
            var state = await Task.Run(() => _app.CopilotMonitor.CheckAsync(cts.Token));
            if (_disposed || cts.IsCancellationRequested) return;
            var view = state.ToView();
            ShowState(view);
            if (view.Kind.Length == 0) _ = Task.Run(() => LoadModelsAndQuotaAsync(cts.Token)); // ready: Chat is shown
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Fills the model picker from the CLI and shows the remaining premium requests.</summary>
    private async Task LoadModelsAndQuotaAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (await _app.Copilot.ConnectAsync(cancellationToken).ConfigureAwait(false) is ICopilotCatalog catalog)
            {
                Post(PanelMessages.Models(await catalog.ListModelsAsync(cancellationToken).ConfigureAwait(false)));
                Post(PanelMessages.Model(_chat.Model));
            }
        }
#pragma warning disable CA1031 // Without a model list the Chat still works with the CLI's default model.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
        }

        await _chat.RefreshQuotaAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Opens the CLI side of the Chat on the shared Copilot CLI (started on first use).</summary>
    private async Task<IChatSession> OpenChatAsync(ChatRecord chat, bool existing, ToolApprovals approvals, CancellationToken cancellationToken)
    {
        var source = await ChatSourceAsync(cancellationToken).ConfigureAwait(false);
        return await source.OpenChatAsync(chat, existing, approvals, _actions, cancellationToken).ConfigureAwait(false);
    }

    private async Task DeleteChatAsync(string chatId, CancellationToken cancellationToken)
    {
        var source = await ChatSourceAsync(cancellationToken).ConfigureAwait(false);
        await source.DeleteChatAsync(chatId, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ICopilotChatSource> ChatSourceAsync(CancellationToken cancellationToken)
    {
        var connection = await _app.Copilot.ConnectAsync(cancellationToken).ConfigureAwait(false);
        return connection as ICopilotChatSource
            ?? throw new InvalidOperationException(_app.Copilot.State.Reason ?? "Copilot is not available.");
    }

    private void OnAction(CopilotPanelAction action)
    {
        switch (action)
        {
            case CopilotPanelAction.SignIn:
                if (Shell.OpenCopilotLogin(_app.CurrentCopilotCli))
                    ShowState(new PanelStateView("SigningIn", "Signing in", "Finish the sign-in in the terminal window, then check again.", CopilotPanelAction.Recheck));
                else
                    _ = CheckAsync();
                break;
            case CopilotPanelAction.OpenSettings: _app.ShowSettings(); break;
            default: _ = CheckAsync(); break; // Retry, Recheck
        }
    }

    public void SetTheme() => _channel.Send(PanelMessages.Theme(Theme.Current == ElementTheme.Dark));

    /// <summary>Shows a connection or sign-in state in place of the Chat (ticket 05); <see cref="PanelStateView.None"/> shows the Chat.</summary>
    public void ShowState(PanelStateView view) => _channel.Send(PanelMessages.State(view));

    /// <summary>Shows the active Pane's location and selection as the chips of the next message (pushed when the Pane changes).</summary>
    public void RefreshContext()
    {
        if (_disposed) return;
        var context = _context();
        Volatile.Write(ref _activeFolder, context.Active.Folder);
        var chips = ContextChips.Defaults(context);
        _chips.Issue(chips);
        _channel.Send(PanelMessages.Context(chips));
        _chat.PublishHeader(); // a Chat not started yet follows the active Pane's folder
    }

    /// <summary>Selection actions: a new Chat starts with <paramref name="prompt"/> and the selection as context.</summary>
    public void StartChat(string prompt, IReadOnlyList<ContextChip> chips)
    {
        if (_disposed) return;
        _chips.Issue(chips);
        _ = RunAsync(() => _chat.StartNewAsync(prompt, chips, CancellationToken.None));
    }

    private void OnMentionQuery(string query)
    {
        var chips = ContextChips.Mentions(_context(), query);
        _chips.Issue(chips);
        _channel.Send(PanelMessages.Mentions(query, chips));
    }

    /// <summary>Entries dragged from a Pane into the Panel become chips of the next message.</summary>
    private void OnDrop(CoreWebView2WebMessageReceivedEventArgs e)
    {
        var paths = new List<string>();
        var objects = e.AdditionalObjects;
        if (objects is not null)
            foreach (var o in objects)
                if (o is CoreWebView2File f) paths.Add(f.Path);
        if (paths.Count == 0) return;
        var chips = ContextChips.FromPaths(paths);
        _chips.Issue(chips);
        _channel.Send(PanelMessages.ChipsAdded(chips));
    }

    /// <summary>
    /// <paramref name="json"/> is null unless copilot.html posted it (S-16); that check also guards the dropped file
    /// paths of <see cref="OnDrop"/>. PanelMessages parses the content defensively.
    /// </summary>
    private void OnMessage(string? json, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (json is null) return;
        switch (_channel.Receive(json))
        {
            case PanelEvent.Close: _close(); break;
            case PanelEvent.Action a: OnAction(a.Value); break;
            case PanelEvent.OpenLink link: Shell.OpenUrl(link.Url); break; // never navigated inside the page
            case PanelEvent.Send s:
                var sent = _chips.Resolve(s.ChipIds); // only chips Pollux showed; the page names them by id
                _ = RunAsync(() => _chat.SendAsync(s.Text, sent, CancellationToken.None));
                break;
            case PanelEvent.Mention m: OnMentionQuery(m.Query); break;
            case PanelEvent.ContextRequest: RefreshContext(); break;
            case PanelEvent.Drop: OnDrop(e); break;
            case PanelEvent.Cancel: _ = RunAsync(() => _chat.CancelAsync(CancellationToken.None)); break;
            case PanelEvent.Approve a: _chat.Answer(a.Id, a.Answer); break;
            case PanelEvent.ActionLogRequest: _chat.ShowActionLog(); break;
            case PanelEvent.UseActiveFolder:
                var folder = _context().Active.Folder;
                _ = RunAsync(() => _chat.SetWorkingFolderAsync(folder));
                break;
            case PanelEvent.ChooseWorkingFolder: _ = ChooseWorkingFolderAsync(); break;
            case PanelEvent.ClearTainted: _chat.ClearTainted(); break;
            case PanelEvent.SetAgentMode m: _ = RunAsync(() => _chat.SetAgentModeAsync(m.Mode, CancellationToken.None)); break;
            case PanelEvent.SetPermissionMode m: _ = RunAsync(() => _chat.SetPermissionModeAsync(m.Mode, CancellationToken.None)); break;
            case PanelEvent.SetSandbox m: _ = RunAsync(() => _chat.SetSandboxAsync(m.Enabled, CancellationToken.None)); break;
            case PanelEvent.NewChat: _ = RunAsync(() => _chat.NewChatAsync()); break;
            case PanelEvent.OpenChat o: _ = RunAsync(() => _chat.OpenAsync(o.Id, CancellationToken.None)); break;
            case PanelEvent.DeleteChat d: _ = RunAsync(() => _chat.DeleteAsync(d.Id, CancellationToken.None)); break;
            case PanelEvent.SelectModel m: _chat.SelectModel(m.Id); break;
        }
    }

    /// <summary>
    /// Runs a Chat call off the UI thread. An exception the call does not handle itself (e.g. one thrown before its own
    /// try/catch) shows in the Chat and goes to errors.log instead of being lost with the discarded task.
    /// </summary>
    private async Task RunAsync(Func<Task> work)
    {
        try
        {
            await Task.Run(work).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Any failure of a fire-and-forget Chat call is shown, never swallowed.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            CrashLog.Write(ex);
            Post(PanelMessages.ChatError(ex.Message));
        }
    }

    /// <summary>Lets the user pick the Chat's Working Folder in a folder dialog.</summary>
    private async Task ChooseWorkingFolderAsync()
    {
        string? folder;
        try
        {
            folder = await _pickFolder();
        }
#pragma warning disable CA1031 // A dialog that cannot be shown leaves the Working Folder as it is.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Post(PanelMessages.ChatError($"Could not open the folder dialog: {ex.Message}"));
            return;
        }

        if (string.IsNullOrEmpty(folder) || _disposed) return;
        _ = RunAsync(() => _chat.SetWorkingFolderAsync(folder));
    }

    public void Dispose()
    {
        _disposed = true;
        _check?.Cancel();
        _ = Task.Run(() => _chat.DisposeAsync().AsTask()); // rejects open tool prompts, releases the session
        _web.Close();
    }
}
