using Pollux.Core.Launch;

namespace Pollux.Core.Tests.Launch;

/// <summary>
/// The launch plan in the Admin Instance (ADR 0008, SECURITY.md S-09): command and terminal settings come from the
/// user's settings.json, which any process running as the non-elevated user can change, so the elevated instance
/// ignores them and starts fixed tools.
/// </summary>
public sealed partial class LaunchPlannerTests
{
    /// <summary>What a process running as the user wrote into settings.json to run its own program elevated.</summary>
    private const string EvilExe = @"C:\Users\me\AppData\Local\Temp\evil.exe";

    /// <summary>Program Files and Program Files (x86): only administrators can write there.</summary>
    private static readonly string[] AdminOnlyRoots = [@"C:\Program Files", @"C:\Program Files (x86)"];

    /// <summary>VS Code and Windows Terminal installed for all users, under Program Files.</summary>
    private const string SystemCodeCmd = @"C:\Program Files\Microsoft VS Code\bin\code.cmd";
    private const string SystemWt = @"C:\Program Files\WindowsApps\Microsoft.WindowsTerminal_1.21.0.0_x64__8wekyb3d8bbwe\wt.exe";
    private const string SystemGit = @"C:\Program Files\Git\cmd\git.exe";

    private static LaunchContext AdminContext(params string[] installed) => Context(installed) with { AdminInstance = true, AdminOnlyRoots = AdminOnlyRoots };

    [TestMethod]
    public void S09_AdminVsCodeIgnoresTheCommandSettingAndRunsCode()
    {
        var run = Run(LaunchPlanner.Plan(new LaunchIntent.OpenInVsCode(Browsed, $"\"{EvilExe}\" --run"), AdminContext(SystemCodeCmd, EvilExe)));

        Assert.AreEqual(Cmd, run.Executable);
        Assert.AreEqual($"/d /v:off /s /c \"\"{SystemCodeCmd}\" \"{Browsed}\"\"", run.Arguments);
        Assert.IsFalse(run.Arguments.Contains("evil", StringComparison.OrdinalIgnoreCase), run.Arguments);
    }

    [TestMethod]
    public void S09_AdminCopilotAppIgnoresTheCommandSettingAndIsNotAvailable()
    {
        var plan = LaunchPlanner.Plan(new LaunchIntent.OpenInCopilotApp(Browsed, $"\"{EvilExe}\" {{path}}"), AdminContext(EvilExe));

        var missing = (LaunchPlan.Missing)plan;
        StringAssert.Contains(missing.Message, "Copilot app");
        StringAssert.Contains(missing.Message, "Admin Instance");
    }

    [TestMethod]
    public void S09_AdminTerminalIgnoresTheProfileAndOpensTheDefaultWindowsTerminal()
    {
        // A Windows Terminal profile's commandline is any program; the user's settings must not pick it.
        var run = Run(LaunchPlanner.Plan(new LaunchIntent.OpenTerminal(Browsed, "wt", "Evil", "pwsh"), AdminContext(SystemWt, Pwsh)));

        Assert.AreEqual(SystemWt, run.Executable);
        Assert.AreEqual($"-d \"{Browsed}\"", run.Arguments);
    }

    [TestMethod]
    public void S09_AdminTerminalIgnoresTheTerminalAppAndFallbackShellSettings()
    {
        Assert.AreEqual(SystemWt, Run(LaunchPlanner.Plan(new LaunchIntent.OpenTerminal(Browsed, "cmd", "", "cmd"), AdminContext(SystemWt, Pwsh))).Executable);
        // Without Windows Terminal: the fixed fallback (PowerShell 7, else Windows PowerShell), not the fallbackShell setting.
        Assert.AreEqual(Pwsh, Run(LaunchPlanner.Plan(new LaunchIntent.OpenTerminal(Browsed, "cmd", "", "cmd"), AdminContext(Pwsh))).Executable);
        Assert.AreEqual(WindowsPowerShell, Run(LaunchPlanner.Plan(new LaunchIntent.OpenTerminal(Browsed, "wt", "", "cmd"), AdminContext())).Executable);
    }

