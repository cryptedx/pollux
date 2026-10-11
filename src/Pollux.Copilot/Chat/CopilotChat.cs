using System.Globalization;
using Pollux.Copilot.Context;
using Pollux.Copilot.Models;
using Pollux.Copilot.Panel;
using Pollux.Core.Settings;

namespace Pollux.Copilot.Chat;

/// <summary>Opens the CLI side of a Chat: creates its SDK session, or resumes it (<paramref name="existing"/>) with the Chat's own id.</summary>
public delegate Task<IChatSession> ChatOpener(ChatRecord chat, bool existing, ToolApprovals approvals, CancellationToken cancellationToken);

/// <summary>
/// The Chat of a Copilot Panel and the list of all Chats (global, kept in a <see cref="ChatStore"/>): the user's messages
/// go to the CLI, the streamed answer, tool activity, errors and tool approval prompts come back as Panel messages
/// (<see cref="PanelMessages"/>). A Chat is recorded with its first message, its SDK session opens with that message
/// (resumes after a restart or a CLI failure) and is dropped when another Chat is opened. Each Chat has its own Agent
/// Mode and Permission Mode (kept with it, applied to the CLI whenever its session opens; the CLI's own modes, so an
/// organization policy applies and a mode it blocks cannot be selected). UI-free; <paramref name="post"/> may be called on any thread.
/// </summary>
/// <param name="store">The persisted Chat list.</param>
/// <param name="open">Opens the CLI side of a Chat (with the Chat's model).</param>
/// <param name="deleteInCli">Deletes a Chat's session data in the CLI (best effort).</param>
/// <param name="post">Sends a Panel message to the page.</param>
/// <param name="refreshQuota">Asks the CLI for the premium-request quota; called after every answer.</param>
/// <param name="now">The clock; defaults to the system clock.</param>
/// <param name="defaultModes">The modes a new Chat starts with (settings); defaults to Interactive + Manual.</param>
/// <param name="activeFolder">The active Pane's folder (null on Home): a new Chat's Working Folder unless the user picks one.</param>
/// <param name="guardRules">The user's Guard Rules extensions (<c>copilot.guardRules</c>), read when a Chat's session opens.</param>
/// <param name="environment">Resolves USERPROFILE, %VARS% etc. for the Guard Rules; null uses the process environment.</param>
/// <param name="actionLogs">The Action Logs of the Chats (every tool call, kept next to the Chat); null keeps none.</param>
public sealed class CopilotChat(
    ChatStore store,
    ChatOpener open,
    Func<string, CancellationToken, Task> deleteInCli,
    Action<string> post,
    Func<CancellationToken, Task<CopilotQuota?>>? refreshQuota = null,
    Func<DateTimeOffset>? now = null,
    Func<ChatModes>? defaultModes = null,
    Func<string?>? activeFolder = null,
    Func<GuardRuleSettings>? guardRules = null,
    Func<string, string?>? environment = null,
    ActionLogStore? actionLogs = null) : IAsyncDisposable
{
    private const int TitleLength = 60;

    /// <summary>Shown in the Copilot Panel when a tool ran without the Guard Rules hook having seen it.</summary>
    public const string HookDidNotRunWarning = "Guard Rules hook did not run; switched to Manual";

    /// <summary>Shown when a prompt is refused in Allow All or Autopilot because the CLI did not confirm the sandbox state.</summary>
    public const string SandboxUnconfirmed =
        "Not sent: the sandbox state could not be confirmed. Allow All and Autopilot need it; send again to retry, or switch to Manual or Assisted in Interactive mode.";

    /// <summary>Shown when a prompt is refused in Allow All or Autopilot because the organization's Copilot policy could not be read.</summary>
    public const string ModePolicyUnconfirmed =
        "Not sent: your organization's Copilot policy could not be checked. Allow All and Autopilot need it; send again to retry, or switch to Manual or Assisted in Interactive mode.";

    private readonly Lock _gate = new();
    private readonly Func<DateTimeOffset> _now = now ?? (() => DateTimeOffset.UtcNow);
    private readonly Func<ChatModes> _defaultModes = defaultModes ?? (() => ChatModes.Default);
    private ChatModes? _modes;            // of the current Chat; null until first asked (then the defaults)
    private ModePolicy _policy = ModePolicy.None;
    private bool _policyRead;             // the open session's CLI told the organization's policy (false: _policy is only a guess)
    private SandboxPolicy _sandboxPolicy = SandboxPolicy.None;
    private bool? _sandboxChoice;         // the user's choice for the current Chat; null = the default for its modes
    private bool? _sandboxApplied;        // what the open session's CLI was last told
    private ToolApprovals? _approvals;
    private IChatSession? _session;
    private IDisposable? _subscription;
    private ChatRecord? _record;
    private bool _onDisk;
    private string? _model;        // chosen for the next message; null = the CLI's default model
    private string? _sessionModel; // what the open session uses
    private bool _busy;
    private bool _folderChosen;    // the user picked the Working Folder of the Chat not created yet
    private string? _chosenFolder;
    private int _nextMessage;

    /// <summary>Copilot is answering (or a Chat is being opened); new messages are refused until it is idle.</summary>
    public bool IsBusy { get { lock (_gate) return _busy; } }

    /// <summary>The id of the open Chat; null while it is a new one that has not been sent yet.</summary>
    public string? CurrentId { get { lock (_gate) return _record?.Id; } }

    /// <summary>Sends the Action Log of the open Chat to the page (empty for a Chat not started yet).</summary>
    public void ShowActionLog()
    {
        var id = CurrentId;
        post(PanelMessages.ActionLog(id is null || actionLogs is null ? [] : actionLogs.Open(id).Entries));
    }

    /// <summary>Sends the Chat list to the page.</summary>
    public void PublishChats()
    {
        string? current;
        lock (_gate) current = _record?.Id;
        post(PanelMessages.Chats(store.List(), current));
    }

    /// <summary>
    /// The current Chat's Working Folder: its own once it exists; before that the one the user picked, or the active
    /// Pane's folder (which it becomes when the first message is sent).
    /// </summary>
    public string? WorkingFolder
    {
        get
        {
            lock (_gate)
            {
                if (_record is not null) return _record.WorkingFolder;
                if (_folderChosen) return _chosenFolder;
            }

            return activeFolder?.Invoke();
        }
    }

    /// <summary>Sends the header (Working Folder, Tainted marker) to the page.</summary>
    public void PublishHeader()
    {
        bool tainted;
        string? taintedBy;
        lock (_gate)
        {
            tainted = _record?.Tainted ?? false;
            taintedBy = _record?.TaintedBy;
        }

        post(PanelMessages.Header(HeaderOf(WorkingFolder, tainted, taintedBy)));
    }

    private static PanelHeader HeaderOf(string? workingFolder, bool tainted, string? taintedBy) =>
        PanelHeader.Placeholder with { WorkingFolder = workingFolder, Tainted = tainted, TaintedBy = tainted ? taintedBy : null };

    /// <summary>The current Chat is a Tainted Chat: it asks before shell, network and write actions in every Permission Mode.</summary>
    public bool Tainted { get { lock (_gate) return _record?.Tainted ?? false; } }

    /// <summary>The user cleared the Tainted mark of the current Chat: Guard Rules and the Permission Mode apply as before it read untrusted content.</summary>
    public void ClearTainted()
    {
        ChatRecord? saved;
        lock (_gate)
        {
            if (_record is not { Tainted: true } record) return;
            record.Tainted = false;
            record.TaintedBy = null;
            saved = _onDisk ? record : null;
        }

        if (saved is not null) store.Save(saved);
        PublishHeader();
    }

    /// <summary>A tool call of <paramref name="chat"/> read untrusted content: the Chat is Tainted (kept with it) and the header shows it.</summary>
    private void MarkTainted(ChatRecord chat, string reason)
    {
        bool current;
        ChatRecord? saved;
        lock (_gate)
        {
            if (chat.Tainted) return;
            chat.Tainted = true;
            chat.TaintedBy = reason;
            current = ReferenceEquals(_record, chat);
            saved = current && _onDisk ? chat : null;
        }

        if (saved is not null) store.Save(saved);
        if (current) PublishHeader();
    }

    /// <summary>
    /// Changes the current Chat's Working Folder (null: none, every write asks). A started Chat keeps it and its CLI
    /// session is resumed there with the next message. False while Copilot is answering.
    /// </summary>
    public async Task<bool> SetWorkingFolderAsync(string? folder)
    {
        if (!TryBeginSwitch())
        {
            PublishHeader(); // the page shows the folder that stays
            return false;
        }

        IChatSession? session;
        ChatRecord? saved = null;
        lock (_gate)
        {
            session = _session;
            if (_record is null)
            {
                _folderChosen = true;
                _chosenFolder = folder;
            }
            else
            {
                _record.WorkingFolder = folder;
                if (_onDisk) saved = _record;
            }
        }

        if (saved is not null) store.Save(saved);
        if (session is not null) await DropSessionAsync(session).ConfigureAwait(false);
        EndSwitch();
        PublishHeader();
        if (saved is not null) PublishChats();
        return true;
    }

    /// <summary>The Agent Mode and Permission Mode of the current Chat.</summary>
    public ChatModes Modes { get { lock (_gate) return CurrentModes(); } }

    /// <summary>Sends the current Chat's modes, and the Permission Modes the organization's policy blocks, to the page.</summary>
    public void PublishModes()
    {
        ChatModes modes;
        ModePolicy policy;
        lock (_gate)
        {
            modes = CurrentModes();
            policy = _policy;
        }

        post(PanelMessages.Modes(modes, policy));
        post(PanelMessages.Sandbox(Sandbox));
    }

    /// <summary>The state of the current Chat's sandbox toggle.</summary>
    public SandboxState Sandbox { get { lock (_gate) return _sandboxPolicy.Resolve(CurrentModes(), _sandboxChoice); } }

    /// <summary>
    /// The user switched the sandbox of the current Chat (kept with the Chat; in the CLI once its session is open).
    /// False when the toggle is locked (organization policy, unsupported build) or the CLI could not switch.
    /// </summary>
    public async Task<bool> SetSandboxAsync(bool enabled, CancellationToken cancellationToken)
    {
        IChatSession? session;
        bool? previous;
        lock (_gate)
        {
            if (_sandboxPolicy.Resolve(CurrentModes(), _sandboxChoice).Locked)
            {
                session = null;
                previous = null;
            }
            else
            {
                session = _session;
                previous = _sandboxChoice;
                _sandboxChoice = enabled;
            }
        }

        if (session is null && Sandbox.Locked)
        {
            PublishModes(); // the page puts its toggle back
            return false;
        }

        if (session is not null && !await ApplySandboxAsync(session, cancellationToken).ConfigureAwait(false))
        {
            lock (_gate) _sandboxChoice = previous;
            PublishModes();
            return false;
        }

        RememberSandbox();
        return true;
    }

    /// <summary>
    /// Tells the CLI the sandbox state the Chat has now (unless that is already what it was told or the toggle is locked: the
    /// CLI enforces an organization's sandbox itself and a host without sandbox has nothing to switch). False when the CLI refused (the Copilot Panel shows why).
    /// </summary>
    private async Task<bool> ApplySandboxAsync(IChatSession session, CancellationToken cancellationToken)
    {
        SandboxState state;
        lock (_gate)
        {
            state = _sandboxPolicy.Resolve(CurrentModes(), _sandboxChoice);
            if (state.Locked || _sandboxApplied == state.Enabled) return true;
        }

        try
        {
            await session.SetSandboxAsync(state.Enabled, cancellationToken).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // A sandbox the CLI cannot switch is shown; the Chat keeps its state.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            post(PanelMessages.ChatError($"Could not switch the sandbox {(state.Enabled ? "on" : "off")}: {ex.Message}"));
            return false;
        }

        lock (_gate) _sandboxApplied = state.Enabled;
        return true;
    }

    /// <summary>Keeps the user's sandbox choice with the Chat (once it is in the list) and shows the toggle.</summary>
    private void RememberSandbox()
    {
        ChatRecord? record;
        lock (_gate)
        {
            record = _onDisk ? _record : null;
            if (record is not null) record.Sandbox = _sandboxChoice;
        }

        if (record is not null) store.Save(record);
        PublishModes();
    }

    /// <summary>Switches the current Chat's Agent Mode (in the CLI once its session is open). False when the CLI could not switch (the Copilot Panel shows why).</summary>
    public async Task<bool> SetAgentModeAsync(CopilotAgentMode mode, CancellationToken cancellationToken)
    {
        IChatSession? session;
        lock (_gate) session = _session;
        if (session is not null)
        {
            try
            {
                await session.SetAgentModeAsync(mode, cancellationToken).ConfigureAwait(false);
            }
#pragma warning disable CA1031 // A mode the CLI cannot switch to is shown; the Chat keeps its mode.
            catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
            {
                post(PanelMessages.ChatError($"Could not switch to {ChatModes.Label(mode)}: {ex.Message}"));
                PublishModes();
                return false;
            }
        }

        Remember(m => m with { Agent = mode });
        if (session is not null) await ApplySandboxAsync(session, cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Switches the current Chat's Permission Mode (in the CLI once its session is open). False when the organization's
    /// policy blocks it or the CLI refuses it (the mode is then shown as blocked and the Chat keeps the mode the CLI reports).
    /// </summary>
    public async Task<bool> SetPermissionModeAsync(CopilotPermissionMode mode, CancellationToken cancellationToken)
    {
        IChatSession? session;
        bool blocked;
        lock (_gate)
        {
            session = _session;
            blocked = _policy.BlockedReason(mode) is not null;
        }

        if (blocked)
        {
            PublishModes(); // the page puts its switcher back
            return false;
        }

        if (session is null)
        {
            Remember(m => m with { Permission = mode });
            return true;
        }

        PermissionModeResult result;
        try
        {
            result = await session.SetPermissionModeAsync(mode, cancellationToken).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // A mode the CLI cannot switch to is shown; the Chat keeps its mode.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            post(PanelMessages.ChatError($"Could not switch to {ChatModes.Label(mode)}: {ex.Message}"));
            PublishModes();
            return false;
        }

        if (!result.Applied) Refuse(mode, ModePolicy.RefusedByCli);
        Remember(m => m with { Permission = result.Mode });
        await ApplySandboxAsync(session, cancellationToken).ConfigureAwait(false);
        return result.Applied;
    }

    /// <summary>The model the next message uses; null is the CLI's default model.</summary>
    public string? Model { get { lock (_gate) return _model; } }

    /// <summary>
    /// Chooses the model for the next message (an answer that is running keeps its model). Once the Chat has started it
    /// cannot go back to the CLI's default model: false, and the Copilot Panel is told the model that stays.
    /// </summary>
    public bool SelectModel(string? modelId)
    {
        bool accepted;
        lock (_gate)
        {
            accepted = modelId is not null || _session is null || _sessionModel is null;
            if (accepted) _model = modelId;
        }

        post(PanelMessages.Model(Model));
        return accepted;
    }

    /// <summary>Sends a user message. False when Copilot is still answering or the message could not be sent (the Copilot Panel shows why).</summary>
    public Task<bool> SendAsync(string text, CancellationToken cancellationToken) => SendAsync(text, [], cancellationToken);

    /// <summary>
    /// Sends a user message with its context chips: the Copilot Panel shows the message with exactly these chips and the CLI
    /// gets the text plus exactly these chips (<see cref="ContextPrompt"/>).
    /// </summary>
    public async Task<bool> SendAsync(string text, IReadOnlyList<ContextChip> chips, CancellationToken cancellationToken)
    {
        ChatRecord record;
        bool existing;
        lock (_gate)
        {
            if (_busy) return false;
            _busy = true;
            var stamp = _now();
            var modes = CurrentModes();
            _record ??= new ChatRecord
            {
                Id = "pollux-" + Guid.NewGuid().ToString("N"), Title = TitleOf(text), Created = stamp, Modified = stamp,
                AgentMode = modes.Agent, PermissionMode = modes.Permission, Sandbox = _sandboxChoice,
                WorkingFolder = _folderChosen ? _chosenFolder : activeFolder?.Invoke(),
            };
            record = _record;
            record.Model = _model;
            existing = _onDisk;
        }

        post(PanelMessages.UserMessage("u" + Interlocked.Increment(ref _nextMessage).ToString(CultureInfo.InvariantCulture), text, chips));
        post(PanelMessages.Busy(true));
        IChatSession? session = null;
        try
        {
            session = await SessionAsync(record, existing, cancellationToken).ConfigureAwait(false);
            if (!await ApplyModelAsync(session, cancellationToken).ConfigureAwait(false)) return false;
            if (await UnconfirmedSafetyAsync(session, cancellationToken).ConfigureAwait(false) is { } refused)
            {
                post(PanelMessages.ChatError(refused));
                SetIdle();
                return false;
            }

            await session.SendAsync(ContextPrompt.Compose(text, chips), cancellationToken).ConfigureAwait(false);
            lock (_gate) _onDisk = true;
            record.Model = Model;
            record.Modified = _now();
            store.Save(record); // the Chat exists once its first message is sent
            PublishChats();
            return true;
        }
#pragma warning disable CA1031 // Any failure to reach the CLI is shown in the Chat; the next message tries again.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            if (session is not null) await DropSessionAsync(session).ConfigureAwait(false);
            post(PanelMessages.ChatError(ex.Message));
            SetIdle();
            return false;
        }
    }

    /// <summary>
    /// Starts a new Chat (selection actions): the running one is dropped, the page is cleared and <paramref name="text"/>
    /// goes to a fresh session with <paramref name="chips"/>.
    /// </summary>
    public async Task<bool> StartNewAsync(string text, IReadOnlyList<ContextChip> chips, CancellationToken cancellationToken)
    {
        if (IsBusy) await CancelAsync(cancellationToken).ConfigureAwait(false); // the running answer is dropped with its Chat
        lock (_gate) _busy = false;
        await NewChatAsync().ConfigureAwait(false);
        return await SendAsync(text, chips, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Stops the running answer: open tool prompts are rejected and the CLI aborts the turn.</summary>
    public async Task CancelAsync(CancellationToken cancellationToken)
    {
        IChatSession? session;
        ToolApprovals? approvals;
        lock (_gate)
        {
            session = _session;
            approvals = _approvals;
        }

        approvals?.RejectPending();
        if (session is null) return;
        try
        {
            await session.AbortAsync(cancellationToken).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // A CLI that cannot abort is gone: drop the session so the Chat is usable again.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            await DropSessionAsync(session).ConfigureAwait(false);
            post(PanelMessages.ChatError(ex.Message));
            SetIdle();
        }
    }

    /// <summary>Leaves the current Chat (it stays in the list) for an empty one; its session is created with the first message. False while Copilot is answering.</summary>
    public async Task<bool> NewChatAsync()
    {
        if (!TryBeginSwitch()) return false;
        await LeaveCurrentAsync().ConfigureAwait(false);
        post(PanelMessages.Clear());
        EndSwitch();
        PublishChats();
        PublishModes();
        PublishHeader();
        return true;
    }

    /// <summary>
    /// Opens a Chat from the list: resumes its session (model, Working Folder, Agent Mode and Permission Mode as recorded) and shows its history.
    /// False while Copilot is answering, for an unknown id, or when the CLI cannot resume it (the Copilot Panel shows why).
    /// </summary>
    public async Task<bool> OpenAsync(string chatId, CancellationToken cancellationToken)
    {
        var chat = store.Get(chatId);
        if (chat is null || !TryBeginSwitch()) return false;
        await LeaveCurrentAsync().ConfigureAwait(false);
        post(PanelMessages.Clear());
        post(PanelMessages.Header(HeaderOf(chat.WorkingFolder, chat.Tainted, chat.TaintedBy)));
        lock (_gate)
        {
            _modes = new ChatModes(chat.AgentMode, chat.PermissionMode);
            _sandboxChoice = chat.Sandbox;
        }

        PublishModes();
        IChatSession? session = null;
        try
        {
            session = await SessionAsync(chat, existing: true, cancellationToken).ConfigureAwait(false);
            foreach (var turn in await session.GetHistoryAsync(cancellationToken).ConfigureAwait(false))
            {
                post(turn.FromUser ? PanelMessages.UserMessage(turn.Id, turn.Text) : PanelMessages.Answer(turn.Id, turn.Text));
            }

            lock (_gate)
            {
                _record = chat;
                _onDisk = true;
                _model = chat.Model;
            }

            post(PanelMessages.Model(chat.Model));
            return true;
        }
#pragma warning disable CA1031 // A Chat that cannot be resumed is reported in the Panel; the list stays usable.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            if (session is not null) await DropSessionAsync(session).ConfigureAwait(false);
            post(PanelMessages.ChatError(ex.Message));
            return false;
        }
        finally
        {
            EndSwitch();
            PublishChats();
        }
    }

    /// <summary>Deletes a Chat from the list and from the CLI; when it is the open one the Copilot Panel shows an empty Chat.</summary>
    public async Task DeleteAsync(string chatId, CancellationToken cancellationToken)
    {
        bool wasCurrent;
        bool wasBusy = false;
        lock (_gate)
        {
            wasCurrent = _record?.Id == chatId;
            if (wasCurrent) wasBusy = _busy;
        }

        if (wasCurrent)
        {
            await LeaveCurrentAsync().ConfigureAwait(false);
            post(PanelMessages.Clear());
            if (wasBusy) SetIdle();
            PublishModes();
        }

        store.Delete(chatId);
        actionLogs?.Delete(chatId);
        try
        {
            await deleteInCli(chatId, cancellationToken).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // The CLI may be off or not know the Chat; it is gone from the list either way.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
        }

        PublishChats();
    }

    /// <summary>The user answered a tool approval prompt: Allow, Allow for this Chat or Deny.</summary>
    public void Answer(string approvalId, ApprovalAnswer answer)
    {
        ToolApprovals? approvals;
        lock (_gate) approvals = _approvals;
        approvals?.Answer(approvalId, answer);
    }

    /// <summary>The user allowed once (<paramref name="approved"/>) or denied a tool approval prompt.</summary>
    public void Answer(string approvalId, bool approved) => Answer(approvalId, approved ? ApprovalAnswer.AllowOnce : ApprovalAnswer.Deny);

    /// <summary>The Copilot Panel closes: open prompts are rejected and the session is released.</summary>
    public async ValueTask DisposeAsync()
    {
        IChatSession? session;
        lock (_gate) session = _session;
        _approvals?.RejectPending();
        if (session is not null) await DropSessionAsync(session).ConfigureAwait(false);
    }

    private static string TitleOf(string text)
    {
        var line = text.Split('\n', 2)[0].Trim();
        return line.Length <= TitleLength ? line : line[..(TitleLength - 1)].TrimEnd() + "…";
    }

    private bool TryBeginSwitch()
    {
        lock (_gate)
        {
            if (_busy) return false;
            _busy = true;
            return true;
        }
    }

    private void EndSwitch()
    {
        lock (_gate) _busy = false;
    }

    /// <summary>Drops the CLI session of the current Chat and forgets the Chat (it stays in the list).</summary>
    private async Task LeaveCurrentAsync()
    {
        IChatSession? session;
        lock (_gate)
        {
            session = _session;
            _record = null;
            _onDisk = false;
            _modes = null;
            _policy = ModePolicy.None;
            _policyRead = false;
            _folderChosen = false;
            _chosenFolder = null;
            _sandboxPolicy = SandboxPolicy.None;
            _sandboxChoice = null;
            _sandboxApplied = null;
        }

        _approvals?.RejectPending();
        if (session is not null) await DropSessionAsync(session).ConfigureAwait(false);
    }


    /// <summary>
    /// Fail closed (S-17): in Allow All and Autopilot no one approves each action, so a prompt goes out only once the CLI has
    /// told the organization's mode policy and confirmed the sandbox state. What is unconfirmed is tried again first, so a passing failure costs one refused prompt.
    /// Null when the prompt may be sent, otherwise why not. Manual and Assisted in Interactive or Plan mode are not gated.
    /// </summary>
    private async Task<string?> UnconfirmedSafetyAsync(IChatSession session, CancellationToken cancellationToken)
    {
        bool policyRead;
        lock (_gate)
        {
            if (!Unattended(CurrentModes())) return null;
            policyRead = _policyRead;
        }

        if (!policyRead)
        {
            if (await ReadModePolicyAsync(session, cancellationToken).ConfigureAwait(false) is not { } policy) return ModePolicyUnconfirmed;
            ChatModes modes;
            lock (_gate)
            {
                // Keep what the CLI refused since (PermissionModeBlocked) on top of what the policy now says.
                _policy = new ModePolicy(policy.AssistedBlocked ?? _policy.AssistedBlocked, policy.AllowAllBlocked ?? _policy.AllowAllBlocked);
                _policyRead = true;
                modes = CurrentModes();
            }

            PublishModes();
            if (policy.BlockedReason(modes.Permission) is { } reason) return $"{ChatModes.Label(modes.Permission)} is not available: {reason}";
        }

        return await ApplySandboxAsync(session, cancellationToken).ConfigureAwait(false) ? null : SandboxUnconfirmed;
    }

    /// <summary>The organization's policy as the CLI tells it; null when it could not be read.</summary>
    private static async Task<ModePolicy?> ReadModePolicyAsync(IChatSession session, CancellationToken cancellationToken)
    {
        try
        {
            return await session.GetModePolicyAsync(cancellationToken).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // An unreadable policy is reported to the caller as null; it decides how to fail closed.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            return null;
        }
    }

    /// <summary>Modes in which the CLI acts without asking for each action.</summary>
    private static bool Unattended(ChatModes modes) =>
        modes.Permission == CopilotPermissionMode.AllowAll || modes.Agent == CopilotAgentMode.Autopilot;

    /// <summary>Switches the open session to the selected model; a failure keeps the session and the old model.</summary>
    private async Task<bool> ApplyModelAsync(IChatSession session, CancellationToken cancellationToken)
    {
        string wanted;
        lock (_gate)
        {
            if (_model is null || _model == _sessionModel) return true;
            wanted = _model;
        }

        try
        {
            await session.SetModelAsync(wanted, cancellationToken).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // A model that cannot be switched to is shown; the Chat goes on with the old one.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            lock (_gate) _model = _sessionModel;
            post(PanelMessages.ChatError($"Could not switch to {wanted}: {ex.Message}"));
            post(PanelMessages.Model(Model));
            SetIdle();
            return false;
        }

        lock (_gate) _sessionModel = wanted;
        return true;
    }

    /// <summary>Asks the CLI for the quota and shows it in the Copilot Panel (after every answer and when the Copilot Panel opens).</summary>
    public async Task RefreshQuotaAsync(CancellationToken cancellationToken = default)
    {
        if (refreshQuota is null) return;
        try
        {
            if (await refreshQuota(cancellationToken).ConfigureAwait(false) is { } quota) post(PanelMessages.Quota(quota));
        }
#pragma warning disable CA1031 // The quota is a hint; the Chat does not depend on it.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
        }
    }

    private async Task<IChatSession> SessionAsync(ChatRecord chat, bool existing, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_session is not null) return _session;
        }

        // Guard Rules hold in every Permission Mode; they judge against the Chat's Working Folder and Tainted state as they are at each call.
        var guard = new ChatGuard(guardRules?.Invoke(), () => chat.WorkingFolder, () => { lock (_gate) return chat.Tainted; }, environment,
            reason => MarkTainted(chat, reason));
        var approvals = new ToolApprovals(
            request => post(PanelMessages.Approval(request)),
            (id, answer) => post(PanelMessages.ApprovalDone(id, answer)),
            guard,
            actionLogs?.Open(chat.Id),
            () => ChatModes.Label(Modes.Permission),
            () => Modes.Agent == CopilotAgentMode.Plan); // read at every call: a switch to or from Plan mode applies at once
        var session = await open(chat, existing, approvals, cancellationToken).ConfigureAwait(false);
        var subscription = session.Subscribe(OnEvent);
        lock (_gate)
        {
            _session = session;
            _sessionModel = chat.Model;
            _approvals = approvals;
            _subscription = subscription;
        }

        try
        {
            await ApplyModesAsync(session, chat, existing, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await DropSessionAsync(session).ConfigureAwait(false);
            throw;
        }

        return session;
    }

    /// <summary>
    /// Puts a freshly opened session into the Chat's modes: asks which Permission Modes the organization's policy
    /// blocks, falls back to Manual (and says why) when the Chat's mode is one of them, then sets both modes in the CLI.
    /// </summary>
    private async Task ApplyModesAsync(IChatSession session, ChatRecord chat, bool existing, CancellationToken cancellationToken)
    {
        // Unknown policy: the CLI still enforces it and refuses a blocked mode below; prompts in Allow All or Autopilot wait
        // until it could be read (UnconfirmedSafetyAsync).
        var read = await ReadModePolicyAsync(session, cancellationToken).ConfigureAwait(false);
        var policy = read ?? ModePolicy.None;
        ChatModes modes;
        lock (_gate)
        {
            _policy = policy;
            _policyRead = read is not null;
            modes = CurrentModes();
        }

        if (policy.BlockedReason(modes.Permission) is { } reason)
        {
            post(PanelMessages.ChatError($"{ChatModes.Label(modes.Permission)} is not available: {reason} This Chat continues in Manual."));
            modes = modes with { Permission = CopilotPermissionMode.Manual };
        }

        var result = await session.SetPermissionModeAsync(modes.Permission, cancellationToken).ConfigureAwait(false);
        if (!result.Applied)
        {
            var refused = Refuse(modes.Permission, ModePolicy.RefusedByCli);
            post(PanelMessages.ChatError($"{ChatModes.Label(modes.Permission)} is not available: {refused} This Chat continues in {ChatModes.Label(result.Mode)}."));
        }

        await session.SetAgentModeAsync(modes.Agent, cancellationToken).ConfigureAwait(false);
        modes = modes with { Permission = result.Mode };
        SandboxPolicy sandboxPolicy;
        try
        {
            sandboxPolicy = await session.GetSandboxPolicyAsync(cancellationToken).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Unknown: the CLI still enforces what applies; the toggle stays usable.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            sandboxPolicy = SandboxPolicy.None;
        }

        lock (_gate)
        {
            _modes = modes;
            _sandboxPolicy = sandboxPolicy;
            _sandboxApplied = null;
        }

        await ApplySandboxAsync(session, cancellationToken).ConfigureAwait(false);
        chat.AgentMode = modes.Agent;
        chat.PermissionMode = modes.Permission;
        if (existing) store.Save(chat);
        PublishModes();
    }

    /// <summary>The current Chat's modes; a new Chat starts with the defaults. Call under the lock.</summary>
    private ChatModes CurrentModes() => _modes ??= _defaultModes();

    /// <summary>
    /// Marks a Permission Mode as blocked and returns the reason it now shows: an earlier, more specific reason (a policy
    /// enforcement the CLI reported) is kept; a later one replaces this (PermissionModeBlocked).
    /// </summary>
    private string Refuse(CopilotPermissionMode mode, string reason)
    {
        lock (_gate)
        {
            if (_policy.BlockedReason(mode) is null) _policy = _policy.Block(mode, reason);
            return _policy.BlockedReason(mode)!;
        }
    }

    /// <summary>Changes the current Chat's modes, keeps them with the Chat (once it is in the list) and shows them.</summary>
    private void Remember(Func<ChatModes, ChatModes> change)
    {
        ChatRecord? record;
        lock (_gate)
        {
            _modes = change(CurrentModes());
            record = _onDisk ? _record : null;
            if (record is not null)
            {
                record.AgentMode = _modes.Agent;
                record.PermissionMode = _modes.Permission;
            }
        }

        if (record is not null) store.Save(record);
        PublishModes();
    }

    private async Task DropSessionAsync(IChatSession session)
    {
        IDisposable? subscription;
        ToolApprovals? approvals;
        lock (_gate)
        {
            if (!ReferenceEquals(_session, session)) return;
            subscription = _subscription;
            approvals = _approvals;
            _session = null;
            _sessionModel = null;
            _sandboxApplied = null;
            _subscription = null;
            _approvals = null;
        }

        subscription?.Dispose();
        approvals?.RejectPending();
        await session.DisposeAsync().ConfigureAwait(false);
    }

    private void OnEvent(ChatEvent e)
    {
        switch (e)
        {
            case ChatEvent.AnswerDelta d: post(PanelMessages.AnswerDelta(d.MessageId, d.Text)); break;
            case ChatEvent.Answer a: post(PanelMessages.Answer(a.MessageId, a.Markdown)); break;
            case ChatEvent.ToolStarted t:
                if (!t.SubAgent) post(PanelMessages.Tool(t.ToolCallId, t.ToolName, "running"));
                ToolApprovals? approvals;
                lock (_gate) approvals = _approvals;
                if (approvals is not null && !approvals.ToolStarted(t.Tool ?? t.ToolName, t.Arguments)) _ = HookDidNotRunAsync();
                break;
            case ChatEvent.ToolCompleted c: post(PanelMessages.Tool(c.ToolCallId, null, c.Success ? "done" : "failed", c.Error)); break;
            case ChatEvent.Failed f: post(PanelMessages.ChatError(f.Message)); break;
            case ChatEvent.Idle: SetIdle(); _ = RefreshQuotaAsync(); break;
            case ChatEvent.AgentModeChanged m:
                Remember(modes => modes with { Agent = m.Mode });
                IChatSession? current;
                lock (_gate) current = _session;
                if (current is not null) _ = ApplySandboxAsync(current, CancellationToken.None);
                break;
            case ChatEvent.PermissionModeBlocked b:
                lock (_gate) _policy = _policy.Block(b.Mode, b.Reason);
                PublishModes();
                break;
        }
    }

    /// <summary>
    /// Hook-liveness watchdog: a tool started that the Guard Rules hook never saw, so the Guard Rules did not judge it
    /// (whatever the Permission Mode). The answer is stopped at once and the Chat falls back to Manual, where the CLI asks
    /// for every action; the Copilot Panel says why (the Action Log has the call).
    /// </summary>
    private async Task HookDidNotRunAsync()
    {
        post(PanelMessages.Warning(HookDidNotRunWarning));
        try
        {
            await CancelAsync(CancellationToken.None).ConfigureAwait(false);
            await SetPermissionModeAsync(CopilotPermissionMode.Manual, CancellationToken.None).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Fire and forget from the event thread: a failure is shown, the warning already is.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            post(PanelMessages.ChatError($"Could not stop the answer or switch to Manual: {ex.Message}"));
        }
    }

    private void SetIdle()
    {
        lock (_gate) _busy = false;
        post(PanelMessages.Busy(false));
    }
}
