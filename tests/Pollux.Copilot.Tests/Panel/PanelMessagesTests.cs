using System.Text.Json;
using Pollux.Copilot.Chat;
using Pollux.Copilot.Models;
using Pollux.Copilot.Panel;
using Pollux.Core.Settings;

namespace Pollux.Copilot.Tests.Panel;

[TestClass]
public sealed class PanelMessagesTests
{
    [TestMethod]
    public void ThemeMessageCarriesDarkFlag()
    {
        Assert.AreEqual("""{"type":"theme","dark":true}""", PanelMessages.Theme(true));
        Assert.AreEqual("""{"type":"theme","dark":false}""", PanelMessages.Theme(false));
    }

    [TestMethod]
    public void HeaderPlaceholderHasNoWorkingFolderAndNoTaint()
    {
        using var doc = JsonDocument.Parse(PanelMessages.Header(PanelHeader.Placeholder));
        var root = doc.RootElement;
        Assert.AreEqual("header", root.GetProperty("type").GetString());
        Assert.AreEqual(JsonValueKind.Null, root.GetProperty("workingFolder").ValueKind);
        Assert.IsFalse(root.GetProperty("tainted").GetBoolean());
        Assert.IsFalse(root.GetProperty("sandbox").GetBoolean());
    }

    [TestMethod]
    public void HeaderEscapesWorkingFolder()
    {
        var header = new PanelHeader("C:\\repos\\\"x\"", true, true);
        using var doc = JsonDocument.Parse(PanelMessages.Header(header));
        Assert.AreEqual("C:\\repos\\\"x\"", doc.RootElement.GetProperty("workingFolder").GetString());
        Assert.IsTrue(doc.RootElement.GetProperty("tainted").GetBoolean());
    }

    [TestMethod]
    public void ModesMessageOffersEveryModeAndMarksBlockedOnesWithTheReason()
    {
        var policy = ModePolicy.None.Block(CopilotPermissionMode.AllowAll, "Blocked by your organization's Copilot policy.");
        using var doc = JsonDocument.Parse(PanelMessages.Modes(new ChatModes(CopilotAgentMode.Plan, CopilotPermissionMode.Assisted), policy));
        var root = doc.RootElement;

        Assert.AreEqual("modes", root.GetProperty("type").GetString());
        Assert.AreEqual("plan", root.GetProperty("agentMode").GetString());
        Assert.AreEqual("assisted", root.GetProperty("permissionMode").GetString());
        CollectionAssert.AreEqual(new[] { "interactive:Interactive", "plan:Plan", "autopilot:Autopilot" },
            root.GetProperty("agentModes").EnumerateArray().Select(m => m.GetProperty("id").GetString() + ":" + m.GetProperty("label").GetString()).ToArray());
        var permission = root.GetProperty("permissionModes").EnumerateArray().ToArray();
        CollectionAssert.AreEqual(new[] { "manual:Manual", "assisted:Assisted", "allowAll:Allow All" },
            permission.Select(m => m.GetProperty("id").GetString() + ":" + m.GetProperty("label").GetString()).ToArray());
        Assert.AreEqual(JsonValueKind.Null, permission[0].GetProperty("blocked").ValueKind);
        Assert.AreEqual("Blocked by your organization's Copilot policy.", permission[2].GetProperty("blocked").GetString());
    }

    [TestMethod]
    public void ParsesModeSwitches()
    {
        Assert.AreEqual(new PanelEvent.SetAgentMode(CopilotAgentMode.Autopilot), PanelMessages.Parse("""{"type":"setAgentMode","mode":"autopilot"}"""));
        Assert.AreEqual(new PanelEvent.SetPermissionMode(CopilotPermissionMode.AllowAll), PanelMessages.Parse("""{"type":"setPermissionMode","mode":"allowAll"}"""));
        Assert.AreEqual(new PanelEvent.SetPermissionMode(CopilotPermissionMode.Manual), PanelMessages.Parse("""{"type":"setPermissionMode","mode":"manual"}"""));
        Assert.IsNull(PanelMessages.Parse("""{"type":"setAgentMode","mode":"yolo"}"""));
        Assert.IsNull(PanelMessages.Parse("""{"type":"setPermissionMode","mode":"2"}"""));
        Assert.IsNull(PanelMessages.Parse("""{"type":"setPermissionMode"}"""));
    }

