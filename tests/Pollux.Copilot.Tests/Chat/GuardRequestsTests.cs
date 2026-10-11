using System.Text.Json;
using GitHub.Copilot;
using Pollux.Copilot.Chat;
using Pollux.Core.Copilot.Guards;

namespace Pollux.Copilot.Tests.Chat;

/// <summary>How a CLI tool call (pre-tool-use hook) or permission request is seen by the Guard Rules.</summary>
[TestClass]
public sealed class GuardRequestsTests
{
    private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static string Show(ToolRequest r) =>
        $"{r.Kind}|{r.Command}|{string.Join(",", r.Paths)}|{string.Join(",", r.Urls)}";

    [TestMethod]
    [DataRow("powershell")]
    [DataRow("bash")]
    public void ShellToolCarriesTheFullCommandText(string tool)
    {
        var request = GuardRequests.FromHook(tool, Args("""{"command":"format C: /q","description":"x"}"""));

        Assert.AreEqual("Shell|format C: /q||", Show(request));
    }

    [TestMethod]
    public void ViewToolIsARead()
    {
        Assert.AreEqual(@"Read||C:\Users\me\.ssh\id_rsa|", Show(GuardRequests.FromHook("view", Args("""{"path":"C:\\Users\\me\\.ssh\\id_rsa"}"""))));
    }

    [TestMethod]
    [DataRow("edit")]
    [DataRow("create")]
    public void FileChangingToolsAreWrites(string tool)
    {
        Assert.AreEqual(@"Write||C:\work\a.txt|", Show(GuardRequests.FromHook(tool, Args("""{"path":"C:\\work\\a.txt","file_text":"hi"}"""))));
    }

    [TestMethod]
    public void ApplyPatchWritesEveryFileItNames()
    {
        const string patch = """
            "*** Begin Patch\n*** Update File: C:\\work\\a.txt\n@@\n-x\n+y\n*** Add File: C:\\out\\b.txt\n+z\n*** Delete File: c.txt\n*** End Patch"
            """;

        var request = GuardRequests.FromHook("apply_patch", Args(patch));

        Assert.AreEqual(@"Write||C:\work\a.txt,C:\out\b.txt,c.txt|", Show(request));
    }

    [TestMethod]
    [DataRow("""{"location":"C:\\docs","pane":"left","newTab":true}""", @"C:\docs")]
    [DataRow("""{"location":"\\\\attacker\\x"}""", @"\\attacker\x")]
    [DataRow("""{"location":"\\\\?\\GLOBALROOT\\Device\\x"}""", @"\\?\GLOBALROOT\Device\x")]
    public void NavigateIsAReadOfItsLocation(string args, string location)
    {
        var request = GuardRequests.FromHook("pollux_navigate", Args(args));

        Assert.AreEqual($"Read||{location}|", Show(request));
        Assert.IsTrue(request.Navigates);
    }

    [TestMethod]
    [DataRow("""{"location":"home"}""")]
    [DataRow("""{"location":"HOME"}""")]
    [DataRow("""{"location":"  "}""")]
    [DataRow("""{"pane":"left"}""")]
    public void NavigateHomeOrNowhereIsAReadOfNothing(string args)
    {
        var request = GuardRequests.FromHook("pollux_navigate", Args(args));

        Assert.AreEqual("Read|||", Show(request));
        Assert.IsTrue(request.Navigates);
    }

    [TestMethod]
    public void NavigateAsAPermissionRequestOfACustomToolIsAReadToo()
    {
        var request = GuardRequests.FromPermission(new PermissionRequestCustomTool
        {
            ToolName = "pollux_navigate", ToolDescription = "", Args = Args("""{"location":"\\\\attacker\\x"}"""),
        });

        Assert.AreEqual(@"Read||\\attacker\x|", Show(request));
    }

    [TestMethod]
    public void WebFetchIsUrlAccess()
    {
        Assert.AreEqual("Url|||https://example.com/x", Show(GuardRequests.FromHook("web_fetch", Args("""{"url":"https://example.com/x"}"""))));
    }

