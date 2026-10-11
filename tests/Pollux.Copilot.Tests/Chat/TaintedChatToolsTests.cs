using System.Text.Json;
using GitHub.Copilot;
using Pollux.Copilot.Chat;

namespace Pollux.Copilot.Tests.Chat;

/// <summary>
/// SECURITY.md S-12: a Tainted Chat asks before every tool call that is not a plain read, including tools without path
/// arguments and navigating Pollux. Only tools that read or merely steer the session itself go on without a prompt.
/// </summary>
[TestClass]
public sealed class TaintedChatToolsTests
{
    private readonly List<ToolApprovalRequest> _asked = [];
    private string _dir = "";
    private ActionLog _log = null!;

    [TestInitialize]
    public void Init()
    {
        _dir = Path.Combine(Path.GetTempPath(), "pollux-tainted-" + Guid.NewGuid().ToString("N"));
        _log = new ActionLogStore(_dir).Open("chat");
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private ToolApprovals Approvals(bool tainted) =>
        new(_asked.Add, guard: new ChatGuard(null, () => @"C:\work", () => tainted, _ => null), log: _log, answerTimeout: TimeSpan.FromMilliseconds(200));

    private static PreToolUseHookInput Hook(string tool, string argsJson) =>
        new() { ToolName = tool, ToolArgs = JsonDocument.Parse(argsJson).RootElement.Clone(), WorkingDirectory = @"C:\work" };

    [TestMethod]
    [DataRow("pollux_navigate", """{"location":"C:\\work\\docs"}""")]
    [DataRow("pollux_navigate", """{"location":"home"}""")]
    [DataRow("pollux_select_entries", """{"names":["a.txt"]}""")]
    [DataRow("some_mcp_tool", """{"query":"x"}""")]
    [DataRow("task", """{"prompt":"clean up the repo"}""")]
    [DataRow("exit_plan_mode", """{"plan":"x"}""")]
    public async Task ATaintedChatAsksBeforeAToolThatIsNotAPlainRead(string tool, string args)
    {
        _ = Approvals(tainted: true).PreToolUseAsync(Hook(tool, args));
        await Task.Yield();

        Assert.HasCount(1, _asked, tool);
        StringAssert.Contains(_asked[0].Reason, "Tainted", tool);
    }

    [TestMethod]
    [DataRow("report_intent", """{"intent":"Reading files"}""")]
    [DataRow("ask_user", """{"question":"Which one?"}""")]
    [DataRow("task_complete", """{"summary":"done"}""")]
    [DataRow("read_agent", """{"agent_id":"a1"}""")]
    [DataRow("list_agents", "{}")]
    [DataRow("pollux_read_panes", "{}")]
    [DataRow("view", """{"path":"C:\\work\\README.md"}""")]
    public async Task ATaintedChatLetsReadsAndSteeringToolsGo(string tool, string args)
    {
        var output = await Approvals(tainted: true).PreToolUseAsync(Hook(tool, args));

        Assert.IsEmpty(_asked, tool);
        Assert.IsNull(output, tool);
    }

    [TestMethod]
    public async Task AnUntaintedChatLetsToolsWithoutPathsGo()
    {
        var output = await Approvals(tainted: false).PreToolUseAsync(Hook("pollux_select_entries", """{"names":["a.txt"]}"""));

        Assert.IsEmpty(_asked);
        Assert.IsNull(output);
    }
}