    [TestMethod]
    public void AdminOpenShellStillOpensTheShellTheUserPicked()
    {
        // "Open PowerShell here" names its shell in the command, not in settings.json.
        var run = Run(LaunchPlanner.Plan(new LaunchIntent.OpenShell(Browsed, "pwsh"), AdminContext(SystemWt, Pwsh)));
        Assert.AreEqual(Pwsh, run.Executable);
        Assert.AreEqual("-NoExit", run.Arguments);
        Assert.AreEqual(Browsed, run.WorkingDirectory);
    }

    // The elevated process's PATH includes the user's part (HKCU\Environment) and user-writable folders such as
    // %LOCALAPPDATA%\Microsoft\WindowsApps: a tool found there could have been planted by any process of the user.

    [TestMethod]
    public void S09_AdminVsCodeFromAUserWritableFolderIsNotAvailable()
    {
        var missing = (LaunchPlan.Missing)LaunchPlanner.Plan(new LaunchIntent.OpenInVsCode(Browsed, "code"), AdminContext(CodeCmd));

        StringAssert.Contains(missing.Message, "Admin Instance");
        StringAssert.Contains(missing.Message, CodeCmd);
    }

    [TestMethod]
    public void S09_AdminTerminalSkipsAWindowsTerminalFromAUserWritableFolder()
    {
        Assert.AreEqual(Pwsh, Run(LaunchPlanner.Plan(new LaunchIntent.OpenTerminal(Browsed, "wt", "", "pwsh"), AdminContext(Wt, Pwsh))).Executable);
        Assert.AreEqual(WindowsPowerShell, Run(LaunchPlanner.Plan(new LaunchIntent.OpenTerminal(Browsed, "wt", "", "pwsh"), AdminContext(Wt, @"C:\Users\me\bin\pwsh.exe"))).Executable);
    }

    [TestMethod]
    public void S09_AdminShellsSkipAPowerShell7FromAUserWritableFolder()
    {
        const string userPwsh = @"C:\Users\me\AppData\Local\Microsoft\WindowsApps\pwsh.exe";
        Assert.AreEqual(WindowsPowerShell, Run(LaunchPlanner.Plan(new LaunchIntent.OpenShell(Browsed, "pwsh"), AdminContext(userPwsh))).Executable);
        Assert.AreEqual(WindowsPowerShell, Run(LaunchPlanner.Plan(new LaunchIntent.EmbeddedTerminal(Browsed, "pwsh"), AdminContext(userPwsh))).Executable);
    }

    [TestMethod]
    public void S09_AdminHelpersRunOnlyFromAdminOnlyFolders()
    {
        Assert.AreEqual(SystemGit, Run(LaunchPlanner.Plan(new LaunchIntent.Git(Browsed, ["status"]), AdminContext(SystemGit))).Executable);

        var git = (LaunchPlan.Missing)LaunchPlanner.Plan(new LaunchIntent.Git(Browsed, ["status"]), AdminContext(@"C:\Users\me\scoop\shims\git.exe"));
        StringAssert.Contains(git.Message, "Admin Instance");

        var winget = (LaunchPlan.Missing)LaunchPlanner.Plan(new LaunchIntent.Winget(["list"]), AdminContext(@"C:\Users\me\AppData\Local\Microsoft\WindowsApps\winget.exe"));
        StringAssert.Contains(winget.Message, "Admin Instance");
    }

    [TestMethod]
    public void S09_AdminToolsUnderTheWindowsFolderAreAdminOnly()
    {
        const string systemWinget = @"C:\Windows\System32\winget.exe";
        Assert.AreEqual(systemWinget, Run(LaunchPlanner.Plan(new LaunchIntent.Winget(["list"]), AdminContext(systemWinget))).Executable);
    }

    [TestMethod]
    public void OutsideTheAdminInstanceTheSettingsStillApply()
    {
        Assert.AreEqual(Cmd, Run(LaunchPlanner.Plan(new LaunchIntent.OpenTerminal(Browsed, "cmd", "", "pwsh"), Context(Wt, Pwsh))).Executable);
        Assert.AreEqual(EvilExe, Run(LaunchPlanner.Plan(new LaunchIntent.OpenInVsCode(Browsed, $"\"{EvilExe}\""), Context(EvilExe))).Executable);
    }
}
