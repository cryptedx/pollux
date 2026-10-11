using Pollux.Core.Navigation;

namespace Pollux.Core.Launch;

/// <summary>
/// The tool launch plan: turns a <see cref="LaunchIntent"/> into an absolute executable, its arguments and a working
/// directory. Nothing is ever started by a bare name, so a file in the browsed folder, the current directory or
/// Pollux's own folder can't stand in for a tool (SECURITY.md S-02, S-08); Windows Terminal never sees a ';' from a
/// folder name (S-14).
/// </summary>
public static class LaunchPlanner
{
    public static LaunchPlan Plan(LaunchIntent intent, LaunchContext context) => intent switch
    {
        LaunchIntent.OpenInVsCode v => OpenInVsCode(v, context),
        LaunchIntent.OpenInCopilotApp c => OpenInCopilotApp(c, context),
        LaunchIntent.OpenTerminal t => OpenTerminal(context.AdminInstance ? AdminTerminal(t) : t, context),
        LaunchIntent.OpenShell s => OpenTerminal(new LaunchIntent.OpenTerminal(s.Folder, s.Shell, "", "pwsh"), context),
        LaunchIntent.Git g => Helper("git", "git", g.Arguments, g.Repository, context, "git not found: install Git for Windows and put it on PATH"),
        LaunchIntent.Winget w => Helper("winget", "winget", w.Arguments, context.SystemDirectory, context, WingetMissing),
        LaunchIntent.ElevatedWinget e => ElevatedWinget(e, context),
        LaunchIntent.CreateJunction j => new LaunchPlan.Run("Command Prompt", Cmd(context),
            $"/d /v:off /c mklink /J {CommandLine.QuoteForCmdBuiltin(j.LinkPath)} {CommandLine.QuoteForCmdBuiltin(j.TargetPath)}", context.SystemDirectory, Hidden: true),
        LaunchIntent.CopilotLogin c => new LaunchPlan.Run("Command Prompt", Cmd(context),
            $"/d /v:off /s /k \"{CommandLine.QuoteForCmd(c.CliPath)} login\"", context.SystemDirectory, Hidden: false),
        LaunchIntent.EmbeddedTerminal t => PlanEmbeddedTerminal(t, context),
        LaunchIntent.OpenWith o => new LaunchPlan.Run("Open with", WinPath.Combine(context.SystemDirectory, "rundll32.exe"),
            "shell32.dll,OpenAs_RunDLL " + o.Path, context.SystemDirectory, Hidden: false),
        LaunchIntent.RevealInExplorer r => new LaunchPlan.Run("Explorer", WinPath.Combine(WindowsDirectory(context), "explorer.exe"),
            "/select," + CommandLine.Quote(r.Path), WindowsDirectory(context), Hidden: false),
        _ => throw new ArgumentOutOfRangeException(nameof(intent)),
    };

    private const string WingetMissing = "winget not found. Install 'App Installer' from Microsoft.";

    /// <summary>
    /// <paramref name="name"/> found on PATH. In the Admin Instance only a tool in a folder the non-elevated user cannot
    /// write to counts (the system directory, Program Files): the elevated PATH includes the user's own part, so anything
    /// else could have been planted by a process running as the user (ADR 0008, S-09). <paramref name="rejected"/> is
    /// such a tool that was found but not used.
    /// </summary>
    private static string? Find(string name, LaunchContext context, out string? rejected)
    {
        rejected = null;
        if (context.FindOnPath(name) is not { } exe) return null;
        if (!context.AdminInstance) return exe;
        string[] roots = [context.SystemDirectory, .. context.AdminOnlyRoots ?? []];
        if (WinPath.Parent(exe) is { } folder && !InstallLocation.IsUserWritable(folder, roots)) return exe;
        rejected = exe;
        return null;
    }

    private static string? Find(string name, LaunchContext context) => Find(name, context, out _);

    /// <summary>The message for a tool the Admin Instance found only in a user-writable folder.</summary>
    private static string NotInAdminInstance(string tool, string exe) =>
        $"{tool} not available in the Admin Instance: {exe} is in a folder your normal account can change. Install it for all users.";