    [TestMethod]
    public void MarkdownMessageRoundTripsText()
    {
        const string md = "# Hi\n\n```cs\nvar a = \"</script>\";\n```";
        using var doc = JsonDocument.Parse(PanelMessages.Markdown(md));
        Assert.AreEqual(md, doc.RootElement.GetProperty("text").GetString());
    }

    [TestMethod]
    public void StateNoneClearsTheStateView()
    {
        using var doc = JsonDocument.Parse(PanelMessages.State(PanelStateView.None));
        Assert.AreEqual("", doc.RootElement.GetProperty("kind").GetString());
    }

    [TestMethod]
    public void ParsesKnownEvents()
    {
        Assert.IsInstanceOfType<PanelEvent.Ready>(PanelMessages.Parse("""{"type":"ready"}"""));
        Assert.IsInstanceOfType<PanelEvent.Close>(PanelMessages.Parse("""{"type":"close"}"""));
        var link = PanelMessages.Parse("""{"type":"openLink","url":"https://example.com/a?b=1"}""");
        Assert.AreEqual("https://example.com/a?b=1", ((PanelEvent.OpenLink)link!).Url);
    }

    [TestMethod]
    public void ParsesChatEvents()
    {
        var send = (PanelEvent.Send)PanelMessages.Parse("""{"type":"send","text":"What is in here?"}""")!;
        Assert.AreEqual("What is in here?", send.Text);
        Assert.HasCount(0, send.ChipIds);
        Assert.IsInstanceOfType<PanelEvent.Cancel>(PanelMessages.Parse("""{"type":"cancel"}"""));
        Assert.AreEqual(new PanelEvent.Approve("t1", ApprovalAnswer.AllowOnce), PanelMessages.Parse("""{"type":"approve","id":"t1","approved":true}"""));
        Assert.AreEqual(new PanelEvent.Approve("t2", ApprovalAnswer.Deny), PanelMessages.Parse("""{"type":"approve","id":"t2","approved":false}"""));
        Assert.AreEqual(new PanelEvent.Approve("t3", ApprovalAnswer.AllowForChat), PanelMessages.Parse("""{"type":"approve","id":"t3","approved":true,"forChat":true}"""));
        Assert.AreEqual(new PanelEvent.Approve("t4", ApprovalAnswer.Deny), PanelMessages.Parse("""{"type":"approve","id":"t4","approved":false,"forChat":true}"""));
    }

    [TestMethod]
    public void ParsesChatListEvents()
    {
        Assert.IsInstanceOfType<PanelEvent.NewChat>(PanelMessages.Parse("""{"type":"newChat"}"""));
        Assert.AreEqual(new PanelEvent.OpenChat("pollux-1"), PanelMessages.Parse("""{"type":"openChat","id":"pollux-1"}"""));
        Assert.AreEqual(new PanelEvent.DeleteChat("pollux-1"), PanelMessages.Parse("""{"type":"deleteChat","id":"pollux-1"}"""));
        Assert.IsNull(PanelMessages.Parse("""{"type":"openChat"}"""));
        Assert.IsNull(PanelMessages.Parse("""{"type":"deleteChat","id":""}"""));
    }

