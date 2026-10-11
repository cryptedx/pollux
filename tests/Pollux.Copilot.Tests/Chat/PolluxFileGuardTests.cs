using System.Text.Json;
using GitHub.Copilot;
using GitHub.Copilot.Rpc;
using Pollux.Copilot.Chat;
using Pollux.Copilot.Tools;
using Pollux.Core.Settings;

namespace Pollux.Copilot.Tests.Chat;

/// <summary>
/// Guard Rules on the Pollux file tools through <see cref="ToolApprovals"/>: the tool's own check right before Pollux acts
/// (<see cref="IPolluxToolGate"/>), the pre-tool-use hook and the CLI's permission request, with one prompt per call.
/// </summary>
[TestClass]
public sealed class PolluxFileGuardTests
{
#pragma warning disable GHCP001 // Experimental SDK API (PermissionDecision).
    private readonly List<ToolApprovalRequest> _asked = [];
    private string? _workingFolder = @"C:\work";
    private bool _tainted;

    private static readonly Dictionary<string, string> Env = new(StringComparer.OrdinalIgnoreCase)
    {
        ["USERPROFILE"] = @"C:\Users\me", ["APPDATA"] = @"C:\Users\me\AppData\Roaming", ["LOCALAPPDATA"] = @"C:\Users\me\AppData\Local",
    };

    private ToolApprovals Approvals() =>
        new(_asked.Add, guard: new ChatGuard(new GuardRuleSettings(), () => _workingFolder, () => _tainted, n => Env.GetValueOrDefault(n)));

    private static FileOperation Op(string tool, string argsJson) =>
        FileOperation.FromToolCall(tool, JsonDocument.Parse(argsJson).RootElement)!;

    private static PolluxToolCall Call(string tool, string argsJson) => PolluxToolCall.Of(Op(tool, argsJson));

    private static PreToolUseHookInput Hook(string tool, string argsJson) =>
        new() { ToolName = tool, ToolArgs = JsonDocument.Parse(argsJson).RootElement.Clone(), WorkingDirectory = @"C:\work" };

    private static PermissionRequestCustomTool Permission(string tool, string argsJson) => new()
    {
        ToolName = tool, ToolDescription = "", Args = JsonDocument.Parse(argsJson).RootElement.Clone(),
    };

    private const string MoveOut = """{"sources":["C:\\work\\a.pdf"],"destination":"D:\\archive"}""";
    private const string MoveIn = """{"sources":["C:\\work\\a.pdf"],"destination":"C:\\work\\docs"}""";

    [TestMethod]
    public async Task AWriteInsideTheWorkingFolderGoesAheadWithoutAsking()
    {
        Assert.IsNull(await Approvals().AuthorizeAsync(Call("pollux_move", MoveIn), TestContext.CancellationToken));
        Assert.IsEmpty(_asked);
    }

    [TestMethod]
    public async Task AWriteOutsideTheWorkingFolderAsksWithReasonAndPaths()
    {
        var approvals = Approvals();

        var decision = approvals.AuthorizeAsync(Call("pollux_move", MoveOut), TestContext.CancellationToken);

        var prompt = _asked.Single();
        Assert.AreEqual("write", prompt.Kind);
        Assert.AreEqual("Move 1 entry", prompt.Title);
        Assert.AreEqual(@"move C:\work\a.pdf -> D:\archive", prompt.Detail);
        StringAssert.Contains(prompt.Reason, "outside the Working Folder");
        CollectionAssert.AreEqual(new[] { @"C:\work\a.pdf", @"D:\archive\a.pdf" }, prompt.Paths!.ToArray());
        approvals.Answer(prompt.Id, ApprovalAnswer.AllowOnce);
        Assert.IsNull(await decision);
    }

