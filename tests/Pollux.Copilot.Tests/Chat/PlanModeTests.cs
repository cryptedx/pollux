using System.Text.Json;
using GitHub.Copilot;
using GitHub.Copilot.Rpc;
using Pollux.Copilot.Chat;
using Pollux.Copilot.Tools;

namespace Pollux.Copilot.Tests.Chat;

/// <summary>Spec "Agent Mode": in Plan mode only read tools are available, also after a switch in the middle of a Chat.</summary>
[TestClass]
public sealed class PlanModeTests
{
#pragma warning disable GHCP001 // Experimental SDK API (PermissionDecision).
    private readonly List<ToolApprovalRequest> _asked = [];
    private bool _plan = true;
    private string _dir = "";
    private ActionLog _log = null!;

    [TestInitialize]
    public void Init()
    {
        _dir = Path.Combine(Path.GetTempPath(), "pollux-plan-" + Guid.NewGuid().ToString("N"));
        _log = new ActionLogStore(_dir).Open("chat");
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private ToolApprovals Approvals() =>
        new(_asked.Add, guard: new ChatGuard(null, () => @"C:\work", environment: Environment), log: _log, readOnly: () => _plan);

    private static string? Environment(string name) => name switch
    {
        "USERPROFILE" => @"C:\Users\me",
        "APPDATA" => @"C:\Users\me\AppData\Roaming",
        "LOCALAPPDATA" => @"C:\Users\me\AppData\Local",
        _ => null,
    };

    private static PreToolUseHookInput Hook(string tool, string argsJson) =>
        new() { ToolName = tool, ToolArgs = JsonDocument.Parse(argsJson).RootElement.Clone(), WorkingDirectory = @"C:\work" };

    [TestMethod]
    [DataRow("powershell", """{"command":"git status"}""")]
    [DataRow("edit", """{"path":"C:\\work\\a.txt"}""")]
    [DataRow("create", """{"path":"C:\\work\\b.txt"}""")]
    [DataRow("pollux_navigate", """{"location":"C:\\work"}""")]
    [DataRow("pollux_select_entries", """{"patterns":["*.pdf"]}""")]
    [DataRow("pollux_copy", """{"sources":["C:\\work\\a.pdf"],"destination":"C:\\work\\docs"}""")]
    [DataRow("some_mcp_tool", """{"name":"x"}""")]
    public async Task ToolsThatCanChangeSomethingAreDeniedInPlanMode(string tool, string args)
    {
        var output = await Approvals().PreToolUseAsync(Hook(tool, args));

        Assert.AreEqual("deny", output?.PermissionDecision, tool);
        StringAssert.Contains(output!.PermissionDecisionReason, "Plan");
        Assert.IsEmpty(_asked);
        var entry = _log.Entries.Single();
        Assert.AreEqual(ActionDecision.Blocked, entry.Decision);
        Assert.AreEqual(ActionDecider.Mode, entry.DecidedBy);
    }

    [TestMethod]
    [DataRow("view", """{"path":"C:\\work\\a.txt"}""")]
    [DataRow("grep", """{"pattern":"x"}""")]
    [DataRow("glob", """{"pattern":"**/*.cs"}""")]
    [DataRow("pollux_read_panes", "{}")]
    [DataRow("report_intent", """{"intent":"Reading"}""")]
    [DataRow("exit_plan_mode", """{"summary":"x"}""")]
    public async Task ReadToolsStayAvailableInPlanMode(string tool, string args) =>
        Assert.IsNull(await Approvals().PreToolUseAsync(Hook(tool, args)), tool);

    [TestMethod]
    public async Task ASwitchToPlanModeInTheMiddleOfAChatAppliesToTheNextCall()
    {
        _plan = false;
        var approvals = Approvals();
        Assert.IsNull(await approvals.PreToolUseAsync(Hook("powershell", """{"command":"git status"}""")));

        _plan = true;

        Assert.AreEqual("deny", (await approvals.PreToolUseAsync(Hook("powershell", """{"command":"git status"}""")))?.PermissionDecision);
    }

    [TestMethod]
    public async Task ThePermissionRequestFallbackRejectsWritesInPlanMode()
    {
        var decision = await Approvals().DecideAsync(new PermissionRequestWrite
        {
            CanOfferSessionApproval = false, Diff = "", FileName = @"C:\work\a.txt", Intention = "",
        });

        Assert.IsInstanceOfType<PermissionDecisionReject>(decision);
        Assert.IsEmpty(_asked);
    }

    [TestMethod]
    public async Task APolluxFileToolRefusesInPlanModeRightBeforeActing()
    {
        var call = PolluxToolCall.Of(FileOperation.FromToolCall("pollux_new_folder", JsonDocument.Parse("""{"parent":"C:\\work","name":"new"}""").RootElement)!);

        var refusal = await Approvals().AuthorizeAsync(call, CancellationToken.None);

        StringAssert.Contains(refusal, "Plan");
    }

    [TestMethod]
    public void TheGateTellsTheNavigationToolsWhenThePanesMayNotChange()
    {
        var approvals = Approvals();
        Assert.IsNotNull(approvals.ReadOnlyReason);
        _plan = false;
        Assert.IsNull(approvals.ReadOnlyReason);
    }
}