    [TestMethod]
    public void ChatsMessageListsChatsAndTheOpenOne()
    {
        var chats = new[]
        {
            new Pollux.Copilot.Chat.ChatRecord { Id = "b", Title = "Say \"hi\"", Modified = new DateTimeOffset(2026, 10, 9, 12, 5, 0, TimeSpan.Zero), WorkingFolder = @"C:\x" },
            new Pollux.Copilot.Chat.ChatRecord { Id = "a", Title = "old", Modified = new DateTimeOffset(2026, 10, 8, 9, 0, 0, TimeSpan.Zero) },
        };

        using var doc = JsonDocument.Parse(PanelMessages.Chats(chats, "b"));
        var root = doc.RootElement;
        Assert.AreEqual("chats", root.GetProperty("type").GetString());
        Assert.AreEqual("b", root.GetProperty("current").GetString());
        var list = root.GetProperty("chats").EnumerateArray().ToArray();
        Assert.AreEqual("Say \"hi\"", list[0].GetProperty("title").GetString());
        Assert.AreEqual("2026-10-09T12:05:00+00:00", list[0].GetProperty("modified").GetString());
        Assert.AreEqual(@"C:\x", list[0].GetProperty("workingFolder").GetString());
        Assert.AreEqual(JsonValueKind.Null, list[1].GetProperty("workingFolder").ValueKind);
        Assert.AreEqual("""{"type":"clear"}""", PanelMessages.Clear());
        using var none = JsonDocument.Parse(PanelMessages.Chats([], null));
        Assert.AreEqual("", none.RootElement.GetProperty("current").GetString());
    }

    [TestMethod]
    public void ActionLogRequestParsesAndTheLogMessageListsEntries()
    {
        Assert.AreEqual(new PanelEvent.ActionLogRequest(), PanelMessages.Parse("""{"type":"actionLog"}"""));

        var entry = new Pollux.Copilot.Chat.ActionLogEntry
        {
            Id = 3, Time = new DateTimeOffset(2026, 10, 9, 12, 5, 0, TimeSpan.Zero), Tool = "powershell", Command = "format C:",
            Paths = [@"C:\x"], Urls = ["https://e.com"], Decision = Pollux.Copilot.Chat.ActionDecision.Blocked,
            DecidedBy = Pollux.Copilot.Chat.ActionDecider.GuardRule, Reason = "formatting",
        };
        using var doc = JsonDocument.Parse(PanelMessages.ActionLog([entry]));
        Assert.AreEqual("actionLog", doc.RootElement.GetProperty("type").GetString());
        var e = doc.RootElement.GetProperty("entries").EnumerateArray().Single();
        Assert.AreEqual(3, e.GetProperty("id").GetInt32());
        Assert.AreEqual("powershell", e.GetProperty("tool").GetString());
        Assert.AreEqual("format C:", e.GetProperty("command").GetString());
        Assert.AreEqual("blocked", e.GetProperty("decision").GetString());
        Assert.AreEqual("guardRule", e.GetProperty("decidedBy").GetString());
        Assert.AreEqual("formatting", e.GetProperty("reason").GetString());
        Assert.AreEqual(@"C:\x", e.GetProperty("paths")[0].GetString());
        Assert.AreEqual("https://e.com", e.GetProperty("urls")[0].GetString());
        Assert.AreEqual("2026-10-09T12:05:00+00:00", e.GetProperty("time").GetString());
    }

    [TestMethod]
    public void AnswerDeltaCarriesMessageIdAndText()
    {
        using var doc = JsonDocument.Parse(PanelMessages.AnswerDelta("a1", "Hel\"lo"));
        Assert.AreEqual("answerDelta", doc.RootElement.GetProperty("type").GetString());
        Assert.AreEqual("a1", doc.RootElement.GetProperty("id").GetString());
        Assert.AreEqual("Hel\"lo", doc.RootElement.GetProperty("text").GetString());
    }

