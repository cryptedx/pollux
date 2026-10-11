using System.Collections.Concurrent;
using System.Text.Json;
using Pollux.Copilot.Chat;
using Pollux.Copilot.Models;
using Pollux.Core.Settings;

namespace Pollux.Copilot.Tests.Chat;

/// <summary>The Chat as the Copilot Panel drives it: Panel messages in, Panel messages out.</summary>
[TestClass]
public sealed class CopilotChatTests
{
    private readonly ConcurrentQueue<string> _posted = new();
    private readonly List<FakeSession> _sessions = [];
    private ToolApprovals? _approvals;
    private Exception? _openFails;
    private Func<CancellationToken, Task<CopilotQuota?>>? _quota;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pollux-chat-" + Guid.NewGuid().ToString("N"));
    private readonly List<(string Id, bool Resume, string? Model, string? Folder)> _opened = [];
    private readonly List<string> _deletedInCli = [];
    private IReadOnlyList<ChatTurn> _history = [];
    private DateTimeOffset _now = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    private ChatModes _defaults = new(CopilotAgentMode.Interactive, CopilotPermissionMode.Manual);
    private ModePolicy _policy = ModePolicy.None;
    private readonly HashSet<CopilotPermissionMode> _cliRefuses = [];
    private SandboxPolicy _sandboxPolicy = SandboxPolicy.None;
    private Exception? _sandboxFails;
    private Exception? _policyFails;

    private CopilotChat Chat() => new(new ChatStore(_dir), (record, resume, approvals, _) =>
    {
        _approvals = approvals;
        if (_openFails is { } ex) return Task.FromException<IChatSession>(ex);
        _opened.Add((record.Id, resume, record.Model, record.WorkingFolder));
        var s = new FakeSession { History = _history, Policy = _policy, CliRefuses = _cliRefuses, SandboxPolicy = _sandboxPolicy, SandboxFails = _sandboxFails, PolicyFails = _policyFails };
        _sessions.Add(s);
        return Task.FromResult<IChatSession>(s);
    }, (id, _) =>
    {
        _deletedInCli.Add(id);
        return Task.CompletedTask;
    }, _posted.Enqueue, _quota, () => _now, () => _defaults, activeFolder: () => _activeFolder, guardRules: () => _guardSettings,
        environment: name => name == "USERPROFILE" ? @"C:\Users\marko" : null, actionLogs: _actionLogs);

    private ActionLogStore? _actionLogs;

    private string? _activeFolder = @"C:\work";
    private GuardRuleSettings _guardSettings = new();

    /// <summary>The Working Folder in the newest header message.</summary>
    private string? LastHeaderFolder()
    {
        using var doc = JsonDocument.Parse(_posted.Last(m => m.Contains("\"header\"", StringComparison.Ordinal)));
        return doc.RootElement.GetProperty("workingFolder").GetString();
    }

    [TestMethod]
    public async Task NewChatWorksInTheActivePanesFolder()
    {
        await using var chat = Chat();
        chat.PublishHeader();
        Assert.AreEqual(@"C:\work", LastHeaderFolder());

        await chat.SendAsync("hi", TestContext.CancellationToken);
        _activeFolder = @"C:\elsewhere";
        chat.PublishHeader();

        Assert.AreEqual(@"C:\work", _opened.Single().Folder);
        Assert.AreEqual(@"C:\work", new ChatStore(_dir).List().Single().WorkingFolder);
        Assert.AreEqual(@"C:\work", LastHeaderFolder(), "Once the Chat exists its Working Folder no longer follows the Pane.");
    }

    [TestMethod]
    public async Task HomeHasNoWorkingFolder()
    {
        _activeFolder = null;
        await using var chat = Chat();

        await chat.SendAsync("hi", TestContext.CancellationToken);

        Assert.IsNull(_opened.Single().Folder);
    }

    [TestMethod]
    public async Task WorkingFolderChosenBeforeTheFirstMessageIsUsed()
    {
        await using var chat = Chat();

        Assert.IsTrue(await chat.SetWorkingFolderAsync(@"D:\repo"));
        await chat.SendAsync("hi", TestContext.CancellationToken);

        Assert.AreEqual(@"D:\repo", _opened.Single().Folder);
        Assert.AreEqual(@"D:\repo", LastHeaderFolder());
    }

    [TestMethod]
    public async Task ChangingTheWorkingFolderOfAStartedChatResumesItThere()
    {
        await using var chat = Chat();
        await chat.SendAsync("hi", TestContext.CancellationToken);
        _sessions[0].Raise(new ChatEvent.Idle(false));

        Assert.IsTrue(await chat.SetWorkingFolderAsync(@"D:\repo"));
        await chat.SendAsync("again", TestContext.CancellationToken);

        Assert.IsTrue(_sessions[0].Disposed, "The CLI gets the new Working Folder by resuming the session.");
        Assert.AreEqual((_opened[0].Id, true, (string?)null, @"D:\repo"), _opened[1]);
        Assert.AreEqual(@"D:\repo", new ChatStore(_dir).List().Single().WorkingFolder);
        Assert.AreEqual(@"D:\repo", LastHeaderFolder());
    }

    [TestMethod]
    public async Task WorkingFolderCannotChangeWhileCopilotAnswers()
    {
        await using var chat = Chat();
        await chat.SendAsync("hi", TestContext.CancellationToken);

        Assert.IsFalse(await chat.SetWorkingFolderAsync(@"D:\repo"));
        Assert.AreEqual(@"C:\work", LastHeaderFolder());
    }

    [TestMethod]
    public async Task GuardRulesUseTheChatsWorkingFolder()
    {
        await using var chat = Chat();
        await chat.SendAsync("hi", TestContext.CancellationToken);

        Assert.IsNull(await _approvals!.PreToolUseAsync(new GitHub.Copilot.PreToolUseHookInput { ToolName = "create", ToolArgs = JsonDocument.Parse("""{"path":"C:\\work\\a.txt"}""").RootElement.Clone() }));
        _ = _approvals!.PreToolUseAsync(new GitHub.Copilot.PreToolUseHookInput { ToolName = "create", ToolArgs = JsonDocument.Parse("""{"path":"C:\\other\\a.txt"}""").RootElement.Clone() });

        CollectionAssert.Contains(Posted(), @"approval:C:\other\a.txt");
    }

    private static GitHub.Copilot.PreToolUseHookInput Hook(string tool, string argsJson) =>
        new() { ToolName = tool, ToolArgs = JsonDocument.Parse(argsJson).RootElement.Clone() };

    private bool? LastHeaderTainted()
    {
        using var doc = JsonDocument.Parse(_posted.Last(m => m.Contains("\"header\"", StringComparison.Ordinal)));
        return doc.RootElement.GetProperty("tainted").GetBoolean();
    }

    [TestMethod]
    public async Task ReadingFromDownloadsTaintsTheChatUntilTheUserClearsIt()
    {
        _defaults = new(CopilotAgentMode.Interactive, CopilotPermissionMode.AllowAll);
        await using var chat = Chat();
        await chat.SendAsync("what is in my invoice?", TestContext.CancellationToken);
        Assert.IsNull(await _approvals!.PreToolUseAsync(Hook("powershell", """{"command":"git status"}""")), "untainted: the Permission Mode decides");

        Assert.IsNull(await _approvals!.PreToolUseAsync(Hook("view", """{"path":"C:\\Users\\marko\\Downloads\\invoice.txt"}""")), "the read itself goes ahead");

        Assert.IsTrue(chat.Tainted);
        Assert.IsTrue(LastHeaderTainted());
        var shell = _approvals!.PreToolUseAsync(Hook("powershell", """{"command":"git status"}"""));
        Assert.HasCount(1, _approvals!.Pending, "a Tainted Chat asks before shell even under Allow All");
        chat.Answer(_approvals!.Pending.Single(), ApprovalAnswer.Deny);
        Assert.AreEqual("deny", (await shell)!.PermissionDecision);

        chat.ClearTainted();

        Assert.IsFalse(chat.Tainted);
        Assert.IsFalse(LastHeaderTainted());
        Assert.IsNull(await _approvals!.PreToolUseAsync(Hook("powershell", """{"command":"git status"}""")), "cleared: back to normal");
    }

