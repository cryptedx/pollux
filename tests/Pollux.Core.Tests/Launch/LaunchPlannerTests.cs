using Pollux.Core.Launch;

namespace Pollux.Core.Tests.Launch;

[TestClass]
public sealed partial class LaunchPlannerTests
{
    private const string System32 = @"C:\Windows\System32";
    private const string Cmd = @"C:\Windows\System32\cmd.exe";
    private const string WindowsPowerShell = @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe";
    private const string CodeCmd = @"C:\Users\me\AppData\Local\Programs\Microsoft VS Code\bin\code.cmd";
    private const string Pwsh = @"C:\Program Files\PowerShell\7\pwsh.exe";
    private const string Wt = @"C:\Users\me\AppData\Local\Microsoft\WindowsApps\wt.exe";

    /// <summary>The folder the user is browsing, with planted <c>code.cmd</c> and <c>pwsh.exe</c> (SECURITY.md S-02, S-08).</summary>
    private const string Browsed = @"C:\Users\me\Downloads\repo";

    private static readonly string[] Planted = [$@"{Browsed}\code.cmd", $@"{Browsed}\code.bat", $@"{Browsed}\pwsh.exe", $@"{Browsed}\wt.exe", $@"{Browsed}\cmd.exe", $@"{Browsed}\powershell.exe"];

    /// <summary>A PATH lookup over the given installed tools, keyed by file name with and without extension.</summary>
    private static LaunchContext Context(params string[] installed) => new(
        name => installed.FirstOrDefault(p =>
        {
            var file = p[(p.LastIndexOf('\\') + 1)..];
            return p.Equals(name, StringComparison.OrdinalIgnoreCase)
                || file.Equals(name, StringComparison.OrdinalIgnoreCase)
                || (file.Contains('.') && file[..file.LastIndexOf('.')].Equals(name, StringComparison.OrdinalIgnoreCase));
        }),
        System32);

    private static LaunchPlan.Run Run(LaunchPlan plan)
    {
        Assert.IsInstanceOfType<LaunchPlan.Run>(plan, (plan as LaunchPlan.Missing)?.Message);
        return (LaunchPlan.Run)plan;
    }

    private static void AssertNothingFrom(string folder, LaunchPlan.Run run)
    {
        Assert.IsFalse(run.Executable.StartsWith(folder, StringComparison.OrdinalIgnoreCase), run.Executable);
        foreach (var p in Planted) Assert.IsFalse(run.Arguments.Contains(p, StringComparison.OrdinalIgnoreCase), run.Arguments);
    }

    // ---- VS Code (S-02) ----

    [TestMethod]
    public void S02_VsCodeRunsTheCodeCmdFromPathThroughSystemCmdInANeutralFolder()
    {
        var run = Run(LaunchPlanner.Plan(new LaunchIntent.OpenInVsCode(Browsed, "code"), Context(CodeCmd)));

        Assert.AreEqual(Cmd, run.Executable);
        Assert.AreEqual(System32, run.WorkingDirectory);
        Assert.IsTrue(run.Hidden);
        Assert.AreEqual($"/d /v:off /s /c \"\"{CodeCmd}\" \"{Browsed}\"\"", run.Arguments);
        AssertNothingFrom(Browsed, run);
    }

    [TestMethod]
    public void S02_VsCodeForAFileNeverUsesTheFilesFolderAsWorkingDirectory()
    {
        var run = Run(LaunchPlanner.Plan(new LaunchIntent.OpenInVsCode($@"{Browsed}\README.md", "code"), Context(CodeCmd)));
        Assert.AreEqual(System32, run.WorkingDirectory);
        AssertNothingFrom(Browsed, run);
    }

    [TestMethod]
    public void VsCodeConfiguredAsAnExeRunsDirectly()
    {
        const string exe = @"D:\VSCode\Code.exe";
        var run = Run(LaunchPlanner.Plan(new LaunchIntent.OpenInVsCode(Browsed, "\"D:\\VSCode\\Code.exe\" --new-window"), Context(exe)));
        Assert.AreEqual(exe, run.Executable);
        Assert.AreEqual($"--new-window \"{Browsed}\"", run.Arguments);
        Assert.AreEqual(System32, run.WorkingDirectory);
        Assert.IsFalse(run.Hidden);
    }