    /// <summary>A tool Pollux runs and reads: found on PATH (never in the current or Pollux's folder), run without a window.</summary>
    private static LaunchPlan Helper(string tool, string command, IReadOnlyList<string> arguments, string workingDirectory, LaunchContext context, string missing) =>
        Find(command, context, out var rejected) is { } exe
            ? new LaunchPlan.Run(tool, exe, string.Join(' ', arguments.Select(CommandLine.Quote)), workingDirectory, Hidden: true)
            : new LaunchPlan.Missing(rejected is null ? missing : NotInAdminInstance(tool, rejected));

    private static LaunchPlan ElevatedWinget(LaunchIntent.ElevatedWinget intent, LaunchContext context)
    {
        var ids = intent.PackageIds.Where(IsSafeWingetId).ToList();
        if (ids.Count == 0) return new LaunchPlan.Missing("No package to update.");
        if (Find("winget", context, out var rejected) is not { } winget) return new LaunchPlan.Missing(rejected is null ? WingetMissing : NotInAdminInstance("winget", rejected));

        // Ids are plain dotted names (no quotes or shell syntax) and the script is passed encoded; winget is the
        // absolute path found here, so the elevated PowerShell resolves no name of its own.
        var exe = "& '" + winget.Replace("'", "''", StringComparison.Ordinal) + "'";
        var script = string.Join("; ", ids.Select(id => $"{exe} upgrade --id '{id}' --exact --accept-source-agreements --accept-package-agreements"))
            + "; Read-Host 'Done. Press Enter to close'";
        var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));
        return new LaunchPlan.Run("Windows PowerShell", WindowsPowerShell(context), $"-NoProfile -EncodedCommand {encoded}", context.SystemDirectory, Hidden: false, Elevated: true);
    }

    /// <summary>winget ids are dotted names; anything else (shortened "…" ids, quotes, spaces) never reaches a command line.</summary>
    public static bool IsSafeWingetId(string id) =>
        id.Length > 0 && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' or '+');

    /// <summary>The embedded terminal panel's shell; always available (Windows PowerShell is the last fallback).</summary>
    public static LaunchPlan.Run PlanEmbeddedTerminal(LaunchIntent.EmbeddedTerminal intent, LaunchContext context) =>
        ShellRun(ShellKinds.Parse(intent.Shell) ?? ShellKind.Pwsh, intent.Folder, "-NoLogo", context);

    /// <summary>
    /// A shell window in <paramref name="folder"/>: PowerShell 7 from PATH (else Windows PowerShell), Windows PowerShell
    /// or cmd from the system directory. <paramref name="powerShellArguments"/> go to either PowerShell.
    /// </summary>
    private static LaunchPlan.Run ShellRun(ShellKind kind, string folder, string powerShellArguments, LaunchContext context) => kind switch
    {
        ShellKind.Cmd => new LaunchPlan.Run("Command Prompt", Cmd(context), "", folder, Hidden: false),
        ShellKind.Pwsh when Find("pwsh.exe", context) is { } pwsh => new LaunchPlan.Run("PowerShell 7", pwsh, powerShellArguments, folder, Hidden: false),
        _ => new LaunchPlan.Run("Windows PowerShell", WindowsPowerShell(context), powerShellArguments, folder, Hidden: false),
    };

    private static string WindowsPowerShell(LaunchContext context) => WinPath.Combine(context.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");

    private static string WindowsDirectory(LaunchContext context) => WinPath.Parent(context.SystemDirectory) ?? context.SystemDirectory;

    private static LaunchPlan OpenInVsCode(LaunchIntent.OpenInVsCode intent, LaunchContext context)
    {
        // The Admin Instance never runs a command from settings.json: a process running as the user could point it at
        // its own program (S-09).
        var command = context.AdminInstance || string.IsNullOrWhiteSpace(intent.Command) ? "code" : intent.Command;
        var (name, rest) = SplitCommand(command);
        return Find(name, context, out var rejected) is { } exe
            ? Command("VS Code", exe, rest, intent.Path, context)
            : new LaunchPlan.Missing(rejected is null
                ? $"VS Code not found: '{name}' is not on PATH (integration.vsCodeCommand)"
                : NotInAdminInstance("VS Code", rejected));
    }

    private static LaunchPlan OpenInCopilotApp(LaunchIntent.OpenInCopilotApp intent, LaunchContext context)
    {
        // The Copilot app has no fixed default to fall back on, and its command comes from settings.json (S-09).
        if (context.AdminInstance)
        {
            return new LaunchPlan.Missing("Copilot app not available in the Admin Instance: its command comes from settings.json");
        }

        if (string.IsNullOrWhiteSpace(intent.CommandTemplate))
        {
            return new LaunchPlan.Missing("Copilot app not configured: set integration.copilotAppCommand in settings.json");
        }

        var (name, rest) = SplitCommand(intent.CommandTemplate);
        return context.FindOnPath(name) is { } exe
            ? Command("Copilot app", exe, rest, intent.Path, context)
            : new LaunchPlan.Missing($"Copilot app not found: '{name}' is not on PATH (integration.copilotAppCommand)");
    }

    /// <summary>
    /// Runs a resolved command for <paramref name="path"/>: an .exe directly, a .cmd/.bat through the system cmd.exe.
    /// Both get the system directory as working directory, so nothing the tool or cmd looks up relative to it can
    /// come from the browsed folder. "{path}" in <paramref name="rest"/> is replaced, otherwise the path is appended.
    /// </summary>
    private static LaunchPlan.Run Command(string tool, string exe, string rest, string path, LaunchContext context)
    {
        var batch = exe.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || exe.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);
        Func<string, string> quote = batch ? CommandLine.QuoteForCmd : CommandLine.Quote;
        var quotedPath = quote(path);
        var args = rest.Contains("{path}", StringComparison.Ordinal)
            ? rest.Replace("{path}", quotedPath, StringComparison.Ordinal)
            : rest.Length == 0 ? quotedPath : rest + " " + quotedPath;
        return batch
            ? new LaunchPlan.Run(tool, Cmd(context), $"/d /v:off /s /c \"{quote(exe)} {args}\"", context.SystemDirectory, Hidden: true)
            : new LaunchPlan.Run(tool, exe, args, context.SystemDirectory, Hidden: false);
    }

    /// <summary>
    /// The Admin Instance's terminal: the fixed defaults instead of the terminal app, Windows Terminal profile and
    /// fallback shell from settings.json; a profile's command line is any program (S-09).
    /// </summary>
    private static LaunchIntent.OpenTerminal AdminTerminal(LaunchIntent.OpenTerminal intent) => new(intent.Folder, "wt", "", "pwsh");

    private static LaunchPlan.Run OpenTerminal(LaunchIntent.OpenTerminal intent, LaunchContext context)
    {
        var app = intent.App.Trim().ToLowerInvariant();
        if (app == "wt" && Find("wt.exe", context) is { } wt)
        {
            var args = string.IsNullOrWhiteSpace(intent.WindowsTerminalProfile)
                ? ""
                : "-p " + CommandLine.Quote(intent.WindowsTerminalProfile.Replace(";", @"\;", StringComparison.Ordinal)) + " ";
            // wt splits its whole command line on ';' (escaped "\;" stays in the value), so a folder with ';' is passed
            // as the working directory and "-d ." instead.
            args += intent.Folder.Contains(';') ? "-d ." : "-d " + CommandLine.Quote(intent.Folder);
            return new LaunchPlan.Run("Windows Terminal", wt, args, intent.Folder, Hidden: false);
        }

        var shell = ShellKinds.Parse(app) ?? ShellKinds.Parse(intent.FallbackShell) ?? ShellKind.WindowsPowerShell;
        return ShellRun(shell, intent.Folder, "-NoExit", context);
    }

    private static string Cmd(LaunchContext context) => WinPath.Combine(context.SystemDirectory, "cmd.exe");

    /// <summary>The program of a command from settings (quoted, or up to the first space) and the rest of the line.</summary>
    private static (string Name, string Arguments) SplitCommand(string command)
    {
        var c = command.Trim();
        if (c.StartsWith('"'))
        {
            var end = c.IndexOf('"', 1);
            return end < 0 ? (c[1..], "") : (c[1..end], c[(end + 1)..].Trim());
        }

        var space = c.IndexOf(' ');
        return space < 0 ? (c, "") : (c[..space], c[(space + 1)..].Trim());
    }
}
