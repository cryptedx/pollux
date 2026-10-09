using System.Text.Json;
using GitHub.Copilot;
using GitHub.Copilot.Rpc;
using Pollux.Copilot.Chat;
using Pollux.Copilot.Tools;

namespace Pollux.Copilot.Tests.Chat;

/// <summary>
/// What an approval prompt offers and how long it waits. Spec: always-ask commands always ask and a Tainted Chat asks
/// until it is cleared, so "Allow for this Chat" exists only for questions of the Permission Mode.
/// </summary>
[TestClass]
public sealed class ApprovalPromptTests
{
#pragma warning disable GHCP001 // Experimental SDK API (PermissionDecision).
    private readonly List<ToolApprovalRequest> _asked = [];
    private readonly List<(string Id, ApprovalAnswer Answer)> _answered = [];
    private bool _tainted;
    private string _dir = "";
    private ActionLog _log = null!;

    [TestInitialize]
    public void Init()
    {
        _dir = Path.Combine(Path.GetTempPath(), "pollux-prompts-" + Guid.NewGuid().ToString("N"));
        _log = new ActionLogStore(_dir).Open("chat");
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private ToolApprovals Approvals(TimeSpan? timeout = null) =>
        new(_asked.Add, (id, a) => _answered.Add((id, a)), new ChatGuard(null, () => @"C:\work", () => _tainted, _ => null), _log,
            answerTimeout: timeout);

    private static PreToolUseHookInput Hook(string tool, string argsJson) =>
        new() { ToolName = tool, ToolArgs = JsonDocument.Parse(argsJson).RootElement.Clone(), WorkingDirectory = @"C:\work" };

    private static PreToolUseHookInput Shell(string command) =>
        Hook("powershell", $$"""{"command":{{JsonSerializer.Serialize(command, ChatGuardTestJson.Default.String)}}}""");

    private static PermissionRequestShell ShellRequest(string command) => new()
    {
        CanOfferSessionApproval = false, Commands = [], FullCommandText = command, HasWriteFileRedirection = false,
        Intention = "", PossiblePaths = [], PossibleUrls = [],
    };

    private static PermissionRequestCustomTool FilePermission(string tool, string argsJson) => new()
    {
        ToolName = tool, ToolDescription = "", Args = JsonDocument.Parse(argsJson).RootElement.Clone(),
    };

    private const string MoveOut = """{"sources":["C:\\work\\a.pdf"],"destination":"D:\\archive"}""";
    private const string MoveIn = """{"sources":["C:\\work\\a.pdf"],"destination":"C:\\work\\docs"}""";

    [TestMethod]
    public async Task AnAlwaysAskCommandDoesNotOfferAllowForThisChatAndAsksAgain()
    {
        var approvals = Approvals();
        var first = approvals.PreToolUseAsync(Shell("git push --force"));
        Assert.IsFalse(_asked.Single().CanAllowForChat);

        approvals.Answer(_asked.Single().Id, ApprovalAnswer.AllowForChat);
        Assert.AreEqual("allow", (await first)!.PermissionDecision);
        Assert.AreEqual(ApprovalAnswer.AllowOnce, _answered.Single().Answer, "the answer is taken as Allow once");

        _ = approvals.PreToolUseAsync(Shell("git push --force"));
        Assert.HasCount(2, _asked, "an always-ask command asks every time");
    }

    [TestMethod]
    public async Task ATaintedChatAsksAgainEvenForACommandAllowedForTheChatBefore()
    {
        var approvals = Approvals();
        var first = approvals.DecideAsync(ShellRequest("dotnet build"));
        Assert.IsTrue(_asked.Single().CanAllowForChat, "a plain Permission Mode prompt offers it");
        approvals.Answer(_asked.Single().Id, ApprovalAnswer.AllowForChat);
        await first;

        _tainted = true;
        _ = approvals.PreToolUseAsync(Shell("dotnet build"));

        Assert.HasCount(2, _asked);
        Assert.IsFalse(_asked[1].CanAllowForChat);
        StringAssert.Contains(_asked[1].Reason, "Tainted");
    }

    [TestMethod]
    public async Task ATaintedPermissionRequestDoesNotUseAnEarlierAllowForThisChat()
    {
        var approvals = Approvals();
        var first = approvals.DecideAsync(ShellRequest("dotnet build"));
        approvals.Answer(_asked.Single().Id, ApprovalAnswer.AllowForChat);
        await first;

        _tainted = true;
        _ = approvals.DecideAsync(ShellRequest("dotnet build"));

        Assert.HasCount(2, _asked);
    }

    [TestMethod]
    public async Task AFileToolTheGuardRulesAskAboutDoesNotOfferAllowForThisChat()
    {
        var approvals = Approvals();
        var call = PolluxToolCall.Of(FileOperation.FromToolCall("pollux_move", JsonDocument.Parse(MoveOut).RootElement)!);
        var first = approvals.AuthorizeAsync(call, CancellationToken.None);
        Assert.IsFalse(_asked.Single().CanAllowForChat);
        approvals.Answer(_asked.Single().Id, ApprovalAnswer.AllowForChat);
        Assert.IsNull(await first);

        _ = approvals.AuthorizeAsync(call, CancellationToken.None);

        Assert.HasCount(2, _asked);
    }

    [TestMethod]
    public async Task AFileToolThePermissionModeAsksAboutOffersAllowForThisChat()
    {
        var approvals = Approvals();
        var first = approvals.DecideAsync(FilePermission("pollux_move", MoveIn));
        Assert.IsTrue(_asked.Single().CanAllowForChat);
        approvals.Answer(_asked.Single().Id, ApprovalAnswer.AllowForChat);
        Assert.IsInstanceOfType<PermissionDecisionApproveOnce>(await first);

        Assert.IsInstanceOfType<PermissionDecisionApproveOnce>(await approvals.DecideAsync(FilePermission("pollux_move", MoveIn)));
        Assert.HasCount(1, _asked);
    }

    [TestMethod]
    public async Task AnUnansweredHookPromptIsDeniedAfterTheTimeoutAndLogged()
    {
        var approvals = Approvals(TimeSpan.FromMilliseconds(50));

        var output = await approvals.PreToolUseAsync(Shell("git push --force"));

        Assert.AreEqual("deny", output!.PermissionDecision);
        StringAssert.Contains(output.PermissionDecisionReason, "No answer");
        Assert.IsEmpty(approvals.Pending);
        Assert.AreEqual((_asked.Single().Id, ApprovalAnswer.Deny), _answered.Single(), "the Panel closes the prompt");
        var entry = _log.Entries.Single();
        Assert.AreEqual(ActionDecision.Blocked, entry.Decision);
        StringAssert.Contains(entry.Reason, "No answer");
    }

    [TestMethod]
    public async Task AnUnansweredPermissionPromptIsRejectedAfterTheTimeout()
    {
        var approvals = Approvals(TimeSpan.FromMilliseconds(50));

        var decision = await approvals.DecideAsync(ShellRequest("dotnet build"));

        Assert.IsInstanceOfType<PermissionDecisionReject>(decision);
        Assert.AreEqual(ActionDecision.Blocked, _log.Entries.Single().Decision);
    }

    [TestMethod]
    public async Task AnUnansweredFileToolPromptRefusesAfterTheTimeout()
    {
        var approvals = Approvals(TimeSpan.FromMilliseconds(50));
        var call = PolluxToolCall.Of(FileOperation.FromToolCall("pollux_move", JsonDocument.Parse(MoveOut).RootElement)!);

        StringAssert.Contains(await approvals.AuthorizeAsync(call, CancellationToken.None), "No answer");
    }

    [TestMethod]
    public void PromptsWaitFifteenMinutesByDefault() =>
        Assert.AreEqual(TimeSpan.FromMinutes(15), ToolApprovals.DefaultAnswerTimeout);
}