    [TestMethod]
    public void MissingVsCodeNamesTheTool()
    {
        var plan = LaunchPlanner.Plan(new LaunchIntent.OpenInVsCode(Browsed, "code"), Context());
        var missing = (LaunchPlan.Missing)plan;
        StringAssert.Contains(missing.Message, "VS Code");
        StringAssert.Contains(missing.Message, "'code'");
    }

    [TestMethod]
    public void EmptyVsCodeSettingMeansCode()
    {
        Assert.AreEqual(Cmd, Run(LaunchPlanner.Plan(new LaunchIntent.OpenInVsCode(Browsed, " "), Context(CodeCmd))).Executable);
    }

    [TestMethod]
    public void S20_PercentInAFolderReachesVsCodeUnexpanded()
    {
        const string folder = @"C:\x\a%USERNAME%b & c^d";
        var run = Run(LaunchPlanner.Plan(new LaunchIntent.OpenInVsCode(folder, "code"), Context(CodeCmd)));
        // Verified in the Windows VM: cmd passes "C:\x\a"%"USERNAME"%"b & c^d" to code.cmd, which Code.exe reads as the folder.
        Assert.AreEqual($"/d /v:off /s /c \"\"{CodeCmd}\" \"C:\\x\\a\"^%\"USERNAME\"^%\"b & c^d\"\"", run.Arguments);
    }