    [TestMethod]
    public async Task TaintedStateIsKeptWithTheChatAndRestoredOnResume()
    {
        await using (var chat = Chat())
        {
            await chat.SendAsync("summarize the download", TestContext.CancellationToken);
            await _approvals!.PreToolUseAsync(Hook("view", """{"path":"C:\\Users\\marko\\Downloads\\a.txt"}"""));
        }

        var saved = new ChatStore(_dir).List().Single();
        Assert.IsTrue(saved.Tainted);
        StringAssert.Contains(saved.TaintedBy, "Downloads");

        await using var resumed = Chat();
        Assert.IsTrue(await resumed.OpenAsync(saved.Id, TestContext.CancellationToken));

        Assert.IsTrue(resumed.Tainted);
        Assert.IsTrue(LastHeaderTainted());
        _ = _approvals!.PreToolUseAsync(Hook("create", """{"path":"C:\\work\\a.txt"}"""));
        Assert.HasCount(1, _approvals!.Pending, "the resumed Tainted Chat asks before a write inside the Working Folder");

        resumed.ClearTainted();
        Assert.IsFalse(new ChatStore(_dir).Get(saved.Id)!.Tainted, "clearing is kept as well");
    }

    [TestMethod]
    public async Task ANewChatIsNotTainted()
    {
        await using var chat = Chat();
        await chat.SendAsync("one", TestContext.CancellationToken);
        var fetch = _approvals!.PreToolUseAsync(Hook("web_fetch", """{"url":"https://example.com"}"""));
        chat.Answer(_approvals!.Pending.Single(), ApprovalAnswer.AllowOnce); // URL access always asks
        await fetch;
        _sessions[0].Raise(new ChatEvent.Idle(false));
        Assert.IsTrue(chat.Tainted);

        await chat.NewChatAsync();

        Assert.IsFalse(chat.Tainted);
        Assert.IsFalse(LastHeaderTainted());
    }

    private string[] ActionLogTools()
    {
        var last = _posted.Last(m => m.Contains("\"actionLog\"", StringComparison.Ordinal));
        using var doc = JsonDocument.Parse(last);
        return [.. doc.RootElement.GetProperty("entries").EnumerateArray().Select(e => e.GetProperty("tool").GetString()! + ":" + e.GetProperty("decision").GetString())];
    }

    [TestMethod]
    public async Task EveryToolCallOfAChatIsInItsActionLogIncludingBlockedOnesAndItSurvivesResume()
    {
        var logs = new ActionLogStore(_dir);
        string chatId;
        await using (var chat = ChatWithLog(logs))
        {
            await chat.SendAsync("hi", TestContext.CancellationToken);
            chatId = chat.CurrentId!;
            await _approvals!.PreToolUseAsync(Hook("view", """{"path":"C:\\work\\a.txt"}"""));
            await _approvals!.PreToolUseAsync(Hook("powershell", """{"command":"format C:"}"""));
            chat.ShowActionLog();
            CollectionAssert.AreEqual(new[] { "view:allowed", "powershell:blocked" }, ActionLogTools());
        }

        await using var restarted = ChatWithLog(new ActionLogStore(_dir));
        await restarted.OpenAsync(chatId, TestContext.CancellationToken);
        await _approvals!.PreToolUseAsync(Hook("view", """{"path":"C:\\work\\b.txt"}"""));
        restarted.ShowActionLog();

        CollectionAssert.AreEqual(new[] { "view:allowed", "powershell:blocked", "view:allowed" }, ActionLogTools());
    }

    [TestMethod]
    public async Task DeletingAChatDeletesItsActionLog()
    {
        var logs = new ActionLogStore(_dir);
        await using var chat = ChatWithLog(logs);
        await chat.SendAsync("hi", TestContext.CancellationToken);
        var id = chat.CurrentId!;
        await _approvals!.PreToolUseAsync(Hook("powershell", """{"command":"format C:"}"""));

        await chat.DeleteAsync(id, TestContext.CancellationToken);

        Assert.IsEmpty(new ActionLogStore(_dir).Open(id).Entries);
    }

    [TestMethod]
    public async Task ActionLogOfAnEmptyChatIsEmpty()
    {
        await using var chat = ChatWithLog(new ActionLogStore(_dir));

        chat.ShowActionLog();

        Assert.IsEmpty(ActionLogTools());
    }

    private CopilotChat ChatWithLog(ActionLogStore logs) => new(new ChatStore(_dir), (record, resume, approvals, _) =>
    {
        _approvals = approvals;
        var s = new FakeSession { History = _history, Policy = _policy, SandboxPolicy = _sandboxPolicy };
        _sessions.Add(s);
        return Task.FromResult<IChatSession>(s);
    }, (_, _) => Task.CompletedTask, _posted.Enqueue, _quota, () => _now, () => _defaults, activeFolder: () => _activeFolder, guardRules: () => _guardSettings, actionLogs: logs);

    [TestMethod]
    public async Task GuardRulesUseTheSettingsExtensions()
    {
        _guardSettings = new GuardRuleSettings { BlockedCommands = ["shutdown"] };
        await using var chat = Chat();
        await chat.SendAsync("hi", TestContext.CancellationToken);

        var output = await _approvals!.PreToolUseAsync(new GitHub.Copilot.PreToolUseHookInput { ToolName = "powershell", ToolArgs = JsonDocument.Parse("""{"command":"shutdown /s"}""").RootElement.Clone() });

        Assert.AreEqual("deny", output!.PermissionDecision);
    }

    [TestMethod]
    public async Task AllowForThisChatIsShownAsTheAnswer()
    {
        await using var chat = Chat();
        await chat.SendAsync("build it", TestContext.CancellationToken);
        var decision = FakeSession.RequestTool(_approvals!, "dotnet build");

        chat.Answer(_approvals!.Pending.Single(), ApprovalAnswer.AllowForChat);

        Assert.IsTrue(await decision);
        Assert.IsTrue(_posted.Any(m => m.Contains("\"answer\":\"allowForChat\"", StringComparison.Ordinal)));
    }

    /// <summary>The posted messages as "type" or "type:field" for the field that matters.</summary>
    private string[] Posted() => [.. _posted.Where(m => !m.Contains("\"chats\"", StringComparison.Ordinal) && !m.Contains("\"modes\"", StringComparison.Ordinal) && !m.Contains("\"type\":\"sandbox\"", StringComparison.Ordinal)).Select(m =>
    {
        using var doc = JsonDocument.Parse(m);
        var r = doc.RootElement;
        var type = r.GetProperty("type").GetString();
        return type switch
        {
            "user" or "answerDelta" or "answer" => $"{type}:{r.GetProperty("text").GetString()}",
            "busy" => $"busy:{r.GetProperty("busy").GetBoolean()}",
            "chatError" => $"chatError:{r.GetProperty("message").GetString()}",
            "model" => $"model:{r.GetProperty("id").GetString()}",
            "quota" => $"quota:{r.GetProperty("text").GetString()}",
            "tool" => $"tool:{r.GetProperty("status").GetString()}",
            "approval" => $"approval:{r.GetProperty("detail").GetString()}",
            "approvalDone" => $"approvalDone:{r.GetProperty("approved").GetBoolean()}",
            "warning" => $"warning:{r.GetProperty("message").GetString()}",
            _ => type!,
        };
    })];

    /// <summary>The newest Chat list message as "titles|current id".</summary>
    private string LastChats()
    {
        using var doc = JsonDocument.Parse(_posted.Last(m => m.Contains("\"chats\"", StringComparison.Ordinal)));
        var r = doc.RootElement;
        return string.Join(",", r.GetProperty("chats").EnumerateArray().Select(c => c.GetProperty("title").GetString())) + "|" + r.GetProperty("current").GetString();
    }