    [TestMethod]
    public void UnknownToolsWithPathsAreWritesWithTheirPathsAndUrls()
    {
        // Fail closed: an unknown, custom or MCP tool may change what it names, so it is judged like a write.
        var request = GuardRequests.FromHook("some_tool", Args("""{"file":"C:\\x\\.env","nested":{"target":"https://a.b"},"count":3}"""));

        Assert.AreEqual(@"Write||C:\x\.env|https://a.b", Show(request));
    }

    [TestMethod]
    public void AnMcpPermissionRequestWithAPathIsAWrite()
    {
        var request = GuardRequests.FromPermission(new PermissionRequestMcp
        {
            ServerName = "fs", ToolName = "save", ToolTitle = "Save", ReadOnly = false, Args = Args("""{"path":"C:\\Windows\\x.ini"}"""),
        });

        Assert.AreEqual(@"Write||C:\Windows\x.ini|", Show(request));
    }

    [TestMethod]
    [DataRow("grep")]
    [DataRow("glob")]
    public void SearchToolsAreRecursiveReadsOfTheirFolder(string tool)
    {
        var request = GuardRequests.FromHook(tool, Args("""{"pattern":"BEGIN","path":"C:\\Users\\me"}"""));

        Assert.AreEqual(@"Read||C:\Users\me|", Show(request));
        Assert.IsTrue(request.Recursive);
    }

    [TestMethod]
    [DataRow("grep", """{"pattern":"BEGIN","glob":"*.key"}""")]
    [DataRow("glob", """{"pattern":"**/*.pfx"}""")]
    [DataRow("rg", null)]
    public void SearchToolsWithoutAFolderSearchTheWorkingFolder(string tool, string? args)
    {
        var request = GuardRequests.FromHook(tool, args is null ? null : Args(args));

        Assert.AreEqual("Read||.|", Show(request));
        Assert.IsTrue(request.Recursive);
    }

    [TestMethod]
    public void ViewIsNotRecursive() =>
        Assert.IsFalse(GuardRequests.FromHook("view", Args("""{"path":"C:\\Users\\me"}""")).Recursive);

    [TestMethod]
    public void ToolWithoutArgumentsIsOther()
    {
        Assert.AreEqual("Other|||", Show(GuardRequests.FromHook("some_new_tool", null)));
    }

    [TestMethod]
    public void SteeringToolsAreAPlainReadOfNothing() =>
        Assert.AreEqual("Read|||", Show(GuardRequests.FromHook("report_intent", null)));

    // Leaving plan mode turns planning into acting: a Tainted Chat confirms it like any other non-read tool.
    [TestMethod]
    public void ExitPlanModeIsNotASteeringTool() =>
        Assert.AreEqual("Other|||", Show(GuardRequests.FromHook("exit_plan_mode", null)));

    [TestMethod]
    public void ShellPermissionRequestCarriesCommandAndPossiblePathsAndUrls()
    {
        var request = GuardRequests.FromPermission(new PermissionRequestShell
        {
            CanOfferSessionApproval = false,
            Commands = [new PermissionRequestShellCommand { Identifier = "type", ReadOnly = true }],
            FullCommandText = "type secrets.txt",
            HasWriteFileRedirection = false,
            Intention = "",
            PossiblePaths = ["secrets.txt"],
            PossibleUrls = [new PermissionRequestShellPossibleUrl { Url = "https://x.y" }],
        });

        Assert.AreEqual("Shell|type secrets.txt|secrets.txt|https://x.y", Show(request));
    }

    [TestMethod]
    public void ReadWriteAndUrlPermissionRequests()
    {
        Assert.AreEqual(@"Read||C:\a|", Show(GuardRequests.FromPermission(new PermissionRequestRead { Intention = "", Path = @"C:\a" })));
        Assert.AreEqual(@"Write||C:\b|", Show(GuardRequests.FromPermission(new PermissionRequestWrite { CanOfferSessionApproval = false, Diff = "", FileName = @"C:\b", Intention = "" })));
        Assert.AreEqual("Url|||https://c.d", Show(GuardRequests.FromPermission(new PermissionRequestUrl { Intention = "", Url = "https://c.d" })));
    }
}