    [TestMethod]
    public async Task DecliningTheRecycleBinPromptKeepsTheFiles()
    {
        _workingFolder = null;
        var approvals = Approvals();

        var decision = approvals.AuthorizeAsync(Call("pollux_recycle", """{"paths":["C:\\work\\x"]}"""), TestContext.CancellationToken);
        approvals.Answer(_asked.Single().Id, ApprovalAnswer.Deny);

        Assert.AreEqual(ToolApprovals.DeclinedByUser, await decision);
    }

    [TestMethod]
    public async Task CopyingAProtectedSecretIsRefusedWithoutAsking()
    {
        var refusal = await Approvals().AuthorizeAsync(
            Call("pollux_copy", """{"sources":["C:\\Users\\me\\.ssh\\id_rsa"],"destination":"C:\\work"}"""), TestContext.CancellationToken);

        StringAssert.StartsWith(refusal, "Guard Rules: Protected secret");
        Assert.IsEmpty(_asked);
    }

    [TestMethod]
    public async Task BookmarkingAProtectedSecretIsRefusedButOtherBookmarksNeedNoPrompt()
    {
        var approvals = Approvals();

        Assert.IsNotNull(await approvals.AuthorizeAsync(Call("pollux_add_bookmark", """{"path":"C:\\Users\\me\\.aws"}"""), TestContext.CancellationToken));
        Assert.IsNull(await approvals.AuthorizeAsync(Call("pollux_add_bookmark", """{"path":"D:\\projects"}"""), TestContext.CancellationToken));
        Assert.IsEmpty(_asked);
    }

    [TestMethod]
    public async Task ATaintedChatAsksBeforeEveryFileChange()
    {
        _tainted = true;
        var approvals = Approvals();

        _ = approvals.AuthorizeAsync(Call("pollux_new_folder", """{"parent":"C:\\work","name":"x"}"""), TestContext.CancellationToken);

        StringAssert.Contains(_asked.Single().Reason, "Tainted");
    }

    [TestMethod]
    public async Task AGuardRuleQuestionIsAskedAgainForTheSameCall()
    {
        var approvals = Approvals();
        var first = approvals.AuthorizeAsync(Call("pollux_move", MoveOut), TestContext.CancellationToken);
        approvals.Answer(_asked.Single().Id, ApprovalAnswer.AllowForChat);
        await first;

        _ = approvals.AuthorizeAsync(Call("pollux_move", MoveOut), TestContext.CancellationToken);
        Assert.HasCount(2, _asked);
    }

    [TestMethod]
    public async Task TheHookAsksOnceAndThePermissionRequestAndTheToolDoNotAskAgain()
    {
        var approvals = Approvals();

        var hook = approvals.PreToolUseAsync(Hook("pollux_move", MoveOut));
        approvals.Answer(_asked.Single().Id, ApprovalAnswer.AllowOnce);
        Assert.AreEqual("allow", (await hook)!.PermissionDecision);

        Assert.IsInstanceOfType<PermissionDecisionApproveOnce>(await approvals.DecideAsync(Permission("pollux_move", MoveOut)));
        Assert.IsNull(await approvals.AuthorizeAsync(Call("pollux_move", MoveOut), TestContext.CancellationToken));
        Assert.HasCount(1, _asked);

        // The approval was for that one call: the same call again asks again.
        _ = approvals.AuthorizeAsync(Call("pollux_move", MoveOut), TestContext.CancellationToken);
        Assert.HasCount(2, _asked);
    }

