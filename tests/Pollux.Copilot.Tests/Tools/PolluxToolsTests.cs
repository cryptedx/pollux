using System.Text.Json;
using Microsoft.Extensions.AI;
using Pollux.Copilot.Chat;
using Pollux.Copilot.Tools;

namespace Pollux.Copilot.Tests.Tools;

[TestClass]
public sealed class PolluxToolsTests
{
    private sealed class FakeActions : IPolluxActions
    {
        public List<PaneSnapshot> Panes { get; set; } =
        [
            new("left", true, @"C:\work", [@"C:\work", @"D:\"], 0, [@"C:\work\a.pdf"], [new("a.pdf", false), new("b.pdf", false), new("c.txt", false), new("sub", true)]),
            new("right", false, @"C:\inbox", [@"C:\inbox"], 0, [], [new("x.PDF", false), new("y.docx", false)]),
        ];

        public (string Pane, string Location, bool NewTab)? Navigated { get; private set; }
        public (string Pane, string[] Names)? Selected { get; private set; }
        public string? NavigateError { get; set; }

        public Task<IReadOnlyList<PaneSnapshot>> ReadPanesAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PaneSnapshot>>(Panes);

        public Task<NavigateResult> NavigateAsync(string pane, string location, bool newTab, CancellationToken cancellationToken)
        {
            Navigated = (pane, location, newTab);
            if (NavigateError is { } e) return Task.FromResult(new NavigateResult(false, null, 0, e));
            Panes[1] = Panes[1] with { Folder = location, Entries = [new("one.pdf", false), new("two.pdf", false), new("three.txt", false)], Selection = [] };
            return Task.FromResult(new NavigateResult(true, location, 3, null));
        }

        public Task<int> SelectAsync(string pane, IReadOnlyList<string> names, CancellationToken cancellationToken)
        {
            Selected = (pane, [.. names]);
            return Task.FromResult(names.Count);
        }
    }

    private readonly FakeActions _actions = new();

    private AIFunction Tool(string name) =>
        PolluxTools.Create(_actions, readOnly: false).Single(t => t.Name == name);

    private async Task<JsonElement> Call(string name, params (string Key, object? Value)[] args)
    {
        var tool = Tool(name);
        var arguments = new AIFunctionArguments();
        foreach (var (k, v) in args) arguments[k] = v;
        var result = await tool.InvokeAsync(arguments, TestContext.CancellationToken);
        return JsonSerializer.SerializeToElement(result, tool.JsonSerializerOptions.GetTypeInfo(typeof(object)));
    }

    [TestMethod]
    public void ToolsAreNamedAfterPolluxAndAllOfferedOutsidePlanMode() =>
        CollectionAssert.AreEquivalent(
            new[] { "pollux_read_panes", "pollux_navigate", "pollux_select_entries" },
            PolluxTools.Create(_actions, readOnly: false).Select(t => t.Name).ToArray());

    [TestMethod]
    public void PlanModeOffersOnlyTheReadTool() =>
        CollectionAssert.AreEqual(new[] { "pollux_read_panes" }, PolluxTools.Create(_actions, readOnly: true).Select(t => t.Name).ToArray());

    private sealed class PlanGate : IPolluxToolGate
    {
        public string? ReadOnlyReason => "Plan mode: only read tools are available.";

        public Task<string?> AuthorizeAsync(PolluxToolCall toolCall, CancellationToken cancellationToken) => Task.FromResult(ReadOnlyReason);
    }

    [TestMethod]
    [DataRow("pollux_navigate")]
    [DataRow("pollux_select_entries")]
    public async Task NavigationToolsChangeNothingWhileTheChatIsInPlanMode(string name)
    {
        var tool = PolluxTools.Create(_actions, readOnly: false, new PlanGate()).Single(t => t.Name == name);
        var arguments = new AIFunctionArguments { ["location"] = @"D:\x", ["patterns"] = new[] { "*.pdf" } };

        var result = await tool.InvokeAsync(arguments, TestContext.CancellationToken);
        var json = JsonSerializer.SerializeToElement(result, tool.JsonSerializerOptions.GetTypeInfo(typeof(object)));

        Assert.IsFalse(json.GetProperty("ok").GetBoolean());
        StringAssert.Contains(json.GetProperty("error").GetString(), "Plan");
        Assert.IsNull(_actions.Navigated);
        Assert.IsNull(_actions.Selected);
    }

    [TestMethod]
    public async Task ReadPanesReportsLocationTabsAndSelectionOfBothPanes()
    {
        var result = await Call("pollux_read_panes");

        var panes = result.GetProperty("panes");
        Assert.AreEqual(2, panes.GetArrayLength());
        var left = panes[0];
        Assert.AreEqual("left", left.GetProperty("pane").GetString());
        Assert.IsTrue(left.GetProperty("active").GetBoolean());
        Assert.AreEqual(@"C:\work", left.GetProperty("folder").GetString());
        Assert.AreEqual(2, left.GetProperty("tabs").GetArrayLength());
        Assert.AreEqual(@"C:\work\a.pdf", left.GetProperty("selection")[0].GetString());
        Assert.AreEqual(4, left.GetProperty("entryCount").GetInt32());
        Assert.AreEqual("sub", left.GetProperty("entries")[3].GetProperty("name").GetString());
        Assert.IsTrue(left.GetProperty("entries")[3].GetProperty("isFolder").GetBoolean());
    }

    [TestMethod]
    public async Task ReadPanesCapsLongListingsAndSaysSo()
    {
        _actions.Panes[0] = _actions.Panes[0] with { Entries = [.. Enumerable.Range(0, 450).Select(i => new PaneEntry($"f{i}.txt", false))] };

        var left = (await Call("pollux_read_panes")).GetProperty("panes")[0];

        Assert.AreEqual(450, left.GetProperty("entryCount").GetInt32());
        Assert.AreEqual(PolluxTools.MaxListedEntries, left.GetProperty("entries").GetArrayLength());
        Assert.IsTrue(left.GetProperty("entriesTruncated").GetBoolean());
    }

    [TestMethod]
    public async Task NavigateOpensTheFolderInTheChosenPane()
    {
        var result = await Call("pollux_navigate", ("pane", "right"), ("location", @"C:\docs"));

        Assert.AreEqual(("right", @"C:\docs", false), _actions.Navigated);
        Assert.IsTrue(result.GetProperty("ok").GetBoolean());
        Assert.AreEqual(@"C:\docs", result.GetProperty("location").GetString());
        Assert.AreEqual(3, result.GetProperty("entryCount").GetInt32());
    }

    [TestMethod]
    public async Task NavigateDefaultsToTheActivePaneAndCanOpenATab()
    {
        await Call("pollux_navigate", ("location", @"C:\docs"), ("newTab", true));

        Assert.AreEqual(("active", @"C:\docs", true), _actions.Navigated);
    }

    [TestMethod]
    public async Task NavigateRejectsUnknownPaneWithoutTouchingPollux()
    {
        var result = await Call("pollux_navigate", ("pane", "middle"), ("location", @"C:\docs"));

        Assert.IsFalse(result.GetProperty("ok").GetBoolean());
        StringAssert.Contains(result.GetProperty("error").GetString(), "left, right, active or other");
        Assert.IsNull(_actions.Navigated);
    }

    [TestMethod]
    public async Task NavigateRejectsEmptyLocation()
    {
        var result = await Call("pollux_navigate", ("location", "  "));

        Assert.IsFalse(result.GetProperty("ok").GetBoolean());
        Assert.IsNull(_actions.Navigated);
    }

    [TestMethod]
    public async Task NavigateFailureFromPolluxIsReportedToCopilot()
    {
        _actions.NavigateError = "The folder does not exist.";

        var result = await Call("pollux_navigate", ("location", @"C:\nope"));

        Assert.IsFalse(result.GetProperty("ok").GetBoolean());
        Assert.AreEqual("The folder does not exist.", result.GetProperty("error").GetString());
    }

    [TestMethod]
    public async Task SelectEntriesSelectsAllMatchingNamesOfThePane()
    {
        var result = await Call("pollux_select_entries", ("pane", "right"), ("patterns", new[] { "*.pdf" }));

        CollectionAssert.AreEqual(new[] { "x.PDF" }, _actions.Selected!.Value.Names);
        Assert.AreEqual("right", _actions.Selected.Value.Pane);
        Assert.IsTrue(result.GetProperty("ok").GetBoolean());
        Assert.AreEqual(1, result.GetProperty("selected").GetInt32());
    }

    [TestMethod]
    public async Task SelectEntriesCanBeLimitedToFiles()
    {
        await Call("pollux_select_entries", ("patterns", new[] { "*" }), ("kind", "file"));

        CollectionAssert.AreEqual(new[] { "a.pdf", "b.pdf", "c.txt" }, _actions.Selected!.Value.Names);
        Assert.AreEqual("active", _actions.Selected.Value.Pane);
    }

    [TestMethod]
    public async Task SelectEntriesWithoutAMatchSelectsNothingAndSaysSo()
    {
        var result = await Call("pollux_select_entries", ("patterns", new[] { "*.mp3" }));

        Assert.IsFalse(result.GetProperty("ok").GetBoolean());
        StringAssert.Contains(result.GetProperty("error").GetString(), "No entry");
        Assert.IsNull(_actions.Selected);
    }

    [TestMethod]
    public async Task SelectEntriesRejectsUnknownPane()
    {
        var result = await Call("pollux_select_entries", ("pane", "top"), ("patterns", new[] { "*" }));

        Assert.IsFalse(result.GetProperty("ok").GetBoolean());
        Assert.IsNull(_actions.Selected);
    }

    [TestMethod]
    public async Task OtherResolvesToTheNonActivePane()
    {
        await Call("pollux_select_entries", ("pane", "other"), ("patterns", new[] { "*.docx" }));

        CollectionAssert.AreEqual(new[] { "y.docx" }, _actions.Selected!.Value.Names);
        Assert.AreEqual("other", _actions.Selected.Value.Pane);
    }

    [TestMethod]
    public void EveryToolHasADescriptionAndTheSystemMessagePointsToThem()
    {
        foreach (var tool in PolluxTools.Create(_actions, readOnly: false))
            Assert.IsFalse(string.IsNullOrWhiteSpace(tool.Description), tool.Name);
        StringAssert.Contains(ChatSessionConfig.PolluxToolsHint, "pollux_read_panes");
        StringAssert.Contains(ChatSessionConfig.PolluxToolsHint, "instead of");
    }

    public TestContext TestContext { get; set; } = null!;
}
