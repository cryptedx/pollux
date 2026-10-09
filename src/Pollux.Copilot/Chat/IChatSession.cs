using Pollux.Core.Settings;
using Pollux.Copilot.Tools;

namespace Pollux.Copilot.Chat;

/// <summary>The CLI side of one Chat (an SDK session); implemented over the SDK and by test fakes.</summary>
public interface IChatSession : IAsyncDisposable
{
    /// <summary>Receives the Chat's events, on a background thread, in order.</summary>
    IDisposable Subscribe(Action<ChatEvent> handler);

    /// <summary>Sends a user message; the answer arrives as events.</summary>
    Task SendAsync(string prompt, CancellationToken cancellationToken);

    /// <summary>The Chat so far (messages of the user and Copilot's answers), as the CLI kept it; used when a Chat is resumed.</summary>
    Task<IReadOnlyList<ChatTurn>> GetHistoryAsync(CancellationToken cancellationToken);

    /// <summary>Stops the running answer; an <see cref="ChatEvent.Idle"/> follows.</summary>
    Task AbortAsync(CancellationToken cancellationToken);

    /// <summary>Switches the Chat's Agent Mode in the CLI.</summary>
    Task SetAgentModeAsync(CopilotAgentMode mode, CancellationToken cancellationToken);

    /// <summary>Switches the Chat's Permission Mode in the CLI; an organization policy may refuse it (then the result says which mode stays).</summary>
    Task<PermissionModeResult> SetPermissionModeAsync(CopilotPermissionMode mode, CancellationToken cancellationToken);

    /// <summary>The Permission Modes the organization's policy blocks for this Chat.</summary>
    Task<ModePolicy> GetModePolicyAsync(CancellationToken cancellationToken);

    /// <summary>What the host and the organization say about the CLI's local sandbox for this Chat.</summary>
    Task<SandboxPolicy> GetSandboxPolicyAsync(CancellationToken cancellationToken);

    /// <summary>Turns the CLI's local sandbox on or off for this Chat's session.</summary>
    Task SetSandboxAsync(bool enabled, CancellationToken cancellationToken);

    /// <summary>Switches the model; it takes effect with the next message and the Chat is kept.</summary>
    Task SetModelAsync(string modelId, CancellationToken cancellationToken);
}

/// <summary>The outcome of a Permission Mode switch: whether it was applied and the mode the Chat is in now (the CLI's word).</summary>
public sealed record PermissionModeResult(bool Applied, CopilotPermissionMode Mode);

/// <summary>One message of a Chat's history: the user's (<paramref name="FromUser"/>) or Copilot's answer in Markdown.</summary>
public sealed record ChatTurn(bool FromUser, string Id, string Text);

/// <summary>A connection that can open, resume and delete Chats (the real CLI connection).</summary>
public interface ICopilotChatSource
{
    /// <summary>
    /// Opens the Chat <paramref name="chat"/> (its id is the SDK session id; its model and Working Folder apply):
    /// creates the session, or resumes the existing one when <paramref name="existing"/> is set. Tool requests are decided by
    /// <paramref name="approvals"/>; the Chat can use the Pollux tools over <paramref name="actions"/>.
    /// </summary>
    Task<IChatSession> OpenChatAsync(ChatRecord chat, bool existing, ToolApprovals approvals, IPolluxActions actions, CancellationToken cancellationToken);

    /// <summary>Deletes the Chat's session data in the CLI.</summary>
    Task DeleteChatAsync(string chatId, CancellationToken cancellationToken);
}
