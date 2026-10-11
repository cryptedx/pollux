using System.Text.Json;
using Pollux.Core.Settings;

namespace Pollux.Copilot.Chat;

/// <summary>
/// The Permission Modes an organization policy blocks for a Chat, each with the reason the switcher shows. The CLI enforces
/// the policy itself (Pollux never works around it); this only lets the Copilot Panel disable what the CLI would refuse anyway.
/// Manual can never be blocked.
/// </summary>
public sealed record ModePolicy(string? AssistedBlocked = null, string? AllowAllBlocked = null)
{
    public const string BlockedByOrganization = "Blocked by your organization's Copilot policy.";
    public const string BlockedUndetermined = "Blocked because your organization's Copilot policy could not be checked.";

    /// <summary>The CLI refused the mode without naming a policy (e.g. the Assisted judge model is not available to the account).</summary>
    public const string RefusedByCli = "The Copilot CLI refused to switch to it.";

    /// <summary>No mode is blocked.</summary>
    public static ModePolicy None { get; } = new();

    /// <summary>Why <paramref name="mode"/> cannot be selected; null when it can.</summary>
    public string? BlockedReason(CopilotPermissionMode mode) => mode switch
    {
        CopilotPermissionMode.Assisted => AssistedBlocked,
        CopilotPermissionMode.AllowAll => AllowAllBlocked,
        _ => null,
    };

    /// <summary>The same policy with <paramref name="mode"/> blocked (the CLI refused it); Manual stays allowed.</summary>
    public ModePolicy Block(CopilotPermissionMode mode, string reason) => mode switch
    {
        CopilotPermissionMode.Assisted => this with { AssistedBlocked = reason },
        CopilotPermissionMode.AllowAll => this with { AllowAllBlocked = reason },
        _ => this,
    };

    /// <summary>
    /// Reads the CLI's resolved managed settings: bypass permissions disabled (or any <c>disableBypassPermissionsMode</c>
    /// value, unknown ones fail closed) blocks Allow All; <c>disableAssistedPermissionsMode</c> blocks Assisted.
    /// </summary>
    /// <param name="settings">The effective managed settings values, nested (<c>permissions.x</c>) or flattened (<c>"permissions.x"</c>).</param>
    public static ModePolicy FromManagedSettings(bool bypassPermissionsDisabled, bool failClosed, JsonElement? settings)
    {
        var reason = failClosed ? BlockedUndetermined : BlockedByOrganization;
        var bypass = Permission(settings, "disableBypassPermissionsMode");
        var assisted = Permission(settings, "disableAssistedPermissionsMode");
        var allowAllBlocked = bypassPermissionsDisabled
            || bypass is { ValueKind: JsonValueKind.True } || bypass is { ValueKind: JsonValueKind.String } s && s.GetString() is { Length: > 0 };
        var assistedBlocked = assisted is { ValueKind: JsonValueKind.True };
        return new ModePolicy(assistedBlocked ? reason : null, allowAllBlocked ? reason : null);
    }

    private static JsonElement? Permission(JsonElement? settings, string key)
    {
        if (settings is not { ValueKind: JsonValueKind.Object } root) return null;
        if (root.TryGetProperty("permissions", out var p) && p.ValueKind == JsonValueKind.Object && p.TryGetProperty(key, out var nested)) return nested;
        return root.TryGetProperty("permissions." + key, out var flat) ? flat : null;
    }
}