    /// <summary>The newest modes message as "agent/permission" plus " blocked:mode=reason" for each blocked Permission Mode.</summary>
    /// <summary>The last sandbox toggle the Panel was told: "on", "off", with " locked:reason" when it cannot be switched.</summary>
    private string LastSandbox()
    {
        using var doc = JsonDocument.Parse(_posted.Last(m => m.Contains("\"type\":\"sandbox\"", StringComparison.Ordinal)));
        var r = doc.RootElement;
        return (r.GetProperty("enabled").GetBoolean() ? "on" : "off")
            + (r.GetProperty("locked").ValueKind == JsonValueKind.String ? " locked:" + r.GetProperty("locked").GetString() : "");
    }

    private string LastModes()
    {
        using var doc = JsonDocument.Parse(_posted.Last(m => m.Contains("\"modes\"", StringComparison.Ordinal)));
        var r = doc.RootElement;
        var blocked = r.GetProperty("permissionModes").EnumerateArray()
            .Where(p => p.GetProperty("blocked").ValueKind == JsonValueKind.String)
            .Select(p => $" blocked:{p.GetProperty("id").GetString()}={p.GetProperty("blocked").GetString()}");
        return r.GetProperty("agentMode").GetString() + "/" + r.GetProperty("permissionMode").GetString() + string.Concat(blocked);
    }

    [TestMethod]
    public async Task SandboxIsOffByDefaultAndOnForAutopilotOrAllowAll()
    {
        await using var chat = Chat();
        chat.PublishModes();
        Assert.AreEqual("off", LastSandbox());

        await chat.SendAsync("hi", TestContext.CancellationToken);
        CollectionAssert.AreEqual(new[] { "off" }, _sessions.Single().SandboxSet);

        await chat.SetAgentModeAsync(CopilotAgentMode.Autopilot, TestContext.CancellationToken);
        Assert.AreEqual("on", LastSandbox());
        await chat.SetAgentModeAsync(CopilotAgentMode.Interactive, TestContext.CancellationToken);
        await chat.SetPermissionModeAsync(CopilotPermissionMode.AllowAll, TestContext.CancellationToken);
        Assert.AreEqual("on", LastSandbox());
        CollectionAssert.AreEqual(new[] { "off", "on", "off", "on" }, _sessions.Single().SandboxSet);
    }

    [TestMethod]
    public async Task SandboxStartsOnInAChatThatBeginsInAutopilot()
    {
        _defaults = new ChatModes(CopilotAgentMode.Autopilot, CopilotPermissionMode.Manual);
        await using var chat = Chat();
        await chat.SendAsync("hi", TestContext.CancellationToken);

        CollectionAssert.AreEqual(new[] { "on" }, _sessions.Single().SandboxSet);
        Assert.AreEqual("on", LastSandbox());
    }

    [TestMethod]
    public async Task SandboxChoiceReachesTheCliSticksAcrossModesAndPersistsWithTheChat()
    {
        await using var chat = Chat();
        await chat.SendAsync("hi", TestContext.CancellationToken);

        Assert.IsTrue(await chat.SetSandboxAsync(true, TestContext.CancellationToken));
        Assert.AreEqual("on", LastSandbox());
        await chat.SetPermissionModeAsync(CopilotPermissionMode.Assisted, TestContext.CancellationToken);
        Assert.AreEqual("on", LastSandbox());
        CollectionAssert.AreEqual(new[] { "off", "on" }, _sessions.Single().SandboxSet);
        Assert.AreEqual(true, new ChatStore(_dir).List().Single().Sandbox);

        // Resumed: the recorded choice is applied again.
        var id = new ChatStore(_dir).List().Single().Id;
        await using var again = Chat();
        await again.OpenAsync(id, TestContext.CancellationToken);
        Assert.AreEqual("on", LastSandbox());
        CollectionAssert.AreEqual(new[] { "on" }, _sessions[^1].SandboxSet);
    }

    [TestMethod]
    public async Task AllowAllChatWithSandboxSwitchedOffStaysOff()
    {
        _defaults = new ChatModes(CopilotAgentMode.Interactive, CopilotPermissionMode.AllowAll);
        await using var chat = Chat();
        await chat.SetSandboxAsync(false, TestContext.CancellationToken);
        await chat.SendAsync("hi", TestContext.CancellationToken);

        CollectionAssert.AreEqual(new[] { "off" }, _sessions.Single().SandboxSet);
        Assert.AreEqual(false, new ChatStore(_dir).List().Single().Sandbox);
    }

    [TestMethod]
    public async Task SandboxRequiredByTheOrganizationIsOnAndLocked()
    {
        _sandboxPolicy = new SandboxPolicy(ForcedByOrganization: true);
        await using var chat = Chat();
        await chat.SendAsync("hi", TestContext.CancellationToken);

        StringAssert.StartsWith(LastSandbox(), "on locked:");
        Assert.IsFalse(await chat.SetSandboxAsync(false, TestContext.CancellationToken));
        StringAssert.StartsWith(LastSandbox(), "on locked:");
        Assert.AreEqual(0, _sessions.Single().SandboxSet.Count); // the CLI enforces it itself
    }

    [TestMethod]
    public async Task UnsupportedSandboxIsOffAndLockedWithTheReason()
    {
        _defaults = new ChatModes(CopilotAgentMode.Autopilot, CopilotPermissionMode.AllowAll);
        _sandboxPolicy = new SandboxPolicy("No container support on this build.");
        await using var chat = Chat();
        await chat.SendAsync("hi", TestContext.CancellationToken);

        Assert.AreEqual("off locked:No container support on this build.", LastSandbox());
        Assert.IsFalse(await chat.SetSandboxAsync(true, TestContext.CancellationToken));
        Assert.AreEqual("off locked:No container support on this build.", LastSandbox());
        Assert.AreEqual(0, _sessions.Single().SandboxSet.Count);
    }

    [TestMethod]
    public async Task SandboxTheCliCannotSwitchKeepsItsOldStateAndSaysSo()
    {
        await using var chat = Chat();
        await chat.SendAsync("hi", TestContext.CancellationToken);
        _sessions.Single().SandboxFails = new InvalidOperationException("boom");

        Assert.IsFalse(await chat.SetSandboxAsync(true, TestContext.CancellationToken));

        Assert.AreEqual("off", LastSandbox());
        CollectionAssert.Contains(Posted(), "chatError:Could not switch the sandbox on: boom");
    }

    // S-17: in Allow All and Autopilot nobody approves each action, so the sandbox and the organization's mode policy are
    // what limit the agent. A Chat must not send a prompt while either could not be confirmed (fail closed).

    [TestMethod]
    [DataRow(CopilotAgentMode.Interactive, CopilotPermissionMode.AllowAll)]
    [DataRow(CopilotAgentMode.Autopilot, CopilotPermissionMode.Manual)]
    [DataRow(CopilotAgentMode.Autopilot, CopilotPermissionMode.AllowAll)]
    public async Task SandboxTheCliCannotSwitchBlocksPromptsInAllowAllAndAutopilot(CopilotAgentMode agent, CopilotPermissionMode permission)
    {
        _defaults = new ChatModes(agent, permission);
        _sandboxFails = new InvalidOperationException("boom");
        await using var chat = Chat();

        Assert.IsFalse(await chat.SendAsync("rm -rf everything", TestContext.CancellationToken));

        Assert.IsEmpty(_sessions.Single().Sent, "The prompt must not reach the CLI with an unconfirmed sandbox.");
        CollectionAssert.Contains(Posted(), "chatError:Could not switch the sandbox on: boom");
        CollectionAssert.Contains(Posted(), "chatError:" + CopilotChat.SandboxUnconfirmed);
        Assert.IsFalse(chat.IsBusy);
        Assert.AreEqual("busy:False", Posted().Last());
    }

