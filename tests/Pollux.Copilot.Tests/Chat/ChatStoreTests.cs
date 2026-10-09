using Pollux.Copilot.Chat;
using Pollux.Core.Settings;

namespace Pollux.Copilot.Tests.Chat;

/// <summary>The Chat list on disk: global, survives a Pollux restart (a new store on the same folder).</summary>
[TestClass]
public sealed class ChatStoreTests
{
    private string _dir = "";

    [TestInitialize]
    public void Init() => _dir = Path.Combine(Path.GetTempPath(), "pollux-chats-" + Guid.NewGuid().ToString("N"));

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    private static ChatRecord Record(string id, string title, int minute, string? model = null, string? folder = null) => new()
    {
        Id = id,
        Title = title,
        Created = new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero),
        Modified = new DateTimeOffset(2026, 10, 9, 12, minute, 0, TimeSpan.Zero),
        Model = model,
        WorkingFolder = folder,
    };

    [TestMethod]
    public void EmptyStoreHasNoChats() => Assert.IsEmpty(new ChatStore(_dir).List());

    [TestMethod]
    public void ChatsSurviveARestartWithModelAndWorkingFolder()
    {
        new ChatStore(_dir).Save(Record("pollux-1", "Sort my downloads", 5, "gpt-5", @"C:\Users\marko\Downloads"));

        var chat = new ChatStore(_dir).Get("pollux-1");

        Assert.IsNotNull(chat);
        Assert.AreEqual("Sort my downloads", chat.Title);
        Assert.AreEqual("gpt-5", chat.Model);
        Assert.AreEqual(@"C:\Users\marko\Downloads", chat.WorkingFolder);
        Assert.AreEqual(new DateTimeOffset(2026, 10, 9, 12, 5, 0, TimeSpan.Zero), chat.Modified);
    }

    [TestMethod]
    public void ChatsSurviveARestartWithTheirAgentModeAndPermissionMode()
    {
        var record = Record("pollux-1", "Plan it", 5);
        record.AgentMode = CopilotAgentMode.Autopilot;
        record.PermissionMode = CopilotPermissionMode.AllowAll;
        new ChatStore(_dir).Save(record);

        var chat = new ChatStore(_dir).Get("pollux-1")!;

        Assert.AreEqual(CopilotAgentMode.Autopilot, chat.AgentMode);
        Assert.AreEqual(CopilotPermissionMode.AllowAll, chat.PermissionMode);
        StringAssert.Contains(File.ReadAllText(Path.Combine(_dir, "chats.json")), "\"AllowAll\"", "Stored by name, not by number.");
    }

    [TestMethod]
    public void ChatsSavedBeforeModesExistedAreInteractiveAndManual()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "chats.json"), """{ "chats": [ { "id": "pollux-old", "title": "old" } ] }""");

        var chat = new ChatStore(_dir).Get("pollux-old")!;

        Assert.AreEqual(CopilotAgentMode.Interactive, chat.AgentMode);
        Assert.AreEqual(CopilotPermissionMode.Manual, chat.PermissionMode);
    }

    [TestMethod]
    public void ListIsNewestFirstAndSavingAgainReplaces()
    {
        var store = new ChatStore(_dir);
        store.Save(Record("a", "old", 1));
        store.Save(Record("b", "newer", 9));
        store.Save(Record("a", "old, renamed", 3));

        CollectionAssert.AreEqual(new[] { "b", "a" }, store.List().Select(c => c.Id).ToArray());
        Assert.AreEqual("old, renamed", store.Get("a")!.Title);
    }

    [TestMethod]
    public void DeletedChatIsGoneAfterARestart()
    {
        var store = new ChatStore(_dir);
        store.Save(Record("a", "one", 1));
        store.Save(Record("b", "two", 2));

        store.Delete("a");

        CollectionAssert.AreEqual(new[] { "b" }, new ChatStore(_dir).List().Select(c => c.Id).ToArray());
        Assert.IsNull(new ChatStore(_dir).Get("a"));
    }

    [TestMethod]
    public void BrokenFileMeansNoChats()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "chats.json"), "{ not json");

        Assert.IsEmpty(new ChatStore(_dir).List());
    }
}
