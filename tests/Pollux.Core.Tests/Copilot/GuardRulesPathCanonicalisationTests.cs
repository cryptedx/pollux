using Pollux.Core.Copilot.Guards;

namespace Pollux.Core.Tests.Copilot;

/// <summary>
/// SECURITY.md S-04 and S-17 (ADS): device-prefixed, admin-share and alternate-data-stream spellings of a path are judged
/// like the plain path, by the protected-location checks and by Taint, for shell commands and tool paths alike.
/// </summary>
[TestClass]
public sealed class GuardRulesPathCanonicalisationTests
{
    private const string WorkingFolder = @"C:\repos\app";

    private static readonly Dictionary<string, string> Env = new(StringComparer.OrdinalIgnoreCase)
    {
        ["USERPROFILE"] = @"C:\Users\marko",
        ["APPDATA"] = @"C:\Users\marko\AppData\Roaming",
        ["LOCALAPPDATA"] = @"C:\Users\marko\AppData\Local",
        ["TEMP"] = @"C:\Users\marko\AppData\Local\Temp",
        ["COMPUTERNAME"] = "MARKO-PC",
    };

    private static readonly GuardContext Context = new(WorkingFolder, false, name => Env.TryGetValue(name, out var v) ? v : null);

    private static GuardVerdict Evaluate(ToolRequest request) => new GuardRules().Evaluate(request, Context);

    private static void AssertDeny(GuardVerdict verdict, string because)
    {
        Assert.AreEqual(GuardDecision.Deny, verdict.Decision, $"{because} (reason: {verdict.Reason})");
        Assert.IsFalse(string.IsNullOrWhiteSpace(verdict.Reason), "non-allow verdicts carry a reason");
    }

    [TestMethod]
    [DataRow(@"\\?\C:\Users\marko\.ssh\id_rsa")]
    [DataRow(@"\\.\C:\Users\marko\.ssh\id_rsa")]
    [DataRow(@"\\?\c:\users\marko\.ssh\id_rsa")]
    [DataRow(@"//?/C:/Users/marko/.ssh/id_rsa")]
    [DataRow(@"\\localhost\C$\Users\marko\.ssh\id_rsa")]
    [DataRow(@"\\LOCALHOST\c$\Users\marko\.ssh\id_rsa")]
    [DataRow(@"\\127.0.0.1\C$\Users\marko\.ssh\id_rsa")]
    [DataRow(@"\\[::1]\C$\Users\marko\.ssh\id_rsa")]
    [DataRow(@"\\MARKO-PC\C$\Users\marko\.ssh\id_rsa")]
    [DataRow(@"\\marko-pc.corp.example\C$\Users\marko\.ssh\id_rsa")]
    [DataRow(@"\\?\UNC\localhost\C$\Users\marko\.ssh\id_rsa")]
    [DataRow(@"\\.\UNC\127.0.0.1\C$\Users\marko\.ssh\id_rsa")]
    [DataRow(@"\??\C:\Users\marko\.ssh\id_rsa")]
    [DataRow(@"\\?\UNC\MARKO-PC\C$\Users\marko\AppData\Roaming\Microsoft\Credentials\x")]
    [DataRow(@"\\?\C:\Users\marko\AppData\Local\Google\Chrome\User Data\Default\Login Data")]
    [DataRow(@"\\?\C:\repos\app\.env")]
    [DataRow(@"\\localhost\C$\repos\app\certs\server.pfx")]
    public void DevicePrefixedAndAdminSharePathsToAProtectedLocationAreBlocked(string path)
    {
        AssertDeny(Evaluate(ToolRequest.Read(path)), $"read {path}");
        AssertDeny(Evaluate(ToolRequest.Write(path)), $"write {path}");
        AssertDeny(Evaluate(ToolRequest.Shell($"Get-Content '{path}'")), $"shell {path}");
        AssertDeny(Evaluate(ToolRequest.Shell($"type \"{path}\"")), $"cmd {path}");
    }

    [TestMethod]
    [DataRow(@"file:///\\?\C:\Users\marko\.ssh\id_rsa")]
    [DataRow("file:////?/C:/Users/marko/.ssh/id_rsa")]
    [DataRow("file://localhost/C$/Users/marko/.ssh/id_rsa")]
    [DataRow("file://127.0.0.1/C$/Users/marko/.ssh/id_rsa")]
    public void FileUrisWithDeviceOrAdminSharePathsAreBlocked(string uri)
    {
        AssertDeny(Evaluate(ToolRequest.Read(uri)), uri);
        AssertDeny(Evaluate(ToolRequest.Shell($"Get-Content {uri}")), uri);
    }

