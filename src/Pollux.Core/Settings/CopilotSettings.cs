namespace Pollux.Core.Settings;

/// <summary>Copilot integration (opt-in, see ADR 0005).</summary>
public sealed class CopilotSettings
{
    /// <summary>"Enable Copilot". Off: no Copilot UI, commands or CLI process anywhere in Pollux.</summary>
    public bool Enabled { get; set; }

    /// <summary>Copilot CLI to run instead of the one bundled with Pollux; null uses the bundled CLI (ADR 0005).</summary>
    public string? CliPath { get; set; }

    /// <summary>Proxy for the Copilot CLI process only (HTTPS_PROXY/HTTP_PROXY); null leaves the CLI to its own environment.</summary>
    public string? Proxy { get; set; }

    /// <summary>PEM file with additional CA certificates for the Copilot CLI process only (NODE_EXTRA_CA_CERTS).</summary>
    public string? CaCertificates { get; set; }

    /// <summary>Agent Mode of a new Chat (each Chat can switch it).</summary>
    public CopilotAgentMode DefaultAgentMode { get; set; }

    /// <summary>Permission Mode of a new Chat (each Chat can switch it; an organization policy may block a mode).</summary>
    public CopilotPermissionMode DefaultPermissionMode { get; set; }

    /// <summary>Extensions to the built-in Guard Rules; the defaults always apply and cannot be removed here.</summary>
    public GuardRuleSettings GuardRules { get; set; } = new();

    /// <summary>Own prompts for the selection actions; they appear next to Explain, Summarize and Rename by content.</summary>
    public List<PromptTemplate> PromptTemplates { get; set; } = [];
}

/// <summary>A user-defined selection action: <see cref="Name"/> is its menu and palette title.</summary>
public sealed class PromptTemplate
{
    public string Name { get; set; } = "";
    public string Prompt { get; set; } = "";
}

/// <summary>
/// User additions to the Guard Rules. Command entries are a command name optionally followed by arguments that must
/// all be present, e.g. "shutdown" or "git push --tags"; aliases (iwr, rm, del, …) are resolved, case is ignored.
/// Protected paths may use ~, %VARS% and wildcards; an entry without a backslash or slash matches a file or folder name
/// anywhere (e.g. "*.kdbx").
/// </summary>
public sealed class GuardRuleSettings
{
    public List<string> BlockedCommands { get; set; } = [];
    public List<string> AskCommands { get; set; } = [];
    public List<string> ProtectedPaths { get; set; } = [];

    /// <summary>Places whose content makes a Chat a Tainted Chat, in addition to Downloads, Inbox, Temp and archives (same pattern rules as <see cref="ProtectedPaths"/>).</summary>
    public List<string> UntrustedPaths { get; set; } = [];
}

/// <summary>Agent Mode: how far Copilot goes on its own in a Chat (the Copilot CLI's own modes).</summary>
public enum CopilotAgentMode
{
    /// <summary>Works step by step with the user.</summary>
    Interactive,

    /// <summary>Only plans, does not act.</summary>
    Plan,

    /// <summary>Keeps working until the task is done.</summary>
    Autopilot,
}

/// <summary>Permission Mode: which of Copilot's actions need the user's approval (the Copilot CLI's own modes). Guard Rules apply in every mode.</summary>
public enum CopilotPermissionMode
{
    /// <summary>Asks for each action.</summary>
    Manual,

    /// <summary>A second model approves what it judges safe.</summary>
    Assisted,

    /// <summary>Asks for nothing.</summary>
    AllowAll,
}