    [TestMethod]
    public void ApprovalCarriesTheFullRequest()
    {
        var request = new Pollux.Copilot.Chat.ToolApprovalRequest("t1", "write", "Fix the typo", @"C:\src\a.cs", "-a\n+b");
        using var doc = JsonDocument.Parse(PanelMessages.Approval(request));
        var root = doc.RootElement;
        Assert.AreEqual("approval", root.GetProperty("type").GetString());
        Assert.AreEqual("t1", root.GetProperty("id").GetString());
        Assert.AreEqual("write", root.GetProperty("kind").GetString());
        Assert.AreEqual("Fix the typo", root.GetProperty("title").GetString());
        Assert.AreEqual(@"C:\src\a.cs", root.GetProperty("detail").GetString());
        Assert.AreEqual("-a\n+b", root.GetProperty("diff").GetString());
    }

    [TestMethod]
    public void ApprovalShowsTheGuardRulesReasonAndThePaths()
    {
        var request = new Pollux.Copilot.Chat.ToolApprovalRequest("t1", "shell", "Run a command", "rm -rf build", null, "Recursive delete (rm)", [@"C:\work\build"]);
        using var doc = JsonDocument.Parse(PanelMessages.Approval(request));
        var root = doc.RootElement;
        Assert.AreEqual("Recursive delete (rm)", root.GetProperty("reason").GetString());
        Assert.AreEqual(@"C:\work\build", root.GetProperty("paths")[0].GetString());

        using var plain = JsonDocument.Parse(PanelMessages.Approval(new Pollux.Copilot.Chat.ToolApprovalRequest("t2", "shell", "Run", "dir")));
        Assert.AreEqual(JsonValueKind.Null, plain.RootElement.GetProperty("reason").ValueKind);
        Assert.AreEqual(0, plain.RootElement.GetProperty("paths").GetArrayLength());
    }

    [TestMethod]
    public void ApprovalSaysWhetherAllowForThisChatIsOffered()
    {
        using var guardRule = JsonDocument.Parse(PanelMessages.Approval(new Pollux.Copilot.Chat.ToolApprovalRequest("t1", "shell", "Run", "git push", null, "Reaches the network")));
        using var mode = JsonDocument.Parse(PanelMessages.Approval(new Pollux.Copilot.Chat.ToolApprovalRequest("t2", "shell", "Run", "dir", CanAllowForChat: true)));

        Assert.IsFalse(guardRule.RootElement.GetProperty("allowForChat").GetBoolean());
        Assert.IsTrue(mode.RootElement.GetProperty("allowForChat").GetBoolean());
    }

    [TestMethod]
    public void ApprovalDoneSaysHowItWasAnswered()
    {
        using var doc = JsonDocument.Parse(PanelMessages.ApprovalDone("t1", ApprovalAnswer.AllowForChat));
        Assert.IsTrue(doc.RootElement.GetProperty("approved").GetBoolean());
        Assert.AreEqual("allowForChat", doc.RootElement.GetProperty("answer").GetString());
    }

    [TestMethod]
    public void ParsesWorkingFolderEvents()
    {
        Assert.IsInstanceOfType<PanelEvent.UseActiveFolder>(PanelMessages.Parse("""{"type":"useActiveFolder"}"""));
        Assert.IsInstanceOfType<PanelEvent.ChooseWorkingFolder>(PanelMessages.Parse("""{"type":"chooseWorkingFolder"}"""));
    }

    [TestMethod]
    public void HeaderSaysWhatMadeTheChatTainted()
    {
        using var doc = JsonDocument.Parse(PanelMessages.Header(new PanelHeader(null, false, true, "Read from an untrusted place (~\\Downloads)")));

        Assert.IsTrue(doc.RootElement.GetProperty("tainted").GetBoolean());
        Assert.AreEqual("Read from an untrusted place (~\\Downloads)", doc.RootElement.GetProperty("taintedBy").GetString());
    }

    [TestMethod]
    public void ParsesClearTainted() =>
        Assert.IsInstanceOfType<PanelEvent.ClearTainted>(PanelMessages.Parse("""{"type":"clearTainted"}"""));

