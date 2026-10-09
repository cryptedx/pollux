using GitHub.Copilot;
using GitHub.Copilot.Rpc;
using Pollux.Core.Settings;

namespace Pollux.Copilot.Chat;

/// <summary>A Chat over an SDK <see cref="CopilotSession"/>: maps its events to <see cref="ChatEvent"/>s.</summary>
internal sealed class CliChatSession(CopilotSession session, Func<CancellationToken, Task<(bool Supported, string? Reason)>>? hostSupport = null) : IChatSession
{
    public IDisposable Subscribe(Action<ChatEvent> handler) => session.On<SessionEvent>(e =>
    {
        if (Map(e) is { } chatEvent) handler(chatEvent);
    });

    public Task SendAsync(string prompt, CancellationToken cancellationToken) =>
        session.SendAsync(new MessageOptions { Prompt = prompt }, cancellationToken);

    public async Task<IReadOnlyList<ChatTurn>> GetHistoryAsync(CancellationToken cancellationToken)
    {
        var events = await session.GetEventsAsync(cancellationToken).ConfigureAwait(false);
        return History(events);
    }

    /// <summary>The user's messages and Copilot's complete answers of the main agent, in order.</summary>
    internal static List<ChatTurn> History(IEnumerable<SessionEvent> events)
    {
        List<ChatTurn> turns = [];
        foreach (var e in events)
        {
            if (!string.IsNullOrEmpty(e.AgentId)) continue;
            switch (e)
            {
                case UserMessageEvent u when !string.IsNullOrWhiteSpace(u.Data.Content):
                    turns.Add(new ChatTurn(true, u.Data.MessageId ?? "", Context.ContextPrompt.StripContext(u.Data.Content)));
                    break;
                case AssistantMessageEvent a when a.Data.Content.Length > 0:
                    turns.Add(new ChatTurn(false, a.Data.MessageId, a.Data.Content));
                    break;
            }
        }

        return turns;
    }

    public Task AbortAsync(CancellationToken cancellationToken) => session.AbortAsync(cancellationToken);

#pragma warning disable GHCP001 // Experimental SDK API, pinned together with the CLI version (ADR 0005).
    public Task SetAgentModeAsync(CopilotAgentMode mode, CancellationToken cancellationToken) =>
        session.Rpc.Mode.SetAsync(ToCli(mode), cancellationToken: cancellationToken);

    public async Task<PermissionModeResult> SetPermissionModeAsync(CopilotPermissionMode mode, CancellationToken cancellationToken)
    {
        var result = await session.Rpc.Permissions.SetModeAsync(ToCli(mode), cancellationToken: cancellationToken).ConfigureAwait(false);
        return new PermissionModeResult(result.Success, FromCli(result.Mode) ?? CopilotPermissionMode.Manual);
    }

    public async Task<ModePolicy> GetModePolicyAsync(CancellationToken cancellationToken)
    {
        var managed = await session.Rpc.ManagedSettings.GetAsync(cancellationToken).ConfigureAwait(false);
        return ModePolicy.FromManagedSettings(managed.BypassPermissionsDisabled, managed.FailClosed, managed.Settings);
    }

    public async Task<SandboxPolicy> GetSandboxPolicyAsync(CancellationToken cancellationToken)
    {
        (bool Supported, string? Reason)? host = hostSupport is null ? null : await hostSupport(cancellationToken).ConfigureAwait(false);
        var enforcement = await session.Rpc.Sandbox.GetEnforcementStatusAsync(cancellationToken).ConfigureAwait(false);
        return SandboxPolicy.From(host?.Supported ?? true, host?.Reason, enforcement.Required);
    }

    public Task SetSandboxAsync(bool enabled, CancellationToken cancellationToken) =>
        session.Rpc.Options.UpdateAsync(
            sandboxConfig: new SandboxConfig { Enabled = enabled, AddCurrentWorkingDirectory = true, AllowBypass = false },
            sandboxConfigSource: enabled ? SandboxConfigSource.UserEnabled : SandboxConfigSource.UserDisabled,
            cancellationToken: cancellationToken);

    internal static SessionMode ToCli(CopilotAgentMode mode) => mode switch
    {
        CopilotAgentMode.Plan => SessionMode.Plan,
        CopilotAgentMode.Autopilot => SessionMode.Autopilot,
        _ => SessionMode.Interactive,
    };

