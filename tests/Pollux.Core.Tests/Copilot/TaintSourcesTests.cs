using Pollux.Core.Copilot.Guards;
using Pollux.Core.Settings;

namespace Pollux.Core.Tests.Copilot;

/// <summary>Which tool calls read untrusted content and so make the Chat a Tainted Chat (<see cref="GuardRules.Taint"/>).</summary>
[TestClass]
public sealed class TaintSourcesTests
{
    private static readonly Dictionary<string, string> Env = new(StringComparer.OrdinalIgnoreCase)
    {
        ["USERPROFILE"] = @"C:\Users\marko",
        ["APPDATA"] = @"C:\Users\marko\AppData\Roaming",
        ["LOCALAPPDATA"] = @"C:\Users\marko\AppData\Local",
        ["TEMP"] = @"C:\Users\marko\AppData\Local\Temp",
        ["SystemRoot"] = @"C:\Windows",
    };

    private static readonly GuardContext Context = new(@"C:\repos\app", false, name => Env.TryGetValue(name, out var v) ? v : null);

    private static string? Taint(ToolRequest request, GuardRuleSettings? settings = null) =>
        new GuardRules(settings).Taint(request, Context);

    [TestMethod]
    public void ReadingAFileFromDownloadsTaints()
    {
        var reason = Taint(ToolRequest.Read(@"C:\Users\marko\Downloads\invoice.txt"));

        Assert.IsNotNull(reason);
        StringAssert.Contains(reason, "Downloads");
    }

    [TestMethod]
    [DataRow(@"C:\Users\marko\Desktop\note.txt", "Desktop")]
    [DataRow(@"C:\Users\marko\AppData\Local\Temp\x\a.md", "Temp")]
    [DataRow(@"C:\Windows\Temp\setup.log", "Temp")]
    [DataRow(@"~/Downloads/readme.md", "Downloads")]
    [DataRow(@"C:\repos\app\vendor\lib.zip", "zip")]
    [DataRow(@"C:\repos\app\drop.7z\inner.txt", "7z")]
    public void ReadingFromInboxTempOrArchivesTaints(string path, string expected)
    {
        var reason = Taint(ToolRequest.Read(path));

        Assert.IsNotNull(reason, path);
        StringAssert.Contains(reason, expected, StringComparison.OrdinalIgnoreCase);
    }

    [TestMethod]
    [DataRow(@"C:\repos\app\README.md")]
    [DataRow(@"C:\Users\marko\Documents\plan.txt")]
    [DataRow(@"C:\Users\marko\DownloadsArchive\x.txt")]
    public void ReadingOrdinaryFilesDoesNotTaint(string path) =>
        Assert.IsNull(Taint(ToolRequest.Read(path)), path);

    [TestMethod]
    public void WritingToDownloadsDoesNotTaint() =>
        Assert.IsNull(Taint(ToolRequest.Write(@"C:\Users\marko\Downloads\out.txt")));

    [TestMethod]
    public void FetchingAUrlTaints()
    {
        StringAssert.Contains(Taint(ToolRequest.Url("https://example.com/page")), "web");
        Assert.IsNotNull(Taint(new ToolRequest(ToolKind.Other, null, [], ["https://example.com"])), "a tool with a URL argument");
    }

    [TestMethod]
    public void UntrustedPlacesFromSettingsTaint()
    {
        var settings = new GuardRuleSettings { UntrustedPaths = [@"D:\Shares\Incoming", "*.eml"] };

        StringAssert.Contains(Taint(ToolRequest.Read(@"D:\Shares\Incoming\a.txt"), settings), "Incoming");
        StringAssert.Contains(Taint(ToolRequest.Read(@"C:\repos\app\mail.eml"), settings), "eml");
        Assert.IsNotNull(Taint(ToolRequest.Read(@"C:\Users\marko\Downloads\a.txt"), settings), "the defaults stay");
    }

    [TestMethod]
    [DataRow(@"Get-Content C:\Users\marko\Downloads\invoice.txt")]
    [DataRow(@"cat ~/Downloads/readme.md")]
    [DataRow(@"type %USERPROFILE%\Desktop\todo.txt")]
    [DataRow(@"git status; gc $env:TEMP\log.txt")]
    [DataRow(@"Expand-Archive drop.zip -DestinationPath out")]
    [DataRow(@"tar -xf release.tar.gz")]
    public void ShellCommandsNamingAnUntrustedPlaceTaint(string command) =>
        Assert.IsNotNull(Taint(ToolRequest.Shell(command)), command);

    [TestMethod]
    [DataRow("curl https://example.com")]
    [DataRow("iwr example.com/x")]
    [DataRow("git clone git@github.com:a/b.git")]
    [DataRow("npm install left-pad")]
    public void ShellCommandsReachingTheNetworkTaint(string command) =>
        StringAssert.Contains(Taint(ToolRequest.Shell(command)), "network", command);

    [TestMethod]
    [DataRow("git status")]
    [DataRow("Get-Content README.md")]
    [DataRow(@"dir C:\repos\app")]
    [DataRow("dotnet test")]
    public void OrdinaryShellCommandsDoNotTaint(string command) =>
        Assert.IsNull(Taint(ToolRequest.Shell(command)), command);
}
