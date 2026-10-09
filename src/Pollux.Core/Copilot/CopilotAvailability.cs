using Pollux.Core.Settings;

namespace Pollux.Core.Copilot;

/// <summary>
/// The one gate for every Copilot entry point (Copilot Panel, palette, keybindings, menus, CLI process):
/// Copilot exists only when the user enabled it, and never in the Admin Instance (ADR 0005).
/// </summary>
public static class CopilotAvailability
{
    /// <summary>Prefix of every command that belongs to Copilot.</summary>
    public const string CommandPrefix = "copilot.";

    public static bool IsAvailable(CopilotSettings settings, bool adminInstance)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings.Enabled && !adminInstance;
    }

    public static bool IsCopilotCommand(string commandId) =>
        commandId.StartsWith(CommandPrefix, StringComparison.Ordinal);

    /// <summary>Copilot commands do nothing and are hidden while Copilot is unavailable; other commands are unaffected.</summary>
    public static bool AllowsCommand(string commandId, bool available) =>
        available || !IsCopilotCommand(commandId);

    /// <summary>Copilot settings outside the Copilot section (the status bar's quota) are shown only while Copilot is available.</summary>
    public static bool ShowsSetting(string key, bool available) =>
        available || !key.Equals("statusBar.copilotQuota", StringComparison.Ordinal);

    /// <summary>
    /// The CLI self-test (<c>--copilot-selftest</c>, for CI) bypasses the "Enable Copilot" setting but never runs in the
    /// Admin Instance: an elevated process never starts the Copilot CLI. CI runners (GitHub Actions' Windows images run
    /// every process elevated) are the one exception (<paramref name="ciRun"/>: GITHUB_ACTIONS=true).
    /// </summary>
    public static bool AllowsSelfTest(bool adminInstance, bool ciRun) => !adminInstance || ciRun;
}
