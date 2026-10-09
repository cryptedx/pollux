using Pollux.Copilot.Chat;

namespace Pollux.Copilot.Tests.Chat;

/// <summary>The Action Log of a Chat: every tool call, stored locally next to the Chat, surviving a restart.</summary>
[TestClass]
public sealed class ActionLogTests
{
    private string _dir = "";

    [TestInitialize]
    public void Init() => _dir = Path.Combine(Path.GetTempPath(), "pollux-actionlog-" + Guid.NewGuid().ToString("N"));

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static ActionLogEntry Entry(string tool, string? command = null, ActionDecision decision = ActionDecision.Allowed, ActionDecider by = ActionDecider.Mode) => new()
    {
        Tool = tool, Command = command, Decision = decision, DecidedBy = by, Reason = "r",
        Paths = [@"C:\work\a.txt"], Urls = ["https://example.com"],
    };

    [TestMethod]
    public void AnEntryIsStoredWithAllItsFieldsAndSurvivesReopening()
    {
        var store = new ActionLogStore(_dir);
        store.Open("chat1").Add(Entry("powershell", "dir", ActionDecision.Blocked, ActionDecider.GuardRule));

        var entry = new ActionLogStore(_dir).Open("chat1").Entries.Single();

        Assert.AreEqual("powershell", entry.Tool);
        Assert.AreEqual("dir", entry.Command);
        Assert.AreEqual(ActionDecision.Blocked, entry.Decision);
        Assert.AreEqual(ActionDecider.GuardRule, entry.DecidedBy);
        Assert.AreEqual("r", entry.Reason);
        CollectionAssert.AreEqual(new[] { @"C:\work\a.txt" }, entry.Paths.ToArray());
        CollectionAssert.AreEqual(new[] { "https://example.com" }, entry.Urls.ToArray());
    }

    [TestMethod]
    public void EntriesKeepTheirOrderAndGetIncreasingIdsAcrossReopening()
    {
        var log = new ActionLogStore(_dir).Open("c");
        log.Add(Entry("a"));
        log.Add(Entry("b"));
        var again = new ActionLogStore(_dir).Open("c");
        again.Add(Entry("c"));

        CollectionAssert.AreEqual(new[] { "a", "b", "c" }, again.Entries.Select(e => e.Tool).ToArray());
        CollectionAssert.AreEqual(new[] { 1, 2, 3 }, again.Entries.Select(e => e.Id).ToArray());
    }

    [TestMethod]
    public void UpdateChangesAnEntryAndIsStored()
    {
        var log = new ActionLogStore(_dir).Open("c");
        var id = log.Add(Entry("edit")).Id;

        log.Update(id, e => { e.Decision = ActionDecision.Asked; e.DecidedBy = ActionDecider.User; });

        var stored = new ActionLogStore(_dir).Open("c").Entries.Single();
        Assert.AreEqual(ActionDecision.Asked, stored.Decision);
        Assert.AreEqual(ActionDecider.User, stored.DecidedBy);
    }

    [TestMethod]
    public void ChatsHaveSeparateLogsAndDeleteRemovesOne()
    {
        var store = new ActionLogStore(_dir);
        store.Open("a").Add(Entry("x"));
        store.Open("b").Add(Entry("y"));

        store.Delete("a");

        Assert.IsEmpty(new ActionLogStore(_dir).Open("a").Entries);
        Assert.HasCount(1, new ActionLogStore(_dir).Open("b").Entries);
    }

    [TestMethod]
    public void BrokenLinesAreSkippedAndAChatIdCannotEscapeTheDirectory()
    {
        var store = new ActionLogStore(_dir);
        store.Open("c").Add(Entry("ok"));
        File.AppendAllText(Directory.GetFiles(_dir, "*", SearchOption.AllDirectories).Single(), "{ not json\n");

        Assert.AreEqual("ok", new ActionLogStore(_dir).Open("c").Entries.Single().Tool);

        store.Open(@"..\..\evil").Add(Entry("z"));
        Assert.IsTrue(Directory.GetFiles(_dir, "*", SearchOption.AllDirectories).All(f => f.StartsWith(_dir, StringComparison.Ordinal)));
    }
}