    [TestMethod]
    public void TrailingBackslashDoesNotEscapeTheClosingQuote()
    {
        var run = Run(LaunchPlanner.Plan(new LaunchIntent.OpenInVsCode(@"D:\", "code"), Context(CodeCmd)));
        Assert.AreEqual($"/d /v:off /s /c \"\"{CodeCmd}\" \"D:\\\\\"\"", run.Arguments);
    }

    // ---- Copilot app (S-02) ----

    [TestMethod]
    public void S02_CopilotAppCommandIsResolvedOnPathAndNeverFromTheFolder()
    {
        const string app = @"C:\Users\me\AppData\Local\Programs\Copilot\copilot-app.exe";
        var run = Run(LaunchPlanner.Plan(new LaunchIntent.OpenInCopilotApp(Browsed, "copilot-app --open {path}"), Context(app)));
        Assert.AreEqual(app, run.Executable);
        Assert.AreEqual($"--open \"{Browsed}\"", run.Arguments);
        Assert.AreEqual(System32, run.WorkingDirectory);
        AssertNothingFrom(Browsed, run);
    }

    [TestMethod]
    public void CopilotAppCmdRunsThroughSystemCmd()
    {
        const string app = @"C:\Tools\copilot-app.cmd";
        var run = Run(LaunchPlanner.Plan(new LaunchIntent.OpenInCopilotApp(Browsed, "copilot-app"), Context(app)));
        Assert.AreEqual(Cmd, run.Executable);
        Assert.AreEqual($"/d /v:off /s /c \"\"{app}\" \"{Browsed}\"\"", run.Arguments);
        Assert.AreEqual(System32, run.WorkingDirectory);
        Assert.IsTrue(run.Hidden);
    }

    [TestMethod]
    public void CopilotAppNotConfiguredOrMissingNamesTheTool()
    {
        var unset = (LaunchPlan.Missing)LaunchPlanner.Plan(new LaunchIntent.OpenInCopilotApp(Browsed, ""), Context());
        StringAssert.Contains(unset.Message, "integration.copilotAppCommand");
        var missing = (LaunchPlan.Missing)LaunchPlanner.Plan(new LaunchIntent.OpenInCopilotApp(Browsed, "copilot-app {path}"), Context());
        StringAssert.Contains(missing.Message, "Copilot app");
        StringAssert.Contains(missing.Message, "'copilot-app'");
    }

    // ---- terminals (S-08, S-14) ----

    [TestMethod]
    public void S08_WindowsTerminalIsStartedByAbsolutePathInTheFolder()
    {
        var run = Run(LaunchPlanner.Plan(new LaunchIntent.OpenTerminal(Browsed, "wt", "", "pwsh"), Context(Wt, Pwsh)));
        Assert.AreEqual(Wt, run.Executable);
        Assert.AreEqual($"-d \"{Browsed}\"", run.Arguments);
        Assert.AreEqual(Browsed, run.WorkingDirectory);
        Assert.IsFalse(run.Hidden);
    }

    [TestMethod]
    public void S14_SemicolonFolderOpensWindowsTerminalThereWithoutASecondCommand()
    {
        const string folder = @"C:\x; cmd /c calc";
        var run = Run(LaunchPlanner.Plan(new LaunchIntent.OpenTerminal(folder, "wt", "", "pwsh"), Context(Wt)));
        Assert.AreEqual(Wt, run.Executable);
        Assert.IsFalse(run.Arguments.Contains(';'), run.Arguments);
        Assert.AreEqual("-d .", run.Arguments);
        Assert.AreEqual(folder, run.WorkingDirectory);
    }

    [TestMethod]
    public void S14_ProfileIsQuotedAndItsSemicolonsAndQuotesEscaped()
    {
        var run = Run(LaunchPlanner.Plan(new LaunchIntent.OpenTerminal(Browsed, "wt", "Dev \"x\"; calc", "pwsh"), Context(Wt)));
        Assert.AreEqual($"-p \"Dev \\\"x\\\"\\; calc\" -d \"{Browsed}\"", run.Arguments);
    }

    [TestMethod]
    public void S08_PwshComesFromPathNeverFromTheFolder()
    {
        var run = Run(LaunchPlanner.Plan(new LaunchIntent.OpenTerminal(Browsed, "pwsh", "", "pwsh"), Context(Pwsh)));
        Assert.AreEqual(Pwsh, run.Executable);
        Assert.AreEqual("-NoExit", run.Arguments);
        Assert.AreEqual(Browsed, run.WorkingDirectory);
        AssertNothingFrom(Browsed, run);
    }

    [TestMethod]
    public void S08_CmdAndWindowsPowerShellComeFromTheSystemDirectory()
    {
        var cmd = Run(LaunchPlanner.Plan(new LaunchIntent.OpenTerminal(Browsed, "cmd", "", "pwsh"), Context()));
        Assert.AreEqual(Cmd, cmd.Executable);
        Assert.AreEqual(Browsed, cmd.WorkingDirectory);
        var ps = Run(LaunchPlanner.Plan(new LaunchIntent.OpenTerminal(Browsed, "powershell", "", "pwsh"), Context()));
        Assert.AreEqual(WindowsPowerShell, ps.Executable);
        Assert.AreEqual("-NoExit", ps.Arguments);
    }

    [TestMethod]
    public void MissingWindowsTerminalOrPwshFallsBack()
    {
        Assert.AreEqual(Pwsh, Run(LaunchPlanner.Plan(new LaunchIntent.OpenTerminal(Browsed, "wt", "", "pwsh"), Context(Pwsh))).Executable);
        Assert.AreEqual(Cmd, Run(LaunchPlanner.Plan(new LaunchIntent.OpenTerminal(Browsed, "wt", "", "cmd"), Context(Pwsh))).Executable);
        Assert.AreEqual(WindowsPowerShell, Run(LaunchPlanner.Plan(new LaunchIntent.OpenTerminal(Browsed, "wt", "", "pwsh"), Context())).Executable);
        Assert.AreEqual(WindowsPowerShell, Run(LaunchPlanner.Plan(new LaunchIntent.OpenTerminal(Browsed, "pwsh", "", "pwsh"), Context())).Executable);
    }

    [TestMethod]
    public void UnknownTerminalAppUsesTheFallbackShell()
    {
        Assert.AreEqual(Cmd, Run(LaunchPlanner.Plan(new LaunchIntent.OpenTerminal(Browsed, "evil.exe", "", "cmd"), Context())).Executable);
    }
}
