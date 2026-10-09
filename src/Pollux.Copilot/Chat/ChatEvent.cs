using Pollux.Core.Settings;

namespace Pollux.Copilot.Chat;

/// <summary>What happens in a Chat while Copilot answers, as Pollux needs it (a small view of the SDK's session events).</summary>
public abstract record ChatEvent
{
    /// <summary>A streamed piece of the answer <paramref name="MessageId"/>.</summary>
    public sealed record AnswerDelta(string MessageId, string Text) : ChatEvent;

    /// <summary>The complete answer <paramref name="MessageId"/> (Markdown); replaces its deltas.</summary>
    public sealed record Answer(string MessageId, string Markdown) : ChatEvent;

    /// <summary>Copilot started a tool (built-in CLI tool such as shell, view, edit).</summary>
    /// <param name="ToolName">What the Copilot Panel shows (the tool's title, else its name).</param>
    /// <param name="Tool">The tool's name as the CLI calls it (powershell, view, ...); null when unknown.</param>
    /// <param name="Arguments">The call's arguments (JSON); compared with what the Guard Rules hook saw.</param>
    /// <param name="SubAgent">A sub-agent runs the tool (not shown in the Copilot Panel, but checked like any other).</param>
    public sealed record ToolStarted(string ToolCallId, string ToolName, string? Tool = null, string? Arguments = null, bool SubAgent = false) : ChatEvent;

    /// <summary>A tool finished; <paramref name="Error"/> says why it failed or was rejected.</summary>
    public sealed record ToolCompleted(string ToolCallId, bool Success, string? Error) : ChatEvent;

    /// <summary>The CLI reported an error for this Chat.</summary>
    public sealed record Failed(string Message) : ChatEvent;

    /// <summary>The CLI switched the Chat's Agent Mode (on request, or on its own, e.g. after a plan was accepted).</summary>
    public sealed record AgentModeChanged(CopilotAgentMode Mode) : ChatEvent;

    /// <summary>The organization's policy refused a Permission Mode; <paramref name="Reason"/> says why.</summary>
    public sealed record PermissionModeBlocked(CopilotPermissionMode Mode, string Reason) : ChatEvent;

    /// <summary>Copilot finished (or was stopped): the Chat takes the next message.</summary>
    public sealed record Idle(bool Aborted) : ChatEvent;
}