    [TestMethod]
    public async Task TwoIdenticalCallsInParallelAreAskedOncePerCallAtEveryCheckpoint()
    {
        var approvals = Approvals();

        // Both calls reach the hook before either reaches the tool: one yes covers only the first.
        var hookA = approvals.PreToolUseAsync(Hook("pollux_move", MoveOut));
        approvals.Answer(_asked.Single().Id, ApprovalAnswer.AllowOnce);
        Assert.AreEqual("allow", (await hookA)!.PermissionDecision);
        var hookB = approvals.PreToolUseAsync(Hook("pollux_move", MoveOut));
        Assert.HasCount(2, _asked, "The second call's hook asks; the first call's yes does not cover it.");
        approvals.Answer(_asked[1].Id, ApprovalAnswer.AllowOnce);
        Assert.AreEqual("allow", (await hookB)!.PermissionDecision);

        Assert.IsInstanceOfType<PermissionDecisionApproveOnce>(await approvals.DecideAsync(Permission("pollux_move", MoveOut)));
        Assert.IsInstanceOfType<PermissionDecisionApproveOnce>(await approvals.DecideAsync(Permission("pollux_move", MoveOut)));
        Assert.IsNull(await approvals.AuthorizeAsync(Call("pollux_move", MoveOut), TestContext.CancellationToken));
        Assert.IsNull(await approvals.AuthorizeAsync(Call("pollux_move", MoveOut), TestContext.CancellationToken));
        Assert.HasCount(2, _asked, "Each call is asked once across hook, permission request and tool.");
    }

    [TestMethod]
    public async Task OneYesPassesThePermissionRequestOfOnlyOneCall()
    {
        var approvals = Approvals();
        var hook = approvals.PreToolUseAsync(Hook("pollux_move", MoveOut));
        approvals.Answer(_asked.Single().Id, ApprovalAnswer.AllowOnce);
        await hook;

        Assert.IsInstanceOfType<PermissionDecisionApproveOnce>(await approvals.DecideAsync(Permission("pollux_move", MoveOut)));
        var second = approvals.DecideAsync(Permission("pollux_move", MoveOut));
        Assert.HasCount(2, _asked, "A second permission request for the same call is a second call: it asks.");
        approvals.Answer(_asked[1].Id, ApprovalAnswer.Deny);
        Assert.IsInstanceOfType<PermissionDecisionReject>(await second);
    }

    [TestMethod]
    public async Task TheHookDeniesABlockedFileToolCallWithoutAsking()
    {
        var output = await Approvals().PreToolUseAsync(Hook("pollux_recycle", """{"paths":["C:\\work\\.env"]}"""));

        Assert.AreEqual("deny", output!.PermissionDecision);
        Assert.IsEmpty(_asked);
    }

    [TestMethod]
    public async Task TheHookLeavesAnAllowedFileToolCallToThePermissionMode() =>
        Assert.IsNull(await Approvals().PreToolUseAsync(Hook("pollux_move", MoveIn)));

    [TestMethod]
    public async Task WithoutTheHookThePermissionRequestAsksWithTheReasonAndTheToolDoesNotAskAgain()
    {
        var approvals = Approvals();

        var decision = approvals.DecideAsync(Permission("pollux_move", MoveOut));
        var prompt = _asked.Single();
        StringAssert.Contains(prompt.Reason, "outside the Working Folder");
        Assert.AreEqual(@"move C:\work\a.pdf -> D:\archive", prompt.Detail);
        approvals.Answer(prompt.Id, ApprovalAnswer.AllowOnce);

        Assert.IsInstanceOfType<PermissionDecisionApproveOnce>(await decision);
        Assert.IsNull(await approvals.AuthorizeAsync(Call("pollux_move", MoveOut), TestContext.CancellationToken));
        Assert.HasCount(1, _asked);
    }

    [TestMethod]
    public async Task InManualModeAnAllowedFileToolCallIsAskedPlainlyByThePermissionRequest()
    {
        var approvals = Approvals();

        var decision = approvals.DecideAsync(Permission("pollux_move", MoveIn));
        var prompt = _asked.Single();
        Assert.IsNull(prompt.Reason);
        Assert.AreEqual("Move 1 entry", prompt.Title);
        approvals.Answer(prompt.Id, ApprovalAnswer.Deny);

        Assert.IsInstanceOfType<PermissionDecisionReject>(await decision);
    }

    public TestContext TestContext { get; set; } = null!;
}
