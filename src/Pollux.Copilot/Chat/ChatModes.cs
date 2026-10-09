using Pollux.Core.Settings;

namespace Pollux.Copilot.Chat;

/// <summary>The Agent Mode and Permission Mode of a Chat.</summary>
public sealed record ChatModes(CopilotAgentMode Agent, CopilotPermissionMode Permission)
{
    /// <summary>What a new Chat starts with unless configured otherwise.</summary>
    public static ChatModes Default { get; } = new(CopilotAgentMode.Interactive, CopilotPermissionMode.Manual);

    /// <summary>The modes a new Chat starts with (<c>copilot.defaultAgentMode</c>, <c>copilot.defaultPermissionMode</c>).</summary>
    public static ChatModes Of(CopilotSettings settings) => new(settings.DefaultAgentMode, settings.DefaultPermissionMode);

    /// <summary>The name shown in the Copilot Panel.</summary>
    public static string Label(CopilotAgentMode mode) => mode.ToString();

    /// <summary>The name shown in the Copilot Panel.</summary>
    public static string Label(CopilotPermissionMode mode) => mode == CopilotPermissionMode.AllowAll ? "Allow All" : mode.ToString();
}
