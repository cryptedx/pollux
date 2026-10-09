using System.Text.Json;
using GitHub.Copilot;
using Pollux.Copilot.Chat;

namespace Pollux.Copilot.Tests.Chat;

/// <summary>
/// The Guard Rules hook must see every tool call before it runs. <see cref="ToolApprovals.ToolStarted"/> tells, for a
/// tool the CLI started, whether the hook saw that call; a call it never saw is written to the Action Log.
/// </summary>
[TestClass]
public sealed class HookWatchdogTests
{
    private string _dir = "";
    private ActionLog _log = null!;

    [TestInitialize]
    public void Init()
    {
        _dir = Path.Combine(Path.GetTempPath(), "pollux-watchdog-" + Guid.NewGuid().ToString("N"));
        _log = new ActionLogStore(_dir).Open("chat");
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private ToolApprovals Approvals() => new(_ => { }, guard: new ChatGuard(null, () => @"C:\work", environment: _ => null), log: _log);

    private static PreToolUseHookInput Hook(string tool, string argsJson) =>
        new() { ToolName = tool, ToolArgs = JsonDocument.Parse(argsJson).RootElement.Clone() };

    [TestMethod]
    public async Task ACallTheHookSawIsKnownOnce()
    {
        var approvals = Approvals();
        await approvals.PreToolUseAsync(Hook("view", """{"path":"C:\\work\\a.txt"}"""));

        Assert.IsTrue(approvals.ToolStarted("view", """{"path":"C:\\work\\a.txt"}"""));
        Assert.IsFalse(approvals.ToolStarted("view", """{"path":"C:\\work\\a.txt"}"""), "Each hook call vouches for one tool start.");
    }

    [TestMethod]
    public async Task TheSameArgumentsWrittenDifferentlyAreTheSameCall()
    {
        var approvals = Approvals();
        await approvals.PreToolUseAsync(Hook("glob", """{"pattern":"*.cs","path":"C:\\work"}"""));

        Assert.IsTrue(approvals.ToolStarted("glob", """{ "path" : "C:\\work", "pattern" : "*.cs" }"""));
    }

    [TestMethod]
    public async Task ACallTheHookBlockedCountsAsSeen()
    {
        var approvals = Approvals();
        await approvals.PreToolUseAsync(Hook("powershell", """{"command":"format C: /q"}"""));

        Assert.IsTrue(approvals.ToolStarted("powershell", """{"command":"format C: /q"}"""));
    }

    [TestMethod]
    public async Task ACallTheHookNeverSawIsReportedAndLogged()
    {
        var approvals = Approvals();
        await approvals.PreToolUseAsync(Hook("view", """{"path":"C:\\work\\a.txt"}"""));

        Assert.IsFalse(approvals.ToolStarted("powershell", """{"command":"Remove-Item C:\\work\\x"}"""));

        var entry = _log.Entries[^1];
        Assert.AreEqual("powershell", entry.Tool);
        Assert.AreEqual(@"Remove-Item C:\work\x", entry.Command);
        Assert.AreEqual(ToolApprovals.HookDidNotRun, entry.Reason);
    }

    [TestMethod]
    public void AToolStartWithoutArgumentsIsStillChecked()
    {
        Assert.IsFalse(Approvals().ToolStarted("report_intent", null));
    }
}
