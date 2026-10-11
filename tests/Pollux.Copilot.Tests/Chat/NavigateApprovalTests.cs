using System.Text.Json;
using GitHub.Copilot;
using Pollux.Copilot.Chat;

namespace Pollux.Copilot.Tests.Chat;

/// <summary>
/// SECURITY.md S-06: pollux_navigate runs without a CLI permission request, so the pre-tool-use hook is where the Guard
/// Rules judge it, like a read of its location: a network share asks before Pollux contacts the host (NTLM), a device
/// or protected location is refused, a local folder opens without a question.
/// </summary>
[TestClass]
public sealed class NavigateApprovalTests
{
    private readonly List<ToolApprovalRequest> _asked = [];
    private string _dir = "";
    private ActionLog _log = null!;

    [TestInitialize]
    public void Init()
    {
        _dir = Path.Combine(Path.GetTempPath(), "pollux-navigate-" + Guid.NewGuid().ToString("N"));
        _log = new ActionLogStore(_dir).Open("chat");
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private ToolApprovals Approvals() =>
        new(_asked.Add, guard: new ChatGuard(null, () => @"C:\work", environment: Environment), log: _log);

    private static string? Environment(string name) => name switch
    {
        "USERPROFILE" => @"C:\Users\me",
        "APPDATA" => @"C:\Users\me\AppData\Roaming",
        "LOCALAPPDATA" => @"C:\Users\me\AppData\Local",
        _ => null,
    };

    private static PreToolUseHookInput Navigate(string location) => new()
    {
        ToolName = "pollux_navigate",
        ToolArgs = JsonDocument.Parse($$"""{"location":{{JsonSerializer.Serialize(location, ChatGuardTestJson.Default.String)}},"pane":"left"}""").RootElement.Clone(),
        WorkingDirectory = @"C:\work",
    };

    [TestMethod]
    [DataRow(@"\\attacker\x")]
    [DataRow(@"\\?\UNC\attacker\share\folder")]
    public async Task NavigatingToANetworkShareAsksFirst(string location)
    {
        var approvals = Approvals();
        var pending = approvals.PreToolUseAsync(Navigate(location));

        var prompt = _asked.Single();
        Assert.AreEqual("Open a folder in Pollux", prompt.Title);
        StringAssert.Contains(prompt.Reason, "network location");
        Assert.IsFalse(prompt.CanAllowForChat);

        approvals.Answer(prompt.Id, approved: false);
        Assert.AreEqual("deny", (await pending)?.PermissionDecision);
    }

    [TestMethod]
    [DataRow(@"C:\Users\me\.ssh")]
    [DataRow(@"\\?\GLOBALROOT\Device\HarddiskVolumeShadowCopy1\Users\me")]
    public async Task NavigatingToAProtectedOrDeviceLocationIsRefused(string location)
    {
        var output = await Approvals().PreToolUseAsync(Navigate(location));

        Assert.AreEqual("deny", output?.PermissionDecision, location);
        StringAssert.Contains(output!.PermissionDecisionReason, "Guard Rules");
        Assert.IsEmpty(_asked);
        Assert.AreEqual(ActionDecision.Blocked, _log.Entries.Single().Decision);
    }

    [TestMethod]
    [DataRow(@"C:\work\docs")]
    [DataRow("home")]
    public async Task NavigatingToALocalFolderIsLeftToThePermissionMode(string location)
    {
        Assert.IsNull(await Approvals().PreToolUseAsync(Navigate(location)), location);
        Assert.IsEmpty(_asked);
    }
}
