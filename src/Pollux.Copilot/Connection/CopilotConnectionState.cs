using Pollux.Copilot.Panel;

namespace Pollux.Copilot.Connection;

public enum CopilotConnectionIssue
{
    /// <summary>Signed in and usable: the Chat is shown.</summary>
    Ready,

    /// <summary>Copilot is off or unavailable; no Copilot UI exists.</summary>
    Disabled,

    CliMissing,
    CliFailed,
    NotSignedIn,
    NoSeat,
    OrgPolicy,

    /// <summary>Proxy, certificate or other network failure between the CLI and GitHub.</summary>
    Network,

    QuotaExhausted,
}

/// <summary>What the user can do about a state; the Copilot Panel shows one button for it.</summary>
public enum CopilotPanelAction
{
    None,
    SignIn,
    Retry,
    Recheck,
    OpenSettings,
}

/// <summary>A Copilot Panel connection state: cause, next step and the action that performs it.</summary>
public sealed record CopilotConnectionState(CopilotConnectionIssue Issue, string Title, string Message, CopilotPanelAction Action)
{
    public static CopilotConnectionState Ready { get; } = new(CopilotConnectionIssue.Ready, "", "", CopilotPanelAction.None);

    /// <param name="detail">The raw cause reported by the CLI; shown for failures that have no better explanation.</param>
    public static CopilotConnectionState For(CopilotConnectionIssue issue, string? detail = null) => issue switch
    {
        CopilotConnectionIssue.Ready => Ready,
        CopilotConnectionIssue.Disabled => new(issue, "Copilot is off", "Enable Copilot in the settings to use it.", CopilotPanelAction.None),
        CopilotConnectionIssue.CliMissing => new(issue, "Copilot CLI not found",
            $"The Copilot CLI could not be found. {detail} Reinstall Pollux or check the CLI path in the settings.", CopilotPanelAction.OpenSettings),
        CopilotConnectionIssue.CliFailed => new(issue, "Copilot CLI failed",
            $"The Copilot CLI did not start or stopped working: {detail}", CopilotPanelAction.Retry),
        CopilotConnectionIssue.NotSignedIn => new(issue, "Not signed in",
            "Copilot needs a GitHub account with Copilot. Sign in with the Copilot CLI; Pollux never sees your token.", CopilotPanelAction.SignIn),
        CopilotConnectionIssue.NoSeat => new(issue, "No Copilot seat",
            "This GitHub account has no Copilot subscription or seat. Ask your organization for a seat or subscribe, then check again.", CopilotPanelAction.Recheck),
        CopilotConnectionIssue.OrgPolicy => new(issue, "Blocked by organization policy",
            "Your organization's policy does not allow Copilot for this account. Ask a GitHub administrator, then check again.", CopilotPanelAction.Recheck),
        CopilotConnectionIssue.Network => new(issue, "Cannot reach GitHub",
            "The Copilot CLI could not connect to GitHub, probably because of a proxy or certificate problem. Set copilot.proxy and copilot.caCertificates in the settings.", CopilotPanelAction.OpenSettings),
        CopilotConnectionIssue.QuotaExhausted => new(issue, "Copilot quota used up",
            "Your Copilot usage for this period is exhausted. It resets with your billing period; you can check again later.", CopilotPanelAction.Recheck),
        _ => throw new ArgumentOutOfRangeException(nameof(issue)),
    };

    /// <summary>The Copilot Panel's state view; <see cref="PanelStateView.None"/> when ready.</summary>
    public PanelStateView ToView() => Issue == CopilotConnectionIssue.Ready
        ? PanelStateView.None
        : new PanelStateView(Issue.ToString(), Title, Message, Action == CopilotPanelAction.None ? null : Action);
}
