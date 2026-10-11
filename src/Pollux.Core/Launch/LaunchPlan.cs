namespace Pollux.Core.Launch;

/// <summary>What the user asked Pollux to start.</summary>
public abstract record LaunchIntent
{
    private LaunchIntent() { }

    /// <summary>"Open in VS Code" for a file or folder; <paramref name="Command"/> is the integration.vsCodeCommand setting.</summary>
    public sealed record OpenInVsCode(string Path, string Command) : LaunchIntent;

    /// <summary>"Open in Copilot app"; <paramref name="CommandTemplate"/> is integration.copilotAppCommand, "{path}" is replaced.</summary>
    public sealed record OpenInCopilotApp(string Path, string CommandTemplate) : LaunchIntent;

    /// <summary>"Open terminal here"; <paramref name="App"/> is "wt", "pwsh", "powershell" or "cmd" (the terminal settings).</summary>
    public sealed record OpenTerminal(string Folder, string App, string WindowsTerminalProfile, string FallbackShell) : LaunchIntent;

    /// <summary>A shell window ("pwsh", "powershell" or "cmd") the command itself names, e.g. "Open PowerShell here"; no settings involved.</summary>
    public sealed record OpenShell(string Folder, string Shell) : LaunchIntent;

    /// <summary>A hardened source-control run in <paramref name="Repository"/> (the status read behind the git gate); Pollux reads its output.</summary>
    public sealed record Git(string Repository, IReadOnlyList<string> Arguments) : LaunchIntent;

    /// <summary>winget run by Pollux with its output read (update check, per-package upgrade).</summary>
    public sealed record Winget(IReadOnlyList<string> Arguments) : LaunchIntent;

    /// <summary>"Update selected as admin": one elevated Windows PowerShell upgrades all <paramref name="PackageIds"/>; ids that aren't plain winget ids are left out.</summary>
    public sealed record ElevatedWinget(IReadOnlyList<string> PackageIds) : LaunchIntent;

    /// <summary>Create the directory junction <paramref name="LinkPath"/> pointing to <paramref name="TargetPath"/> (cmd's mklink).</summary>
    public sealed record CreateJunction(string LinkPath, string TargetPath) : LaunchIntent;

    /// <summary>Run the Copilot CLI's login in a terminal window; <paramref name="CliPath"/> is the CLI Pollux resolved.</summary>
    public sealed record CopilotLogin(string CliPath) : LaunchIntent;

    /// <summary>The shell of the embedded terminal panel ("pwsh", "powershell" or "cmd") in <paramref name="Folder"/>.</summary>
    public sealed record EmbeddedTerminal(string Folder, string Shell) : LaunchIntent;

    /// <summary>The system "Open with" dialog for a file.</summary>
    public sealed record OpenWith(string Path) : LaunchIntent;

    /// <summary>Explorer with <paramref name="Path"/> selected.</summary>
    public sealed record RevealInExplorer(string Path) : LaunchIntent;
}

/// <summary>How to start a tool, decided by <see cref="LaunchPlanner"/>; the App only executes it.</summary>
public abstract record LaunchPlan
{
    private LaunchPlan() { }

    /// <summary>
    /// Start <paramref name="Executable"/> (always an absolute path) with the raw command-line
    /// <paramref name="Arguments"/> in <paramref name="WorkingDirectory"/>; <paramref name="Hidden"/> for a helper
    /// console (cmd.exe running a .cmd) that must not flash a window; <paramref name="Elevated"/> asks Windows for consent
    /// (runas). <paramref name="Tool"/> names it in messages.
    /// </summary>
    public sealed record Run(string Tool, string Executable, string Arguments, string WorkingDirectory, bool Hidden, bool Elevated = false) : LaunchPlan;

    /// <summary>The tool can't be started; <paramref name="Message"/> names it and says what to fix.</summary>
    public sealed record Missing(string Message) : LaunchPlan;
}

/// <summary>
/// What a plan may depend on: <paramref name="FindOnPath"/> maps a command to an absolute path or null and never
/// searches the current directory or Pollux's folder (<see cref="PathLookup"/>); <paramref name="SystemDirectory"/>
/// is "C:\Windows\System32"; <paramref name="AdminInstance"/> is true in the elevated Admin Instance, whose plans
/// ignore the command and terminal settings from the user's settings.json and use fixed tools (ADR 0008, S-09).
/// <paramref name="AdminOnlyRoots"/> are the folders only administrators can write to (Program Files, Program Files
/// (x86)); the Admin Instance starts a tool found on PATH only from there or the system directory.
/// </summary>
public sealed record LaunchContext(Func<string, string?> FindOnPath, string SystemDirectory, bool AdminInstance = false, IReadOnlyList<string>? AdminOnlyRoots = null);