    [TestMethod]
    public void ModelPickerMessages()
    {
        using var models = JsonDocument.Parse(PanelMessages.Models([new ChatModel("gpt-5", "GPT-5"), new ChatModel("claude-sonnet-4.5", "Claude Sonnet 4.5")]));
        var list = models.RootElement.GetProperty("models");
        Assert.AreEqual("models", models.RootElement.GetProperty("type").GetString());
        Assert.AreEqual(2, list.GetArrayLength());
        Assert.AreEqual("claude-sonnet-4.5", list[1].GetProperty("id").GetString());
        Assert.AreEqual("Claude Sonnet 4.5", list[1].GetProperty("name").GetString());

        Assert.AreEqual("""{"type":"model","id":"gpt-5"}""", PanelMessages.Model("gpt-5"));
        Assert.AreEqual("""{"type":"model","id":null}""", PanelMessages.Model(null));
    }

    [TestMethod]
    public void QuotaMessageCarriesTextAndLevel()
    {
        Assert.AreEqual("""{"type":"quota","text":"142 of 300 premium requests left","level":"ok"}""",
            PanelMessages.Quota(new CopilotQuota(false, 300, 158, 47.3, null, false)));
        Assert.AreEqual("""{"type":"quota","text":null,"level":null}""", PanelMessages.Quota(null));
    }

    [TestMethod]
    public void ParsesModelSelection()
    {
        Assert.AreEqual(new PanelEvent.SelectModel("gpt-5"), PanelMessages.Parse("""{"type":"selectModel","id":"gpt-5"}"""));
        Assert.AreEqual(new PanelEvent.SelectModel(null), PanelMessages.Parse("""{"type":"selectModel","id":null}"""));
    }

