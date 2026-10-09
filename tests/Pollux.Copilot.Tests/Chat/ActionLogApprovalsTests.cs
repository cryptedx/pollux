using System.Text.Json;
using GitHub.Copilot;
using GitHub.Copilot.Rpc;
using Pollux.Copilot.Chat;
using Pollux.Copilot.Tools;

namespace Pollux.Copilot.Tests.Chat;

/// <summary>Every tool call of a Chat reaches its Action Log through <see cref="ToolApprovals"/>, blocked ones included.</summary>
[TestClass]
public sealed class ActionLogApprovalsTests
{
#pragma warning disable GHCP001 // Experimental SDK API (PermissionDecision).
    private readonly List<ToolApprovalRequest> _asked = [];
    private string _dir = "";
    private ActionLog _log = null!;
    private string _mode = "Manual";

    public TestContext TestContext { get; set; } = null!;

    private static readonly Dictionary<string, string> Env = new(StringComparer.OrdinalIgnoreCase)
    {
        ["USERPROFILE"] = @"C:\Users\me", ["APPDATA"] = @"C:\Users\me\AppData\Roaming", ["LOCALAPPDATA"] = @"C:\Users\me\AppData\Local",
    };

    [TestInitialize]
    public void Init()
    {
        _dir = Path.Combine(Path.GetTempPath(), "pollux-actionlog-" + Guid.NewGuid().ToString("N"));
        _log = new ActionLogStore(_dir).Open("chat");
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private ToolApprovals Approvals() =>
        new(_asked.Add, guard: new ChatGuard(null, () => @"C:\work", environment: n => Env.GetValueOrDefault(n)), log: _log, permissionMode: () => _mode);

    private static PreToolUseHookInput Hook(string tool, string argsJson) =>
        new() { ToolName = tool, ToolArgs = JsonDocument.Parse(argsJson).RootElement.Clone(), WorkingDirectory = @"C:\work" };

    private static PreToolUseHookInput Shell(string command) =>
        Hook("powershell", $$"""{"command":{{JsonSerializer.Serialize(command, ChatGuardTestJson.Default.String)}}}""");

    private static PermissionRequestShell ShellRequest(string command) => new()
    {
        CanOfferSessionApproval = false, Commands = [], FullCommandText = command, HasWriteFileRedirection = false,
        Intention = "", PossiblePaths = [], PossibleUrls = [],
    };

    [TestMethod]
    public async Task ABlockedCallIsLoggedAsBlockedByAGuardRule()
    {
        await Approvals().PreToolUseAsync(Shell("format C: /q"));

        var e = _log.Entries.Single();
        Assert.AreEqual("powershell", e.Tool);
        Assert.AreEqual("format C: /q", e.Command);
        Assert.AreEqual(ActionDecision.Blocked, e.Decision);
        Assert.AreEqual(ActionDecider.GuardRule, e.DecidedBy);
        StringAssert.Contains(e.Reason, "formatting");
    }

    [TestMethod]
    public async Task AnAskedCallIsLoggedAsAskedByTheUserOrBlockedByTheUser()
    {
        var approvals = Approvals();

        var allowed = approvals.PreToolUseAsync(Shell("Remove-Item -Recurse build"));
        approvals.Answer(_asked[0].Id, ApprovalAnswer.AllowOnce);
        await allowed;
        var denied = approvals.PreToolUseAsync(Shell("git push --force"));
        approvals.Answer(_asked[1].Id, ApprovalAnswer.Deny);
        await denied;

        var entries = _log.Entries;
        Assert.AreEqual(ActionDecision.Asked, entries[0].Decision);
        Assert.AreEqual(ActionDecider.User, entries[0].DecidedBy);
        StringAssert.Contains(entries[0].Reason, "ecursive delete");
        Assert.AreEqual(ActionDecision.Blocked, entries[1].Decision);
        Assert.AreEqual(ActionDecider.User, entries[1].DecidedBy);
    }

    [TestMethod]
    public async Task ARepeatAllowedForThisChatIsLoggedAsAllowedByTheUser()
    {
        var approvals = Approvals();
        var first = approvals.DecideAsync(ShellRequest("dotnet build"));
        approvals.Answer(_asked[0].Id, ApprovalAnswer.AllowForChat);
        await first;

        await approvals.DecideAsync(ShellRequest("dotnet build"));

        var second = _log.Entries[1];
        Assert.AreEqual(ActionDecision.Allowed, second.Decision);
        Assert.AreEqual(ActionDecider.User, second.DecidedBy);
        StringAssert.Contains(second.Reason, "this Chat");
        Assert.HasCount(1, _asked);
    }

    [TestMethod]
    public async Task AHarmlessCallIsLoggedAsAllowedByThePermissionModeWithItsPathsAndUrls()
    {
        _mode = "Allow All";
        var approvals = Approvals();

        await approvals.PreToolUseAsync(Hook("view", """{"path":"C:\\work\\a.txt"}"""));

        var e = _log.Entries.Single();
        Assert.AreEqual(ActionDecision.Allowed, e.Decision);
        Assert.AreEqual(ActionDecider.Mode, e.DecidedBy);
        StringAssert.Contains(e.Reason, "Allow All");
        CollectionAssert.AreEqual(new[] { @"C:\work\a.txt" }, e.Paths.ToArray());
        Assert.IsNull(e.Command);
    }

    [TestMethod]
    public async Task AUrlCallIsLoggedWithItsUrl()
    {
        var approvals = Approvals();

        var pending = approvals.PreToolUseAsync(Hook("web_fetch", """{"url":"https://example.com/x"}"""));
        approvals.Answer(_asked[0].Id, ApprovalAnswer.AllowOnce);
        await pending;

        CollectionAssert.AreEqual(new[] { "https://example.com/x" }, _log.Entries.Single().Urls.ToArray());
    }

    [TestMethod]
    public async Task ACallTheModeLeavesToTheUserIsOneEntryDecidedByTheUser()
    {
        var approvals = Approvals();
        await approvals.PreToolUseAsync(Shell("dotnet build"));

        var asking = approvals.DecideAsync(ShellRequest("dotnet build"));
        approvals.Answer(_asked[0].Id, ApprovalAnswer.AllowOnce);
        await asking;

        var e = _log.Entries.Single();
        Assert.AreEqual(ActionDecision.Asked, e.Decision);
        Assert.AreEqual(ActionDecider.User, e.DecidedBy);
    }

    [TestMethod]
    public async Task ACallTheUserDeniesInManualModeIsLoggedAsBlockedByTheUser()
    {
        var approvals = Approvals();
        await approvals.PreToolUseAsync(Shell("dotnet build"));

        var asking = approvals.DecideAsync(ShellRequest("dotnet build"));
        approvals.Answer(_asked[0].Id, ApprovalAnswer.Deny);
        await asking;

        var e = _log.Entries.Single();
        Assert.AreEqual(ActionDecision.Blocked, e.Decision);
        Assert.AreEqual(ActionDecider.User, e.DecidedBy);
    }

    [TestMethod]
    public async Task AHookApprovalFollowedByThePermissionRequestStaysOneEntry()
    {
        var approvals = Approvals();
        var hook = approvals.PreToolUseAsync(Shell("Remove-Item -Recurse build"));
        approvals.Answer(_asked[0].Id, ApprovalAnswer.AllowOnce);
        await hook;

        await approvals.DecideAsync(ShellRequest("Remove-Item -Recurse build"));

        Assert.HasCount(1, _log.Entries);
    }

    [TestMethod]
    public async Task AFallbackRequestWithoutAHookEntryIsLoggedToo()
    {
        var approvals = Approvals();

        await approvals.DecideAsync(ShellRequest("format C: /q"));

        var e = _log.Entries.Single();
        Assert.AreEqual(ActionDecision.Blocked, e.Decision);
        Assert.AreEqual(ActionDecider.GuardRule, e.DecidedBy);
        Assert.AreEqual("format C: /q", e.Command);
    }

    [TestMethod]
    public async Task ARequestTheFallbackAsksAboutWithoutAHookEntryIsLoggedAsAsked()
    {
        var approvals = Approvals();

        var asking = approvals.DecideAsync(ShellRequest("dotnet build"));
        approvals.Answer(_asked[0].Id, ApprovalAnswer.AllowOnce);
        await asking;

        var e = _log.Entries.Single();
        Assert.AreEqual(ActionDecision.Asked, e.Decision);
        Assert.AreEqual(ActionDecider.User, e.DecidedBy);
    }

    private static PermissionRequestCustomTool FilePermission(string tool, string argsJson) => new()
    {
        ToolName = tool, ToolDescription = "", Args = JsonDocument.Parse(argsJson).RootElement.Clone(),
    };

    private static PolluxToolCall FileCall(string tool, string argsJson) =>
        PolluxToolCall.Of(FileOperation.FromToolCall(tool, JsonDocument.Parse(argsJson).RootElement)!);

    private const string MoveIn = """{"sources":["C:\\work\\a.pdf"],"destination":"C:\\work\\docs"}""";
    private const string MoveOut = """{"sources":["C:\\work\\a.pdf"],"destination":"D:\\archive"}""";
    private const string MoveSecret = """{"sources":["C:\\Users\\me\\.ssh\\config"],"destination":"C:\\work"}""";

    [TestMethod]
    public async Task AFileToolTheUserDeniesInManualModeIsLoggedAsBlockedByTheUser()
    {
        var approvals = Approvals();
        Assert.IsNull(await approvals.PreToolUseAsync(Hook("pollux_move", MoveIn)));

        var asking = approvals.DecideAsync(FilePermission("pollux_move", MoveIn));
        approvals.Answer(_asked.Single().Id, ApprovalAnswer.Deny);
        await asking;

        var e = _log.Entries.Single();
        Assert.AreEqual("pollux_move", e.Tool);
        Assert.AreEqual(ActionDecision.Blocked, e.Decision);
        Assert.AreEqual(ActionDecider.User, e.DecidedBy);
    }

    [TestMethod]
    public async Task AFileToolTheUserAllowsInManualModeIsLoggedAsAskedByTheUser()
    {
        var approvals = Approvals();
        await approvals.PreToolUseAsync(Hook("pollux_move", MoveIn));

        var asking = approvals.DecideAsync(FilePermission("pollux_move", MoveIn));
        approvals.Answer(_asked.Single().Id, ApprovalAnswer.AllowOnce);
        await asking;
        Assert.IsNull(await approvals.AuthorizeAsync(FileCall("pollux_move", MoveIn), TestContext.CancellationToken));

        var e = _log.Entries.Single();
        Assert.AreEqual(ActionDecision.Asked, e.Decision);
        Assert.AreEqual(ActionDecider.User, e.DecidedBy);
    }

    [TestMethod]
    public async Task AFileToolBlockedInThePermissionRequestWithoutAHookEntryIsLogged()
    {
        await Approvals().DecideAsync(FilePermission("pollux_move", MoveSecret));

        var e = _log.Entries.Single();
        Assert.AreEqual(ActionDecision.Blocked, e.Decision);
        Assert.AreEqual(ActionDecider.GuardRule, e.DecidedBy);
    }

    [TestMethod]
    public async Task AFileToolTheToolItselfAsksAboutIsLoggedWithTheUsersAnswer()
    {
        var approvals = Approvals();

        var allowed = approvals.AuthorizeAsync(FileCall("pollux_move", MoveOut), TestContext.CancellationToken);
        approvals.Answer(_asked[0].Id, ApprovalAnswer.AllowOnce);
        await allowed;
        var denied = approvals.AuthorizeAsync(FileCall("pollux_move", MoveOut), TestContext.CancellationToken);
        approvals.Answer(_asked[1].Id, ApprovalAnswer.Deny);
        await denied;

        var entries = _log.Entries;
        Assert.HasCount(2, entries);
        Assert.AreEqual(ActionDecision.Asked, entries[0].Decision);
        Assert.AreEqual(ActionDecision.Blocked, entries[1].Decision);
        Assert.AreEqual(ActionDecider.User, entries[1].DecidedBy);
    }

    [TestMethod]
    public async Task AFileToolBlockedByTheToolItselfIsLogged()
    {
        Assert.IsNotNull(await Approvals().AuthorizeAsync(FileCall("pollux_move", MoveSecret), TestContext.CancellationToken));

        Assert.AreEqual(ActionDecider.GuardRule, _log.Entries.Single().DecidedBy);
    }

    [TestMethod]
    public async Task AFileToolTheHookAskedAboutStaysOneEntryWhenTheToolRuns()
    {
        var approvals = Approvals();
        var hook = approvals.PreToolUseAsync(Hook("pollux_move", MoveOut));
        approvals.Answer(_asked.Single().Id, ApprovalAnswer.AllowOnce);
        await hook;

        Assert.IsNull(await approvals.AuthorizeAsync(FileCall("pollux_move", MoveOut), TestContext.CancellationToken));

        Assert.HasCount(1, _log.Entries);
    }

    [TestMethod]
    public async Task WithoutALogNothingBreaks()
    {
        var approvals = new ToolApprovals(_asked.Add);

        Assert.AreEqual("deny", (await approvals.PreToolUseAsync(Shell("format C:")))!.PermissionDecision);
    }
}