    [TestMethod]
    [DataRow(@"C:\repos\app\secret.key::$DATA")]
    [DataRow(@"C:\repos\app\secret.key:hidden")]
    [DataRow(@"C:\repos\app\secret.key:hidden:$DATA")]
    [DataRow(@"C:\repos\app\cert.pfx::$DATA")]
    [DataRow(@"C:\repos\app\.env:stream")]
    [DataRow(@"secret.key::$DATA")]
    [DataRow(@"C:\repos\app\secret.key.")]
    [DataRow(@"C:\repos\app\secret.key. . ")]
    [DataRow(@"C:\Users\marko\.ssh::$INDEX_ALLOCATION\id_rsa")]
    [DataRow(@"\\?\C:\repos\app\secret.key::$DATA")]
    public void AlternateDataStreamSuffixesDoNotHideProtectedNames(string path)
    {
        AssertDeny(Evaluate(ToolRequest.Read(path)), $"read {path}");
        AssertDeny(Evaluate(ToolRequest.Shell($"Get-Content '{path}'")), $"shell {path}");
    }

    [TestMethod]
    [DataRow(@"\\?\GLOBALROOT\Device\HarddiskVolumeShadowCopy1\Users\marko\notes.txt")]
    [DataRow(@"\\.\GLOBALROOT\Device\HarddiskVolume3\repos\app\README.md")]
    [DataRow(@"\\.\PhysicalDrive0")]
    [DataRow(@"\\?\Volume{12345678-1234-1234-1234-123456789abc}\repos\app\README.md")]
    [DataRow(@"\\.\pipe\some-pipe")]
    [DataRow(@"\??\GLOBALROOT\Device\HarddiskVolume3\x")]
    public void DeviceNamespacesAreRefused(string path)
    {
        AssertDeny(Evaluate(ToolRequest.Read(path)), $"read {path}");
        AssertDeny(Evaluate(ToolRequest.Shell($"Get-Content '{path}'")), $"shell {path}");
        StringAssert.Contains(Evaluate(ToolRequest.Read(path)).Reason, GuardRules.DevicePath);
    }

    [TestMethod]
    [DataRow(@"\\?\C:\Users\marko")]
    [DataRow(@"\\localhost\C$\Users")]
    [DataRow(@"\\?\UNC\127.0.0.1\C$\Users\marko\AppData")]
    public void RecursiveSearchesOverAProtectedLocationThroughADevicePrefixAreBlocked(string folder)
    {
        AssertDeny(Evaluate(ToolRequest.Read(folder) with { Recursive = true }), $"tool {folder}");
        AssertDeny(Evaluate(ToolRequest.Shell($"Get-ChildItem -Recurse '{folder}'")), $"shell {folder}");
    }

    [TestMethod]
    [DataRow(@"\\?\C:\repos\app\README.md")]
    [DataRow(@"\\localhost\C$\repos\app\src\Program.cs")]
    [DataRow(@"C:\repos\app\notes.txt:Zone.Identifier")]
    public void CanonicalisedOrdinaryPathsStayAllowed(string path) =>
        Assert.AreEqual(GuardDecision.Allow, Evaluate(ToolRequest.Read(path)).Decision, path);

    [TestMethod]
    [DataRow(@"\\?\C:\repos\app\out.txt")]
    [DataRow(@"\\localhost\C$\repos\app\out.txt")]
    public void CanonicalisedWritesInsideTheWorkingFolderStayAllowed(string path) =>
        Assert.AreEqual(GuardDecision.Allow, Evaluate(ToolRequest.Write(path)).Decision, path);

    [TestMethod]
    [DataRow(@"\\?\C:\Windows\System32\drivers\etc\hosts")]
    [DataRow(@"\\localhost\D$\other\x.txt")]
    public void CanonicalisedWritesOutsideTheWorkingFolderAsk(string path) =>
        Assert.AreEqual(GuardDecision.Ask, Evaluate(ToolRequest.Write(path)).Decision, path);

    [TestMethod]
    [DataRow(@"\\?\C:\Users\marko\Downloads\invoice.txt")]
    [DataRow(@"\\.\C:\Users\marko\Downloads\invoice.txt")]
    [DataRow(@"\\localhost\C$\Users\marko\Downloads\invoice.txt")]
    [DataRow(@"\\?\UNC\127.0.0.1\C$\Users\marko\Downloads\invoice.txt")]
    [DataRow(@"\\MARKO-PC\C$\Users\marko\Desktop\note.txt")]
    [DataRow(@"C:\repos\app\vendor\lib.zip::$DATA")]
    [DataRow("file://localhost/C$/Users/marko/Downloads/invoice.txt")]
    public void DevicePrefixedAndAdminSharePathsToAnUntrustedPlaceTaint(string path)
    {
        Assert.IsNotNull(new GuardRules().Taint(ToolRequest.Read(path), Context), $"read {path}");
        Assert.IsNotNull(new GuardRules().Taint(ToolRequest.Shell($"Get-Content '{path}'"), Context), $"shell {path}");
    }
}
