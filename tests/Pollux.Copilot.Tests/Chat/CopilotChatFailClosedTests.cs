using System.Collections.Concurrent;
using System.Text.Json;
using Pollux.Copilot.Chat;
using Pollux.Copilot.Tests.Tools;
using Pollux.Core.Settings;

namespace Pollux.Copilot.Tests.Chat;

/// <summary>
/// S-17 end to end: a Chat over the fake CLI (real SDK, stdio) whose CLI cannot switch the sandbox or cannot tell the
/// organization's policy. In Allow All and Autopilot no prompt may reach the CLI; Manual and Assisted are unchanged.
/// </summary>
[TestClass]
public sealed class CopilotChatFailClosedTests : IAsyncDisposable
{
    private string _dir = null!;
    private CopilotCliConnection _connection = null!;
    private readonly ConcurrentQueue<string> _posted = new();

    [TestInitialize]
    public async Task StartFakeCli()
    {
        _dir = Path.Combine(Path.GetTempPath(), "pollux-failclosed-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _connection = new CopilotCliConnection(new CopilotCli.Configured(FakeCopilotCli.Write(_dir)));
        await _connection.StartAsync(TestContext.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _connection.DisposeAsync();
        Directory.Delete(_dir, recursive: true);
    }

    /// <summary>A recorded Chat whose session id carries the fake CLI's failure marker, opened in the given modes.</summary>
    private async Task<CopilotChat> OpenChat(string id, CopilotAgentMode agent, CopilotPermissionMode permission)
    {
        var store = new ChatStore(Path.Combine(_dir, "chats"));
        var now = DateTimeOffset.UtcNow;
        store.Save(new ChatRecord { Id = id, Title = "t", Created = now, Modified = now, AgentMode = agent, PermissionMode = permission });
        var chat = new CopilotChat(store,
            (record, existing, approvals, ct) => _connection.OpenChatAsync(record, existing, approvals, new NoPolluxActions(), ct),
            (_, _) => Task.CompletedTask, _posted.Enqueue);
        Assert.IsTrue(await chat.OpenAsync(id, TestContext.CancellationToken));
        return chat;
    }

    private int PromptsSent() => FakeCopilotCli.Requests(_dir).Count(r => r.GetProperty("method").GetString() == "session.send");

    private string[] Errors() => [.. _posted.Select(m =>
    {
        using var doc = JsonDocument.Parse(m);
        var r = doc.RootElement;
        return r.GetProperty("type").GetString() == "chatError" ? r.GetProperty("message").GetString()! : "";
    }).Where(m => m.Length > 0)];

    [TestMethod]
    [DataRow(CopilotAgentMode.Interactive, CopilotPermissionMode.AllowAll)]
    [DataRow(CopilotAgentMode.Autopilot, CopilotPermissionMode.Manual)]
    public async Task SandboxTheCliRefusesBlocksPrompts(CopilotAgentMode agent, CopilotPermissionMode permission)
    {
        await using var chat = await OpenChat("pollux-sandboxrefused", agent, permission);

        Assert.IsFalse(await chat.SendAsync("rm -rf everything", TestContext.CancellationToken));

        Assert.AreEqual(0, PromptsSent());
        CollectionAssert.Contains(Errors(), CopilotChat.SandboxUnconfirmed);
    }

    [TestMethod]
    [DataRow(CopilotAgentMode.Interactive, CopilotPermissionMode.AllowAll)]
    [DataRow(CopilotAgentMode.Autopilot, CopilotPermissionMode.Manual)]
    public async Task UnreadableManagedSettingsBlockPrompts(CopilotAgentMode agent, CopilotPermissionMode permission)
    {
        await using var chat = await OpenChat("pollux-unreadablesettings", agent, permission);

        Assert.IsFalse(await chat.SendAsync("rm -rf everything", TestContext.CancellationToken));

        Assert.AreEqual(0, PromptsSent());
        CollectionAssert.Contains(Errors(), CopilotChat.ModePolicyUnconfirmed);
    }

    [TestMethod]
    [DataRow("pollux-sandboxrefused", CopilotPermissionMode.Manual)]
    [DataRow("pollux-sandboxrefused", CopilotPermissionMode.Assisted)]
    [DataRow("pollux-unreadablesettings", CopilotPermissionMode.Manual)]
    [DataRow("pollux-unreadablesettings", CopilotPermissionMode.Assisted)]
    public async Task ManualAndAssistedStillSend(string id, CopilotPermissionMode permission)
    {
        await using var chat = await OpenChat(id, CopilotAgentMode.Interactive, permission);
        await chat.SetSandboxAsync(true, TestContext.CancellationToken); // refused by "sandboxrefused"; not a reason to block here

        Assert.IsTrue(await chat.SendAsync("hello", TestContext.CancellationToken));

        Assert.AreEqual(1, PromptsSent());
        CollectionAssert.DoesNotContain(Errors(), CopilotChat.SandboxUnconfirmed);
        CollectionAssert.DoesNotContain(Errors(), CopilotChat.ModePolicyUnconfirmed);
    }

    public TestContext TestContext { get; set; } = null!;
}
