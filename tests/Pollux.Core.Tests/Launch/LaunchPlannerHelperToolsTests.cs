using Pollux.Core.Launch;

namespace Pollux.Core.Tests.Launch;

/// <summary>Helper tools started through the launch plan by absolute path (SECURITY.md S-15).</summary>
public sealed partial class LaunchPlannerTests
{
    private const string Git = @"C:\Program Files\Git\cmd\git.exe";
    private const string Winget = @"C:\Users\me\AppData\Local\Microsoft\WindowsApps\winget.exe";
    private const string Repo = @"C:\Users\me\src\repo";

    [TestMethod]
    public void S15_GitRunsTheGitFromPathInTheRepository()
    {
        var run = Run(LaunchPlanner.Plan(new LaunchIntent.Git(Repo, ["-c", "core.hooksPath=NUL", "status", "--porcelain=v1"]), Context(Git)));
        Assert.AreEqual(Git, run.Executable);
        Assert.AreEqual("\"-c\" \"core.hooksPath=NUL\" \"status\" \"--porcelain=v1\"", run.Arguments);
        Assert.AreEqual(Repo, run.WorkingDirectory);
        Assert.IsTrue(run.Hidden);
        Assert.IsFalse(run.Elevated);
    }

    [TestMethod]
    public void S15_WithoutGitOnPathThereIsNothingToRun()
    {
        // The lookup never returns a file planted in the repository or Pollux's folder (PathLookup excludes them).
        var plan = LaunchPlanner.Plan(new LaunchIntent.Git(Repo, ["status"]), Context());
        StringAssert.Contains(((LaunchPlan.Missing)plan).Message, "git");
    }

    [TestMethod]
    public void S15_WingetRunsFromPathInTheSystemDirectory()
    {
        var run = Run(LaunchPlanner.Plan(new LaunchIntent.Winget(["upgrade", "--id", "Foo.Bar", "--exact"]), Context(Winget)));
        Assert.AreEqual(Winget, run.Executable);
        Assert.AreEqual("\"upgrade\" \"--id\" \"Foo.Bar\" \"--exact\"", run.Arguments);
        Assert.AreEqual(System32, run.WorkingDirectory);
        Assert.IsTrue(run.Hidden);
    }

    [TestMethod]
    public void S15_MissingWingetNamesTheTool()
    {
        var missing = (LaunchPlan.Missing)LaunchPlanner.Plan(new LaunchIntent.Winget(["list"]), Context());
        StringAssert.Contains(missing.Message, "winget");
        StringAssert.Contains(missing.Message, "App Installer");
    }

    private static string Script(LaunchPlan.Run run)
    {
        const string prefix = "-NoProfile -EncodedCommand ";
        Assert.StartsWith(prefix, run.Arguments);
        return System.Text.Encoding.Unicode.GetString(Convert.FromBase64String(run.Arguments[prefix.Length..]));
    }

    [TestMethod]
    public void S15_ElevatedWingetRunsWindowsPowerShellFromTheSystemDirectoryWithAnEncodedScript()
    {
        var run = Run(LaunchPlanner.Plan(new LaunchIntent.ElevatedWinget(["Foo.Bar", "Baz-1_x+"]), Context(Winget)));
        Assert.AreEqual(WindowsPowerShell, run.Executable);
        Assert.AreEqual(System32, run.WorkingDirectory);
        Assert.IsTrue(run.Elevated);
        Assert.IsFalse(run.Hidden);
        Assert.AreEqual(
            $"& '{Winget}' upgrade --id 'Foo.Bar' --exact --accept-source-agreements --accept-package-agreements; "
            + $"& '{Winget}' upgrade --id 'Baz-1_x+' --exact --accept-source-agreements --accept-package-agreements; Read-Host 'Done. Press Enter to close'",
            Script(run));
    }

    [TestMethod]
    public void S15_ElevatedWingetLeavesOutUnsafeIds()
    {
        var script = Script(Run(LaunchPlanner.Plan(new LaunchIntent.ElevatedWinget(["Foo.Bar", "x'; calc; '", "Short…", ""]), Context(Winget))));
        Assert.DoesNotContain("calc", script);
        Assert.DoesNotContain("Short", script);
        Assert.Contains("'Foo.Bar'", script);
    }

