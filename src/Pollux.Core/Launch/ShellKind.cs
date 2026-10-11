namespace Pollux.Core.Launch;

/// <summary>The shells Pollux starts: PowerShell 7, Windows PowerShell and the Command Prompt.</summary>
public enum ShellKind
{
    Pwsh,
    WindowsPowerShell,
    Cmd,
}

/// <summary>The shell names of the settings and commands ("pwsh", "powershell", "cmd").</summary>
public static class ShellKinds
{
    /// <summary>The shell <paramref name="name"/> names (case and blanks ignored), or null for anything else (such as "wt").</summary>
    public static ShellKind? Parse(string? name) => name?.Trim().ToLowerInvariant() switch
    {
        "pwsh" => ShellKind.Pwsh,
        "powershell" => ShellKind.WindowsPowerShell,
        "cmd" => ShellKind.Cmd,
        _ => null,
    };
}