    [TestMethod]
    [DataRow(CopilotAgentMode.Interactive, CopilotPermissionMode.AllowAll)]
    [DataRow(CopilotAgentMode.Autopilot, CopilotPermissionMode.Manual)]
    public async Task ModePolicyThatCannotBeReadBlocksPromptsInAllowAllAndAutopilot(CopilotAgentMode agent, CopilotPermissionMode permission)
    {
        _defaults = new ChatModes(agent, permission);
        _policyFails = new InvalidOperationException("managed settings unavailable");
        await using var chat = Chat();

        Assert.IsFalse(await chat.SendAsync("rm -rf everything", TestContext.CancellationToken));

        Assert.IsEmpty(_sessions.Single().Sent, "An unreadable policy must not be taken as \"nothing is blocked\".");
        CollectionAssert.Contains(Posted(), "chatError:" + CopilotChat.ModePolicyUnconfirmed);
        Assert.IsFalse(chat.IsBusy);
    }

    [TestMethod]
    [DataRow(CopilotAgentMode.Interactive, CopilotPermissionMode.Manual)]
    [DataRow(CopilotAgentMode.Interactive, CopilotPermissionMode.Assisted)]
    [DataRow(CopilotAgentMode.Plan, CopilotPermissionMode.Assisted)]
    public async Task ManualAndAssistedStillSendWhenSandboxAndPolicyCannotBeConfirmed(CopilotAgentMode agent, CopilotPermissionMode permission)
    {
        _defaults = new ChatModes(agent, permission);
        _sandboxFails = new InvalidOperationException("boom");
        _policyFails = new InvalidOperationException("managed settings unavailable");
        await using var chat = Chat();

        Assert.IsTrue(await chat.SendAsync("hi", TestContext.CancellationToken));

        CollectionAssert.AreEqual(new[] { "hi" }, _sessions.Single().Sent);
        CollectionAssert.DoesNotContain(Posted(), "chatError:" + CopilotChat.SandboxUnconfirmed);
        CollectionAssert.DoesNotContain(Posted(), "chatError:" + CopilotChat.ModePolicyUnconfirmed);
    }

    [TestMethod]
    public async Task RefusedPromptGoesThroughOnceSandboxAndPolicyAreConfirmed()
    {
        _defaults = new ChatModes(CopilotAgentMode.Autopilot, CopilotPermissionMode.AllowAll);
        _sandboxFails = new InvalidOperationException("boom");
        _policyFails = new InvalidOperationException("managed settings unavailable");
        await using var chat = Chat();
        Assert.IsFalse(await chat.SendAsync("hi", TestContext.CancellationToken));

        var session = _sessions.Single();
        session.SandboxFails = null;
        Assert.IsFalse(await chat.SendAsync("hi", TestContext.CancellationToken), "The policy is still unreadable.");
        session.PolicyFails = null;
        Assert.IsTrue(await chat.SendAsync("hi", TestContext.CancellationToken));

        CollectionAssert.AreEqual(new[] { "hi" }, session.Sent);
        CollectionAssert.AreEqual(new[] { "on" }, session.SandboxSet);
    }

    [TestMethod]
    public async Task PolicyReadBeforeSendingCanStillBlockTheMode()
    {
        _defaults = new ChatModes(CopilotAgentMode.Interactive, CopilotPermissionMode.AllowAll);
        _policyFails = new InvalidOperationException("managed settings unavailable");
        await using var chat = Chat();
        Assert.IsFalse(await chat.SendAsync("hi", TestContext.CancellationToken));

        var session = _sessions.Single();
        session.PolicyFails = null;
        session.Policy = ModePolicy.None.Block(CopilotPermissionMode.AllowAll, ModePolicy.BlockedByOrganization);
        Assert.IsFalse(await chat.SendAsync("hi", TestContext.CancellationToken));

        Assert.IsEmpty(session.Sent);
        Assert.AreEqual("interactive/allowAll blocked:allowAll=" + ModePolicy.BlockedByOrganization, LastModes());
        CollectionAssert.Contains(Posted(), "chatError:Allow All is not available: " + ModePolicy.BlockedByOrganization);
    }

    [TestMethod]
    public async Task SwitchingToAutopilotWithASandboxTheCliCannotSwitchBlocksTheNextPrompt()
    {
        await using var chat = Chat();
        Assert.IsTrue(await chat.SendAsync("one", TestContext.CancellationToken));
        _sessions[0].Raise(new ChatEvent.Idle(false));
        _sessions[0].SandboxFails = new InvalidOperationException("boom");

        await chat.SetAgentModeAsync(CopilotAgentMode.Autopilot, TestContext.CancellationToken);
        Assert.IsFalse(await chat.SendAsync("two", TestContext.CancellationToken));

        CollectionAssert.AreEqual(new[] { "one" }, _sessions[0].Sent);
        CollectionAssert.Contains(Posted(), "chatError:" + CopilotChat.SandboxUnconfirmed);
    }

    [TestMethod]
    public async Task SandboxChosenBeforeTheFirstMessageAppliesToIt()
    {
        await using var chat = Chat();
        Assert.IsTrue(await chat.SetSandboxAsync(true, TestContext.CancellationToken));
        chat.PublishModes();
        Assert.AreEqual("on", LastSandbox());

        await chat.SendAsync("hi", TestContext.CancellationToken);

        CollectionAssert.AreEqual(new[] { "on" }, _sessions.Single().SandboxSet);
        Assert.AreEqual(true, new ChatStore(_dir).List().Single().Sandbox);
    }

    [TestMethod]
    public async Task NewChatStartsInteractiveAndManualInTheCli()
    {
        await using var chat = Chat();
        chat.PublishModes();
        Assert.AreEqual("interactive/manual", LastModes());

        await chat.SendAsync("hi", TestContext.CancellationToken);

        CollectionAssert.AreEqual(new[] { "permission:Manual", "agent:Interactive" }, _sessions.Single().ModesSet);
    }

    [TestMethod]
    public async Task NewChatStartsInTheConfiguredDefaultModes()
    {
        _defaults = new ChatModes(CopilotAgentMode.Plan, CopilotPermissionMode.Assisted);
        await using var chat = Chat();
        chat.PublishModes();
        Assert.AreEqual("plan/assisted", LastModes());

        await chat.SendAsync("hi", TestContext.CancellationToken);

        CollectionAssert.AreEqual(new[] { "permission:Assisted", "agent:Plan" }, _sessions.Single().ModesSet);
        var record = new ChatStore(_dir).List().Single();
        Assert.AreEqual(CopilotAgentMode.Plan, record.AgentMode);
        Assert.AreEqual(CopilotPermissionMode.Assisted, record.PermissionMode);
    }

    [TestMethod]
    public async Task PlanModeLeavesOnlyReadToolsAlsoAfterASwitchInTheMiddleOfTheChat()
    {
        _defaults = new ChatModes(CopilotAgentMode.Plan, CopilotPermissionMode.AllowAll);
        await using var chat = Chat();
        await chat.SendAsync("hi", TestContext.CancellationToken);

        Assert.AreEqual("deny", (await _approvals!.PreToolUseAsync(Hook("powershell", """{"command":"git status"}""")))?.PermissionDecision);
        Assert.IsNull(await _approvals!.PreToolUseAsync(Hook("view", """{"path":"C:\\work\\a.txt"}""")));

        Assert.IsTrue(await chat.SetAgentModeAsync(CopilotAgentMode.Interactive, TestContext.CancellationToken));
        Assert.IsNull(await _approvals!.PreToolUseAsync(Hook("powershell", """{"command":"git status"}""")));

        Assert.IsTrue(await chat.SetAgentModeAsync(CopilotAgentMode.Plan, TestContext.CancellationToken));
        Assert.AreEqual("deny", (await _approvals!.PreToolUseAsync(Hook("powershell", """{"command":"git status"}""")))?.PermissionDecision);
    }

    [TestMethod]
    public async Task ModesChosenBeforeTheFirstMessageApplyToIt()
    {
        await using var chat = Chat();

        Assert.IsTrue(await chat.SetAgentModeAsync(CopilotAgentMode.Autopilot, TestContext.CancellationToken));
        Assert.IsTrue(await chat.SetPermissionModeAsync(CopilotPermissionMode.AllowAll, TestContext.CancellationToken));
        Assert.AreEqual("autopilot/allowAll", LastModes());
        await chat.SendAsync("hi", TestContext.CancellationToken);

        CollectionAssert.AreEqual(new[] { "permission:AllowAll", "agent:Autopilot" }, _sessions.Single().ModesSet);
    }