    [TestMethod]
    public void S15_ElevatedWingetWithoutSafeIdsOrWingetIsMissing()
    {
        Assert.IsInstanceOfType<LaunchPlan.Missing>(LaunchPlanner.Plan(new LaunchIntent.ElevatedWinget(["a b"]), Context(Winget)));
        StringAssert.Contains(((LaunchPlan.Missing)LaunchPlanner.Plan(new LaunchIntent.ElevatedWinget(["Foo.Bar"]), Context())).Message, "winget");
    }

    [TestMethod]
    public void S15_ElevatedWingetQuotesAWingetPathWithAnApostrophe()
    {
        const string odd = @"C:\Users\o'neil\AppData\Local\Microsoft\WindowsApps\winget.exe";
        var script = Script(Run(LaunchPlanner.Plan(new LaunchIntent.ElevatedWinget(["Foo.Bar"]), Context(odd))));
        Assert.StartsWith(@"& 'C:\Users\o''neil\", script);
    }

    [TestMethod]
    public void S15_JunctionRunsSystemCmdInTheSystemDirectory()
    {
        var run = Run(LaunchPlanner.Plan(new LaunchIntent.CreateJunction(@"C:\Users\me\Downloads\link", @"D:\data\a%B%"), Context()));
        Assert.AreEqual(Cmd, run.Executable);
        Assert.AreEqual(System32, run.WorkingDirectory);
        Assert.IsTrue(run.Hidden);
        Assert.AreEqual("/d /v:off /c mklink /J \"C:\\Users\\me\\Downloads\\link\" \"D:\\data\\a\"^%\"B\"^%\"\"", run.Arguments);
        AssertNothingFrom(@"C:\Users\me\Downloads", run);
    }

    [TestMethod]
    public void S15_CopilotLoginRunsTheCliThroughSystemCmdInTheSystemDirectory()
    {
        const string cli = @"C:\Users\me\AppData\Local\Pollux\copilot\copilot.exe";
        var run = Run(LaunchPlanner.Plan(new LaunchIntent.CopilotLogin(cli), Context()));
        Assert.AreEqual(Cmd, run.Executable);
        Assert.AreEqual(System32, run.WorkingDirectory);
        Assert.IsFalse(run.Hidden);
        Assert.AreEqual($"/d /v:off /s /k \"\"{cli}\" login\"", run.Arguments);
    }

    [TestMethod]
    public void S15_EmbeddedTerminalUsesAbsoluteShells()
    {
        var pwsh = Run(LaunchPlanner.Plan(new LaunchIntent.EmbeddedTerminal(Browsed, "pwsh"), Context(Pwsh)));
        Assert.AreEqual(Pwsh, pwsh.Executable);
        Assert.AreEqual("-NoLogo", pwsh.Arguments);
        Assert.AreEqual(Browsed, pwsh.WorkingDirectory);

        Assert.AreEqual(WindowsPowerShell, Run(LaunchPlanner.Plan(new LaunchIntent.EmbeddedTerminal(Browsed, "pwsh"), Context())).Executable);
        var ps = Run(LaunchPlanner.Plan(new LaunchIntent.EmbeddedTerminal(Browsed, "powershell"), Context(Pwsh)));
        Assert.AreEqual(WindowsPowerShell, ps.Executable);
        Assert.AreEqual("-NoLogo", ps.Arguments);
        var cmd = Run(LaunchPlanner.Plan(new LaunchIntent.EmbeddedTerminal(Browsed, "cmd"), Context()));
        Assert.AreEqual(Cmd, cmd.Executable);
        Assert.AreEqual("", cmd.Arguments);
    }

    [TestMethod]
    public void S15_OpenWithAndRevealUseSystemPrograms()
    {
        var with = Run(LaunchPlanner.Plan(new LaunchIntent.OpenWith(@"C:\x\a b.txt"), Context()));
        Assert.AreEqual(@"C:\Windows\System32\rundll32.exe", with.Executable);
        Assert.AreEqual(@"shell32.dll,OpenAs_RunDLL C:\x\a b.txt", with.Arguments);
        Assert.AreEqual(System32, with.WorkingDirectory);

        var reveal = Run(LaunchPlanner.Plan(new LaunchIntent.RevealInExplorer(@"C:\x\a b.txt"), Context()));
        Assert.AreEqual(@"C:\Windows\explorer.exe", reveal.Executable);
        Assert.AreEqual(@"/select,""C:\x\a b.txt""", reveal.Arguments);
        Assert.AreEqual(@"C:\Windows", reveal.WorkingDirectory);
    }
}