    internal static CopilotAgentMode? FromCli(SessionMode mode) =>
        mode == SessionMode.Interactive ? CopilotAgentMode.Interactive
        : mode == SessionMode.Plan ? CopilotAgentMode.Plan
        : mode == SessionMode.Autopilot ? CopilotAgentMode.Autopilot
        : null;

    internal static PermissionMode ToCli(CopilotPermissionMode mode) => mode switch
    {
        CopilotPermissionMode.Assisted => PermissionMode.Assisted,
        CopilotPermissionMode.AllowAll => PermissionMode.AllowAll,
        _ => PermissionMode.Manual,
    };

    internal static CopilotPermissionMode? FromCli(PermissionMode mode) =>
        mode == PermissionMode.Manual ? CopilotPermissionMode.Manual
        : mode == PermissionMode.Assisted ? CopilotPermissionMode.Assisted
        : mode == PermissionMode.AllowAll ? CopilotPermissionMode.AllowAll
        : null;

    /// <summary>The Permission Mode a policy enforcement refused, from the escalation it names.</summary>
    private static CopilotPermissionMode? Refused(SessionManagedSettingsEnforcedData data) =>
        data.Escalation is not { } e ? null
        : e == ManagedSettingsEnforcedEscalation.AssistedApproval ? CopilotPermissionMode.Assisted
        : e == ManagedSettingsEnforcedEscalation.AllowAll || e == ManagedSettingsEnforcedEscalation.ApproveAll ? CopilotPermissionMode.AllowAll
        : null;
#pragma warning restore GHCP001

    private static ChatEvent.ToolStarted ToolStarted(ToolExecutionStartData data, bool subAgent) =>
        new(data.ToolCallId, data.ToolTitle ?? data.ToolName, data.ToolName, ArgumentsJson(data.Arguments), subAgent);

    private static string? ArgumentsJson(System.Text.Json.JsonElement? arguments) =>
        arguments is { ValueKind: not (System.Text.Json.JsonValueKind.Undefined or System.Text.Json.JsonValueKind.Null) } a ? a.GetRawText() : null;

    public Task SetModelAsync(string modelId, CancellationToken cancellationToken) => session.SetModelAsync(modelId, cancellationToken);

    public ValueTask DisposeAsync() => session.DisposeAsync();

    /// <summary>
    /// Only the main agent's answer is shown; sub-agent events (AgentId set) are left out, except their tool starts: the
    /// Chat checks every tool start against the Guard Rules hook.
    /// </summary>
    internal static ChatEvent? Map(SessionEvent e)
    {
        if (!string.IsNullOrEmpty(e.AgentId))
        {
            return e is ToolExecutionStartEvent sub ? ToolStarted(sub.Data, subAgent: true) : null;
        }

        return e switch
        {
            AssistantMessageDeltaEvent d => new ChatEvent.AnswerDelta(d.Data.MessageId, d.Data.DeltaContent),
            AssistantMessageEvent m when m.Data.Content.Length > 0 => new ChatEvent.Answer(m.Data.MessageId, m.Data.Content),
            ToolExecutionStartEvent t => ToolStarted(t.Data, subAgent: false),
            ToolExecutionCompleteEvent c => new ChatEvent.ToolCompleted(c.Data.ToolCallId, c.Data.Success, c.Data.Error?.Message),
            SessionErrorEvent err => new ChatEvent.Failed(err.Data.Message),
            // An Autopilot idle that was not a stop is a continuation boundary: Copilot keeps working (as SendAndWaitAsync does).
            SessionIdleEvent idle when idle.Data.Aborted != true && idle.Data.Mode == SessionMode.Autopilot => null,
            SessionIdleEvent idle => new ChatEvent.Idle(idle.Data.Aborted == true),
            SessionModeChangedEvent mc when FromCli(mc.Data.NewMode) is { } mode => new ChatEvent.AgentModeChanged(mode),
#pragma warning disable GHCP001 // Experimental SDK API, pinned together with the CLI version (ADR 0005).
            SessionManagedSettingsEnforcedEvent enforced when Refused(enforced.Data) is { } refused =>
                new ChatEvent.PermissionModeBlocked(refused, enforced.Data.Message),
#pragma warning restore GHCP001
            _ => null,
        };
    }
}