    [TestMethod]
    public async Task SwitchingModesInAStartedChatReachesTheCliAndIsKept()
    {
        await using var chat = Chat();
        await chat.SendAsync("hi", TestContext.CancellationToken);
        _sessions[0].Raise(new ChatEvent.Idle(false));

        Assert.IsTrue(await chat.SetAgentModeAsync(CopilotAgentMode.Plan, TestContext.CancellationToken));
        Assert.IsTrue(await chat.SetPermissionModeAsync(CopilotPermissionMode.Assisted, TestContext.CancellationToken));

        CollectionAssert.AreEqual(new[] { "permission:Manual", "agent:Interactive", "agent:Plan", "permission:Assisted" }, _sessions[0].ModesSet);
        var record = new ChatStore(_dir).List().Single();
        Assert.AreEqual(CopilotAgentMode.Plan, record.AgentMode);
        Assert.AreEqual(CopilotPermissionMode.Assisted, record.PermissionMode);
        Assert.AreEqual("plan/assisted", LastModes());
    }

    [TestMethod]
    public async Task ResumedChatGetsBackItsModes()
    {
        new ChatStore(_dir).Save(new ChatRecord
        {
            Id = "pollux-x", Title = "t", Created = _now, Modified = _now,
            AgentMode = CopilotAgentMode.Autopilot, PermissionMode = CopilotPermissionMode.AllowAll,
        });
        await using var chat = Chat();

        Assert.IsTrue(await chat.OpenAsync("pollux-x", TestContext.CancellationToken));

        CollectionAssert.AreEqual(new[] { "permission:AllowAll", "agent:Autopilot" }, _sessions.Single().ModesSet);
        Assert.AreEqual("autopilot/allowAll", LastModes());
    }

    [TestMethod]
    public async Task NewChatAfterAnotherStartsInTheDefaultModesAgain()
    {
        await using var chat = Chat();
        await chat.SetPermissionModeAsync(CopilotPermissionMode.AllowAll, TestContext.CancellationToken);
        await chat.SendAsync("one", TestContext.CancellationToken);
        _sessions[0].Raise(new ChatEvent.Idle(false));

        await chat.NewChatAsync();

        Assert.AreEqual("interactive/manual", LastModes());
    }

    [TestMethod]
    public async Task PermissionModeBlockedByPolicyCannotBeSelected()
    {
        _policy = ModePolicy.None.Block(CopilotPermissionMode.AllowAll, ModePolicy.BlockedByOrganization);
        await using var chat = Chat();
        await chat.SendAsync("hi", TestContext.CancellationToken);
        _sessions[0].Raise(new ChatEvent.Idle(false));
        Assert.AreEqual("interactive/manual blocked:allowAll=" + ModePolicy.BlockedByOrganization, LastModes());

        Assert.IsFalse(await chat.SetPermissionModeAsync(CopilotPermissionMode.AllowAll, TestContext.CancellationToken));

        CollectionAssert.DoesNotContain(_sessions[0].ModesSet, "permission:AllowAll");
        Assert.AreEqual(CopilotPermissionMode.Manual, new ChatStore(_dir).List().Single().PermissionMode);
        Assert.AreEqual("interactive/manual blocked:allowAll=" + ModePolicy.BlockedByOrganization, LastModes());
    }

    [TestMethod]
    public async Task ResumedChatWhoseModeIsNowBlockedFallsBackToManualAndSaysWhy()
    {
        new ChatStore(_dir).Save(new ChatRecord { Id = "pollux-x", Title = "t", Created = _now, Modified = _now, PermissionMode = CopilotPermissionMode.AllowAll });
        _policy = ModePolicy.None.Block(CopilotPermissionMode.AllowAll, ModePolicy.BlockedByOrganization);
        await using var chat = Chat();

        await chat.OpenAsync("pollux-x", TestContext.CancellationToken);

        CollectionAssert.AreEqual(new[] { "permission:Manual", "agent:Interactive" }, _sessions.Single().ModesSet);
        Assert.AreEqual("interactive/manual blocked:allowAll=" + ModePolicy.BlockedByOrganization, LastModes());
        Assert.AreEqual(CopilotPermissionMode.Manual, new ChatStore(_dir).Get("pollux-x")!.PermissionMode);
        CollectionAssert.Contains(Posted(), "chatError:Allow All is not available: " + ModePolicy.BlockedByOrganization + " This Chat continues in Manual.");
    }

    [TestMethod]
    public async Task ResumedChatWhoseModeTheCliRefusesWithoutPolicyDoesNotBlameTheOrganization()
    {
        new ChatStore(_dir).Save(new ChatRecord { Id = "pollux-x", Title = "t", Created = _now, Modified = _now, PermissionMode = CopilotPermissionMode.Assisted });
        _cliRefuses.Add(CopilotPermissionMode.Assisted);
        await using var chat = Chat();

        await chat.OpenAsync("pollux-x", TestContext.CancellationToken);

        Assert.AreEqual("interactive/manual blocked:assisted=" + ModePolicy.RefusedByCli, LastModes());
        CollectionAssert.Contains(Posted(), "chatError:Assisted is not available: " + ModePolicy.RefusedByCli + " This Chat continues in Manual.");
    }

    [TestMethod]
    public async Task SwitchTheCliRefusesWithoutPolicyDoesNotBlameTheOrganization()
    {
        _cliRefuses.Add(CopilotPermissionMode.Assisted);
        await using var chat = Chat();
        await chat.SendAsync("hi", TestContext.CancellationToken);
        _sessions[0].Raise(new ChatEvent.Idle(false));

        Assert.IsFalse(await chat.SetPermissionModeAsync(CopilotPermissionMode.Assisted, TestContext.CancellationToken));

        Assert.AreEqual("interactive/manual blocked:assisted=" + ModePolicy.RefusedByCli, LastModes());
    }

    [TestMethod]
    public async Task PolicyEnforcementAfterACliRefusalNamesThePolicy()
    {
        _cliRefuses.Add(CopilotPermissionMode.Assisted);
        await using var chat = Chat();
        await chat.SendAsync("hi", TestContext.CancellationToken);
        await chat.SetPermissionModeAsync(CopilotPermissionMode.Assisted, TestContext.CancellationToken);

        _sessions[0].Raise(new ChatEvent.PermissionModeBlocked(CopilotPermissionMode.Assisted, "Assisted Permissions are disabled."));

        Assert.AreEqual("interactive/manual blocked:assisted=Assisted Permissions are disabled.", LastModes());
    }