    [TestMethod]
    [DataRow("""{"type":"selectModel"}""")]
    [DataRow("""{"type":"selectModel","id":""}""")]
    [DataRow("""{"type":"selectModel","id":7}""")]
    public void IgnoresMalformedModelSelection(string json) => Assert.IsNull(PanelMessages.Parse(json));

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("not json")]
    [DataRow("[]")]
    [DataRow("""{"type":5}""")]
    [DataRow("""{"type":"launchMissiles"}""")]
    [DataRow("""{"type":"openLink"}""")]
    [DataRow("""{"type":"openLink","url":"file:///C:/Windows/System32/cmd.exe"}""")]
    [DataRow("""{"type":"openLink","url":"javascript:alert(1)"}""")]
    [DataRow("""{"type":"openLink","url":"relative/path"}""")]
    [DataRow("""{"type":"send"}""")]
    [DataRow("""{"type":"send","text":"   "}""")]
    [DataRow("""{"type":"send","text":7}""")]
    [DataRow("""{"type":"approve","id":"t1"}""")]
    [DataRow("""{"type":"approve","approved":true}""")]
    [DataRow("""{"type":"approve","id":"t1","approved":"yes"}""")]
    public void IgnoresMalformedOrUnsafeMessages(string? json) => Assert.IsNull(PanelMessages.Parse(json));

    [TestMethod]
    public void SendCarriesTheIdsOfTheChipsShown()
    {
        var send = (PanelEvent.Send)PanelMessages.Parse("""{"type":"send","text":"Q","chips":["location:ab","selection:cd"]}""")!;

        CollectionAssert.AreEqual(new[] { "location:ab", "selection:cd" }, send.ChipIds.ToArray());
    }

    [TestMethod]
    public void SendIgnoresMalformedChipIds()
    {
        var send = (PanelEvent.Send)PanelMessages.Parse("""{"type":"send","text":"Q","chips":["a",7,null]}""")!;

        CollectionAssert.AreEqual(new[] { "a" }, send.ChipIds.ToArray());
    }

    [TestMethod]
    public void ParsesMentionQueryAndContextRequest()
    {
        Assert.AreEqual(new PanelEvent.Mention("no"), PanelMessages.Parse("""{"type":"mention","query":"no"}"""));
        Assert.IsInstanceOfType<PanelEvent.ContextRequest>(PanelMessages.Parse("""{"type":"contextRequest"}"""));
        Assert.IsNull(PanelMessages.Parse("""{"type":"mention"}"""));
    }

    [TestMethod]
    public void ContextMessageListsTheChips()
    {
        var chip = new Pollux.Copilot.Context.ContextChip(Pollux.Copilot.Context.ContextChipKind.Selection, "a.txt", [@"C:\x\a.txt"]);
        using var doc = JsonDocument.Parse(PanelMessages.Context([chip]));
        var c = doc.RootElement.GetProperty("chips")[0];

        Assert.AreEqual("context", doc.RootElement.GetProperty("type").GetString());
        Assert.AreEqual(chip.Id, c.GetProperty("id").GetString());
        Assert.AreEqual("selection", c.GetProperty("kind").GetString());
        Assert.AreEqual("a.txt", c.GetProperty("label").GetString());
        Assert.AreEqual(@"C:\x\a.txt", c.GetProperty("title").GetString());
    }

    [TestMethod]
    public void MentionsAndAddedChipsAreSeparateMessages()
    {
        using var m = JsonDocument.Parse(PanelMessages.Mentions("no", []));
        using var a = JsonDocument.Parse(PanelMessages.ChipsAdded([]));

        Assert.AreEqual("mentions", m.RootElement.GetProperty("type").GetString());
        Assert.AreEqual("no", m.RootElement.GetProperty("query").GetString());
        Assert.AreEqual("chipsAdded", a.RootElement.GetProperty("type").GetString());
    }

    [TestMethod]
    public void UserMessageShowsTheChipsItCarried()
    {
        var chip = new Pollux.Copilot.Context.ContextChip(Pollux.Copilot.Context.ContextChipKind.Location, @"C:\x", [@"C:\x"]);
        using var doc = JsonDocument.Parse(PanelMessages.UserMessage("u1", "hi", [chip]));

        Assert.AreEqual(@"C:\x", doc.RootElement.GetProperty("chips")[0].GetProperty("label").GetString());
    }

    [TestMethod]
    public void ParsesDropNotice()
    {
        Assert.IsInstanceOfType<PanelEvent.Drop>(PanelMessages.Parse("""{"type":"drop"}"""));
    }

    [TestMethod]
    public void SandboxMessageCarriesTheStateAndWhyItIsLocked()
    {
        using var open = JsonDocument.Parse(PanelMessages.Sandbox(new Pollux.Copilot.Chat.SandboxState(true, null)));
        using var locked = JsonDocument.Parse(PanelMessages.Sandbox(new Pollux.Copilot.Chat.SandboxState(false, "Not supported.")));

        Assert.AreEqual("sandbox", open.RootElement.GetProperty("type").GetString());
        Assert.IsTrue(open.RootElement.GetProperty("enabled").GetBoolean());
        Assert.AreEqual(JsonValueKind.Null, open.RootElement.GetProperty("locked").ValueKind);
        Assert.AreEqual("Not supported.", locked.RootElement.GetProperty("locked").GetString());
    }

    [TestMethod]
    public void ParsesSandboxSwitchOnlyWithABoolean()
    {
        Assert.AreEqual(new PanelEvent.SetSandbox(true), PanelMessages.Parse("""{"type":"setSandbox","enabled":true}"""));
        Assert.AreEqual(new PanelEvent.SetSandbox(false), PanelMessages.Parse("""{"type":"setSandbox","enabled":false}"""));
        Assert.IsNull(PanelMessages.Parse("""{"type":"setSandbox","enabled":"yes"}"""));
        Assert.IsNull(PanelMessages.Parse("""{"type":"setSandbox"}"""));
    }
}
