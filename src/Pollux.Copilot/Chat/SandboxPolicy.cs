using Pollux.Core.Settings;

namespace Pollux.Copilot.Chat;

/// <summary>What the sandbox toggle of a Chat shows: whether the CLI's local sandbox is on, and why the toggle is locked (null when it can be switched).</summary>
public sealed record SandboxState(bool Enabled, string? LockedReason)
{
    public bool Locked => LockedReason is not null;
}

/// <summary>
/// What the host and the organization say about the CLI's local sandbox: this Windows build may not support it
/// (<see cref="UnsupportedReason"/>) and the organization may require it (<see cref="ForcedByOrganization"/>).
/// The CLI enforces both itself; this only lets the Copilot Panel show the toggle truthfully.
/// </summary>
public sealed record SandboxPolicy(string? UnsupportedReason = null, bool ForcedByOrganization = false)
{
    public const string UnsupportedDefault = "The sandbox is not supported on this Windows build.";
    public const string Forced = "The sandbox is required by your organization.";

    /// <summary>Nothing known: supported, not forced (the CLI still enforces whatever applies).</summary>
    public static SandboxPolicy None { get; } = new();

    /// <summary>The sandbox is on by default for Autopilot or Allow All, off otherwise.</summary>
    public static bool DefaultFor(ChatModes modes) =>
        modes.Agent == CopilotAgentMode.Autopilot || modes.Permission == CopilotPermissionMode.AllowAll;

    /// <summary>The state of the toggle for a Chat in <paramref name="modes"/> where the user chose <paramref name="choice"/> (null: not chosen, so the default).</summary>
    public SandboxState Resolve(ChatModes modes, bool? choice)
    {
        if (ForcedByOrganization)
            return new SandboxState(true, UnsupportedReason is null ? Forced : Forced + " " + UnsupportedReason);
        if (UnsupportedReason is not null) return new SandboxState(false, UnsupportedReason);
        return new SandboxState(choice ?? DefaultFor(modes), null);
    }

    /// <summary>Builds the policy from the CLI's answers.</summary>
    public static SandboxPolicy From(bool hostSupported, string? hostReason, bool required) =>
        new(hostSupported ? null : string.IsNullOrWhiteSpace(hostReason) ? UnsupportedDefault : hostReason, required);
}