    [TestMethod]
    public async Task ModeTheCliRefusesIsBlockedWithItsReason()
    {
        await using var chat = Chat();
        await chat.SendAsync("hi", TestContext.CancellationToken);

        _sessions[0].Raise(new ChatEvent.PermissionModeBlocked(CopilotPermissionMode.Assisted, "Assisted Permissions are disabled."));

        Assert.AreEqual("interactive/manual blocked:assisted=Assisted Permissions are disabled.", LastModes());
        Assert.IsFalse(await chat.SetPermissionModeAsync(CopilotPermissionMode.Assisted, TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task AgentModeChangedByTheCliIsShownAndKept()
    {
        await using var chat = Chat();
        await chat.SetAgentModeAsync(CopilotAgentMode.Plan, TestContext.CancellationToken);
        await chat.SendAsync("plan it", TestContext.CancellationToken);

        _sessions[0].Raise(new ChatEvent.AgentModeChanged(CopilotAgentMode.Interactive));

        Assert.AreEqual("interactive/manual", LastModes());
        Assert.AreEqual(CopilotAgentMode.Interactive, new ChatStore(_dir).List().Single().AgentMode);
    }

    [TestMethod]
    public async Task QuestionGetsAStreamedAnswer()
    {
        await using var chat = Chat();

        Assert.IsTrue(await chat.SendAsync("What is in here?", TestContext.CancellationToken));
        var session = _sessions.Single();
        session.Raise(new ChatEvent.AnswerDelta("a1", "Two "));
        session.Raise(new ChatEvent.AnswerDelta("a1", "files."));
        session.Raise(new ChatEvent.Answer("a1", "Two files."));
        session.Raise(new ChatEvent.Idle(false));

        CollectionAssert.AreEqual(new[] { "What is in here?" }, session.Sent);
        CollectionAssert.AreEqual(new[]
        {
            "user:What is in here?", "busy:True", "answerDelta:Two ", "answerDelta:files.", "answer:Two files.", "busy:False",
        }, Posted());
        Assert.IsFalse(chat.IsBusy);
    }

    [TestMethod]
    public async Task ChipsAreSentToTheCliAndShownWithTheMessageExactly()
    {
        await using var chat = Chat();
        var shown = new Pollux.Copilot.Context.ContextChip(Pollux.Copilot.Context.ContextChipKind.Selection, "a.txt", [@"C:\x\a.txt"]);

        await chat.SendAsync("Explain", [shown], TestContext.CancellationToken);

        var sent = _sessions.Single().Sent.Single();
        StringAssert.StartsWith(sent, "Explain");
        StringAssert.Contains(sent, @"C:\x\a.txt");
        using var user = JsonDocument.Parse(_posted.First());
        Assert.AreEqual("Explain", user.RootElement.GetProperty("text").GetString());
        Assert.AreEqual(shown.Id, user.RootElement.GetProperty("chips")[0].GetProperty("id").GetString());
    }

    [TestMethod]
    public async Task MessageWithoutChipsCarriesNoContext()
    {
        await using var chat = Chat();

        await chat.SendAsync("plain", [], TestContext.CancellationToken);

        Assert.AreEqual("plain", _sessions.Single().Sent.Single());
    }

    [TestMethod]
    public async Task FollowUpMessagesStayInTheSameChat()
    {
        await using var chat = Chat();
        await chat.SendAsync("one", TestContext.CancellationToken);
        _sessions[0].Raise(new ChatEvent.Idle(false));

        await chat.SendAsync("two", TestContext.CancellationToken);

        Assert.HasCount(1, _sessions);
        CollectionAssert.AreEqual(new[] { "one", "two" }, _sessions[0].Sent);
    }

    [TestMethod]
    public async Task MessageWhileAnsweringIsRefused()
    {
        await using var chat = Chat();
        await chat.SendAsync("one", TestContext.CancellationToken);

        Assert.IsFalse(await chat.SendAsync("two", TestContext.CancellationToken));
        CollectionAssert.AreEqual(new[] { "one" }, _sessions[0].Sent);
    }

    [TestMethod]
    public async Task StopAbortsTheAnswerAndRejectsOpenToolPrompts()
    {
        await using var chat = Chat();
        await chat.SendAsync("delete build", TestContext.CancellationToken);
        var decision = FakeSession.RequestTool(_approvals!, "rm -rf build");

        await chat.CancelAsync(TestContext.CancellationToken);

        Assert.IsTrue(_sessions[0].Aborted);
        Assert.IsFalse(await decision);
        CollectionAssert.Contains(Posted(), "approvalDone:False");
    }

    [TestMethod]
    public async Task ToolRequestIsShownAndAnsweredByTheUser()
    {
        await using var chat = Chat();
        await chat.SendAsync("delete build", TestContext.CancellationToken);
        var decision = FakeSession.RequestTool(_approvals!, "rm -rf build");

        CollectionAssert.Contains(Posted(), "approval:rm -rf build");
        Assert.IsFalse(decision.IsCompleted, "Tool requests are never decided without the user.");

        chat.Answer(_approvals!.Pending.Single(), approved: true);

        Assert.IsTrue(await decision);
        CollectionAssert.Contains(Posted(), "approvalDone:True");
    }

    [TestMethod]
    public async Task ToolActivityAndErrorsAreShown()
    {
        await using var chat = Chat();
        await chat.SendAsync("go", TestContext.CancellationToken);
        await _approvals!.PreToolUseAsync(Hook("view", """{"path":"C:\\work\\a.txt"}"""));
        _sessions[0].Raise(new ChatEvent.ToolStarted("c1", "Read a.txt", "view", """{"path":"C:\\work\\a.txt"}"""));
        _sessions[0].Raise(new ChatEvent.ToolCompleted("c1", false, "The user rejected this tool call."));
        _sessions[0].Raise(new ChatEvent.Failed("rate limited"));
        _sessions[0].Raise(new ChatEvent.Idle(false));

        CollectionAssert.AreEqual(new[]
        {
            "user:go", "busy:True", "tool:running", "tool:failed", "chatError:rate limited", "busy:False",
        }, Posted());
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task AToolTheGuardRulesHookNeverSawStopsTheTurnAndSwitchesTheChatToManual(bool subAgent)
    {
        _defaults = new(CopilotAgentMode.Interactive, CopilotPermissionMode.AllowAll);
        _actionLogs = new ActionLogStore(Path.Combine(_dir, "logs"));
        await using var chat = Chat();
        await chat.SendAsync("go", TestContext.CancellationToken);

        _sessions[0].Raise(new ChatEvent.ToolStarted("c1", "Run", "powershell", """{"command":"Remove-Item x"}""", subAgent));

        await WaitUntil(() => _sessions[0].Aborted && _sessions[0].ModesSet.LastOrDefault() == "permission:Manual");
        Assert.AreEqual(CopilotPermissionMode.Manual, new ChatStore(_dir).List().Single().PermissionMode);
        CollectionAssert.Contains(Posted(), "warning:" + CopilotChat.HookDidNotRunWarning);
        Assert.AreEqual(ToolApprovals.HookDidNotRun, _actionLogs.Open(chat.CurrentId!).Entries.Single().Reason);
    }

    [TestMethod]
    public async Task AToolTheGuardRulesHookSawRunsOn()
    {
        _defaults = new(CopilotAgentMode.Interactive, CopilotPermissionMode.AllowAll);
        await using var chat = Chat();
        await chat.SendAsync("go", TestContext.CancellationToken);

        await _approvals!.PreToolUseAsync(Hook("view", """{"path":"C:\\work\\a.txt"}"""));
        _sessions[0].Raise(new ChatEvent.ToolStarted("c1", "Read a.txt", "view", """{"path":"C:\\work\\a.txt"}"""));

        Assert.IsFalse(_sessions[0].Aborted);
        Assert.AreEqual("permission:AllowAll", _sessions[0].ModesSet.Last(m => m.StartsWith("permission:", StringComparison.Ordinal)));
    }

    private async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(10, TestContext.CancellationToken);
        Assert.IsTrue(condition(), string.Join("\n", Posted()));
    }

    [TestMethod]
    public async Task ChatThatCannotBeOpenedShowsWhyAndTriesAgainNextTime()
    {
        await using var chat = Chat();
        _openFails = new IOException("Communication error with Copilot CLI: 401 Unauthorized");

        Assert.IsFalse(await chat.SendAsync("hi", TestContext.CancellationToken));
        CollectionAssert.AreEqual(new[]
        {
            "user:hi", "busy:True", "chatError:Communication error with Copilot CLI: 401 Unauthorized", "busy:False",
        }, Posted());
        Assert.IsFalse(chat.IsBusy);

        _openFails = null;
        Assert.IsTrue(await chat.SendAsync("hi", TestContext.CancellationToken));
        Assert.HasCount(1, _sessions);
    }

    [TestMethod]
    public async Task ChatWhoseSendFailsIsReopenedForTheNextMessage()
    {
        await using var chat = Chat();
        await chat.SendAsync("one", TestContext.CancellationToken);
        _sessions[0].Raise(new ChatEvent.Idle(false));
        _sessions[0].SendFails = new IOException("CLI process exited unexpectedly.");

        Assert.IsFalse(await chat.SendAsync("two", TestContext.CancellationToken));
        Assert.IsTrue(_sessions[0].Disposed);
        Assert.IsFalse(chat.IsBusy);

        Assert.IsTrue(await chat.SendAsync("three", TestContext.CancellationToken));
        Assert.HasCount(2, _sessions);
    }

    [TestMethod]
    public async Task ClosingTheChatRejectsOpenPromptsAndDisposesTheSession()
    {
        var chat = Chat();
        await chat.SendAsync("go", TestContext.CancellationToken);
        var decision = FakeSession.RequestTool(_approvals!, "rm -rf build");

        await chat.DisposeAsync();

        Assert.IsFalse(await decision);
        Assert.IsTrue(_sessions[0].Disposed);
    }

    [TestMethod]
    public async Task StartNewDropsTheRunningChatAndSendsToAFreshSession()
    {
        await using var chat = Chat();
        await chat.SendAsync("old", TestContext.CancellationToken); // still answering: busy

        Assert.IsTrue(await chat.StartNewAsync("Explain", [], TestContext.CancellationToken));

        Assert.AreEqual(2, _sessions.Count);
        Assert.IsTrue(_sessions[0].Disposed);
        CollectionAssert.AreEqual(new[] { "Explain" }, _sessions[1].Sent);
        var posted = Posted();
        Assert.IsTrue(Array.IndexOf(posted, "clear") > Array.IndexOf(posted, "user:old"));
        Assert.IsTrue(Array.IndexOf(posted, "user:Explain") > Array.IndexOf(posted, "clear"));
    }

    [TestMethod]
    public async Task FirstMessageCreatesAChatTitledByIt()
    {
        await using var chat = Chat();

        await chat.SendAsync("Sort my downloads by type\nplease", TestContext.CancellationToken);

        var record = new ChatStore(_dir).List().Single();
        Assert.AreEqual("Sort my downloads by type", record.Title);
        Assert.AreEqual(_now, record.Created);
        Assert.IsFalse(_opened.Single().Resume);
        Assert.AreEqual(record.Id, _opened.Single().Id);
        Assert.AreEqual("Sort my downloads by type|" + record.Id, LastChats());
    }

    [TestMethod]
    public async Task ChatSurvivesARestartAndContinuesWithItsHistory()
    {
        string id;
        await using (var first = Chat())
        {
            await first.SendAsync("What is in here?", TestContext.CancellationToken);
            _sessions[0].Raise(new ChatEvent.Idle(false));
            id = new ChatStore(_dir).List().Single().Id;
        }

        _history = [new ChatTurn(true, "u1", "What is in here?"), new ChatTurn(false, "a1", "Two files.")];
        _now = _now.AddHours(1);
        _posted.Clear();
        await using var restarted = Chat();

        Assert.IsTrue(await restarted.OpenAsync(id, TestContext.CancellationToken));
        await restarted.SendAsync("And now?", TestContext.CancellationToken);

        Assert.IsTrue(_opened.Last().Resume);
        Assert.AreEqual(id, _opened.Last().Id);
        CollectionAssert.AreEqual(new[] { "And now?" }, _sessions.Last().Sent);
        CollectionAssert.AreEqual(new[]
        {
            "clear", "header", "user:What is in here?", "answer:Two files.", "model:", "user:And now?", "busy:True",
        }, Posted());
        Assert.AreEqual("What is in here?|" + id, LastChats());
        Assert.AreEqual(_now, new ChatStore(_dir).Get(id)!.Modified);
        Assert.HasCount(1, new ChatStore(_dir).List());
    }

    [TestMethod]
    public async Task ResumedChatKeepsItsModelAndWorkingFolder()
    {
        new ChatStore(_dir).Save(new ChatRecord { Id = "pollux-x", Title = "t", Created = _now, Modified = _now, Model = "gpt-5", WorkingFolder = @"C:\repos\pollux" });
        await using var chat = Chat();

        await chat.OpenAsync("pollux-x", TestContext.CancellationToken);

        Assert.AreEqual(("pollux-x", true, "gpt-5", @"C:\repos\pollux"), _opened.Single());
        using var header = JsonDocument.Parse(_posted.Last(m => m.Contains("\"header\"", StringComparison.Ordinal)));
        Assert.AreEqual(@"C:\repos\pollux", header.RootElement.GetProperty("workingFolder").GetString());
    }

    [TestMethod]
    public async Task NewChatStartsAnotherChatAndKeepsTheFirst()
    {
        await using var chat = Chat();
        await chat.SendAsync("one", TestContext.CancellationToken);
        _sessions[0].Raise(new ChatEvent.Idle(false));

        await chat.NewChatAsync();
        await chat.SendAsync("two", TestContext.CancellationToken);

        Assert.IsTrue(_sessions[0].Disposed);
        Assert.HasCount(2, new ChatStore(_dir).List());
        Assert.AreNotEqual(_opened[0].Id, _opened[1].Id);
        Assert.IsFalse(_opened[1].Resume);
    }

    [TestMethod]
    public async Task DeletingAChatRemovesItEverywhereAndEmptiesThePanelWhenItWasOpen()
    {
        await using var chat = Chat();
        await chat.SendAsync("one", TestContext.CancellationToken);
        _sessions[0].Raise(new ChatEvent.Idle(false));
        var id = new ChatStore(_dir).List().Single().Id;
        _posted.Clear();

        await chat.DeleteAsync(id, TestContext.CancellationToken);

        Assert.IsEmpty(new ChatStore(_dir).List());
        CollectionAssert.AreEqual(new[] { id }, _deletedInCli);
        Assert.IsTrue(_sessions[0].Disposed);
        CollectionAssert.AreEqual(new[] { "clear" }, Posted());
        Assert.AreEqual("|", LastChats());
    }

    [TestMethod]
    public async Task ModelChosenBeforeTheFirstMessageOpensTheChatWithIt()
    {
        await using var chat = Chat();

        Assert.IsTrue(chat.SelectModel("gpt-5"));
        await chat.SendAsync("hi", TestContext.CancellationToken);

        CollectionAssert.AreEqual(new string?[] { "gpt-5" }, _opened.Select(o => o.Model).ToArray());
        Assert.IsEmpty(_sessions[0].Models);
        Assert.AreEqual("gpt-5", chat.Model);
    }

    [TestMethod]
    public async Task SwitchingTheModelAppliesToTheNextMessageAndKeepsTheConversation()
    {
        await using var chat = Chat();
        await chat.SendAsync("one", TestContext.CancellationToken);
        _sessions[0].Raise(new ChatEvent.Idle(false));
        Assert.IsEmpty(_sessions[0].Models, "Nothing is switched before a message is sent.");

        Assert.IsTrue(chat.SelectModel("claude-sonnet-4.5"));
        Assert.IsEmpty(_sessions[0].Models);
        await chat.SendAsync("two", TestContext.CancellationToken);
        _sessions[0].Raise(new ChatEvent.Idle(false));
        await chat.SendAsync("three", TestContext.CancellationToken);

        Assert.HasCount(1, _sessions);
        CollectionAssert.AreEqual(new[] { "claude-sonnet-4.5" }, _sessions[0].Models, "Switched once, before the second message.");
        CollectionAssert.AreEqual(new[] { "one", "two", "three" }, _sessions[0].Sent);
    }

    [TestMethod]
    public async Task SwitchingWhileAnsweringKeepsTheRunningAnswerAndAppliesLater()
    {
        await using var chat = Chat();
        await chat.SendAsync("one", TestContext.CancellationToken);

        Assert.IsTrue(chat.SelectModel("gpt-5"));
        Assert.IsEmpty(_sessions[0].Models);
        _sessions[0].Raise(new ChatEvent.Idle(false));
        await chat.SendAsync("two", TestContext.CancellationToken);

        CollectionAssert.AreEqual(new[] { "gpt-5" }, _sessions[0].Models);
    }

    [TestMethod]
    public async Task StartedChatCannotGoBackToTheDefaultModel()
    {
        await using var chat = Chat();
        chat.SelectModel("gpt-5");
        await chat.SendAsync("one", TestContext.CancellationToken);
        _sessions[0].Raise(new ChatEvent.Idle(false));
        _posted.Clear();

        Assert.IsFalse(chat.SelectModel(null));

        Assert.AreEqual("gpt-5", chat.Model);
        CollectionAssert.AreEqual(new[] { "model:gpt-5" }, Posted(), "The Panel is told which model stays.");
    }

    [TestMethod]
    public async Task ModelThatCannotBeSwitchedToIsReportedAndTheChatGoesOnWithTheOldOne()
    {
        await using var chat = Chat();
        chat.SelectModel("gpt-5");
        await chat.SendAsync("one", TestContext.CancellationToken);
        _sessions[0].Raise(new ChatEvent.Idle(false));
        _sessions[0].SetModelFails = new IOException("model not available");
        chat.SelectModel("claude-opus");
        _posted.Clear();

        Assert.IsFalse(await chat.SendAsync("two", TestContext.CancellationToken));

        CollectionAssert.AreEqual(new[]
        {
            "user:two", "busy:True", "chatError:Could not switch to claude-opus: model not available", "model:gpt-5", "busy:False",
        }, Posted());
        Assert.AreEqual("gpt-5", chat.Model);
        Assert.IsFalse(_sessions[0].Disposed, "The conversation is kept.");
        Assert.IsFalse(chat.IsBusy);
    }

    [TestMethod]
    public async Task QuotaIsRefreshedAfterEveryAnswer()
    {
        var used = 10;
        _quota = _ => Task.FromResult<CopilotQuota?>(new CopilotQuota(false, 300, used, 90, null, false));
        await using var chat = Chat();
        await chat.SendAsync("one", TestContext.CancellationToken);
        Assert.IsFalse(Posted().Any(p => p.StartsWith("quota:", StringComparison.Ordinal)), "Not while the answer runs.");

        _sessions[0].Raise(new ChatEvent.Idle(false));
        used = 11;
        await chat.SendAsync("two", TestContext.CancellationToken);
        _sessions[0].Raise(new ChatEvent.Idle(false));

        CollectionAssert.AreEqual(new[] { "quota:290 of 300 premium requests left", "quota:289 of 300 premium requests left" },
            Posted().Where(p => p.StartsWith("quota:", StringComparison.Ordinal)).ToArray());
    }

    [TestMethod]
    public async Task QuotaThatCannotBeReadDoesNotDisturbTheChat()
    {
        _quota = _ => Task.FromException<CopilotQuota?>(new IOException("offline"));
        await using var chat = Chat();
        await chat.SendAsync("one", TestContext.CancellationToken);

        _sessions[0].Raise(new ChatEvent.Idle(false));

        Assert.IsFalse(chat.IsBusy);
        Assert.IsFalse(Posted().Any(p => p.StartsWith("quota", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task ModelOfAChatIsStoredAndComesBackWhenItIsOpened()
    {
        string id;
        await using (var first = Chat())
        {
            first.SelectModel("gpt-5");
            await first.SendAsync("one", TestContext.CancellationToken);
            id = new ChatStore(_dir).List().Single().Id;
        }

        Assert.AreEqual("gpt-5", new ChatStore(_dir).Get(id)!.Model);
        await using var restarted = Chat();

        Assert.IsTrue(await restarted.OpenAsync(id, TestContext.CancellationToken));

        Assert.AreEqual("gpt-5", restarted.Model);
        Assert.AreEqual("gpt-5", _opened.Last().Model);
        Assert.IsEmpty(_sessions.Last().Models, "The resumed session already uses it.");
    }

    [TestMethod]
    public async Task ChatCannotBeSwitchedWhileCopilotIsAnswering()
    {
        new ChatStore(_dir).Save(new ChatRecord { Id = "pollux-x", Title = "t", Created = _now, Modified = _now });
        await using var chat = Chat();
        await chat.SendAsync("one", TestContext.CancellationToken);

        Assert.IsFalse(await chat.OpenAsync("pollux-x", TestContext.CancellationToken));
        Assert.HasCount(1, _sessions);
    }

    [TestMethod]
    public async Task ChatThatCannotBeResumedShowsWhy()
    {
        new ChatStore(_dir).Save(new ChatRecord { Id = "pollux-x", Title = "t", Created = _now, Modified = _now });
        await using var chat = Chat();
        _openFails = new IOException("Session not found");

        Assert.IsFalse(await chat.OpenAsync("pollux-x", TestContext.CancellationToken));
        CollectionAssert.Contains(Posted(), "chatError:Session not found");
    }

    public TestContext TestContext { get; set; } = null!;

    private sealed class FakeSession : IChatSession
    {
        private Action<ChatEvent>? _handler;

        public List<string> Sent { get; } = [];
        public bool Aborted { get; private set; }
        public bool Disposed { get; private set; }
        public Exception? SendFails { get; set; }
        public IReadOnlyList<ChatTurn> History { get; set; } = [];
        public Exception? SetModelFails { get; set; }
        public List<string> Models { get; } = [];

        /// <summary>The modes the Chat set in the CLI, in order ("agent:Plan", "permission:AllowAll").</summary>
        public List<string> ModesSet { get; } = [];

        /// <summary>What the organization's policy blocks for this session.</summary>
        public ModePolicy Policy { get; set; } = ModePolicy.None;

        /// <summary>Permission Modes the CLI refuses although no policy blocks them (e.g. its judge model is unavailable).</summary>
        public HashSet<CopilotPermissionMode> CliRefuses { get; set; } = [];

        public Task SetAgentModeAsync(CopilotAgentMode mode, CancellationToken cancellationToken)
        {
            ModesSet.Add("agent:" + mode);
            return Task.CompletedTask;
        }

        public Task<PermissionModeResult> SetPermissionModeAsync(CopilotPermissionMode mode, CancellationToken cancellationToken)
        {
            ModesSet.Add("permission:" + mode);
            return Task.FromResult(Policy.BlockedReason(mode) is null && !CliRefuses.Contains(mode)
                ? new PermissionModeResult(true, mode)
                : new PermissionModeResult(false, CopilotPermissionMode.Manual));
        }

        public SandboxPolicy SandboxPolicy { get; set; } = SandboxPolicy.None;
        public Exception? SandboxFails { get; set; }

        /// <summary>What the Chat told the CLI about the sandbox ("on"/"off"), in order.</summary>
        public List<string> SandboxSet { get; } = [];

        public Task<SandboxPolicy> GetSandboxPolicyAsync(CancellationToken cancellationToken) => Task.FromResult(SandboxPolicy);

        public Task SetSandboxAsync(bool enabled, CancellationToken cancellationToken)
        {
            if (SandboxFails is { } ex) return Task.FromException(ex);
            SandboxSet.Add(enabled ? "on" : "off");
            return Task.CompletedTask;
        }

        public Exception? PolicyFails { get; set; }

        public Task<ModePolicy> GetModePolicyAsync(CancellationToken cancellationToken) =>
            PolicyFails is { } ex ? Task.FromException<ModePolicy>(ex) : Task.FromResult(Policy);

        public Task<IReadOnlyList<ChatTurn>> GetHistoryAsync(CancellationToken cancellationToken) => Task.FromResult(History);

        public void Raise(ChatEvent e) => _handler?.Invoke(e);

        /// <summary>What the CLI does for a shell tool: asks the Chat's approvals and waits.</summary>
        public static Task<bool> RequestTool(ToolApprovals approvals, string command) =>
            approvals.AskAsync(new ToolApprovalRequest("", "shell", "Run a command", command, CanAllowForChat: true));

        public IDisposable Subscribe(Action<ChatEvent> handler)
        {
            _handler = handler;
            return new Unsubscribe(() => _handler = null);
        }

        public Task SendAsync(string prompt, CancellationToken cancellationToken)
        {
            if (SendFails is { } ex) return Task.FromException(ex);
            Sent.Add(prompt);
            return Task.CompletedTask;
        }

        public Task SetModelAsync(string modelId, CancellationToken cancellationToken)
        {
            if (SetModelFails is { } ex) return Task.FromException(ex);
            Models.Add(modelId);
            return Task.CompletedTask;
        }

        public Task AbortAsync(CancellationToken cancellationToken)
        {
            Aborted = true;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }

        private sealed class Unsubscribe(Action action) : IDisposable
        {
            public void Dispose() => action();
        }
    }
}
