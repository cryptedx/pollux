using Pollux.Core.Copilot.Guards;

namespace Pollux.Core.Tests.Copilot;

/// <summary>
/// SECURITY.md S-13 (common credential stores are protected) and S-06 (pollux_navigate is judged like a read: a network
/// or device location asks or is refused, a protected location is refused, and judging it never contacts the host).
/// </summary>
[TestClass]
public sealed class GuardRulesCredentialStoresAndNavigateTests
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

    private static GuardVerdict Evaluate(ToolRequest request, IFolderFiles? folders = null) =>
        new GuardRules(folders: folders).Evaluate(request, Context);

    private static void AssertDecision(GuardDecision expected, GuardVerdict verdict, string because)
    {
        Assert.AreEqual(expected, verdict.Decision, $"{because} (reason: {verdict.Reason})");
        if (expected != GuardDecision.Allow)
        {
            Assert.IsFalse(string.IsNullOrWhiteSpace(verdict.Reason), "non-allow verdicts carry a reason");
        }
    }

    // ---- S-13: credential stores ----

    [TestMethod]
    [DataRow(@"C:\Users\marko\AppData\Roaming\GitHub CLI\hosts.yml")]
    [DataRow(@"C:\Users\marko\.config\gh\hosts.yml")]
    [DataRow(@"C:\Users\marko\.git-credentials")]
    [DataRow(@"C:\repos\app\.git-credentials")]
    [DataRow(@"C:\Users\marko\.config\git\credentials")]
    [DataRow(@"C:\Users\marko\.npmrc")]
    [DataRow(@"C:\repos\app\.npmrc")]
    [DataRow(@"C:\Users\marko\.docker\config.json")]
    [DataRow(@"C:\Users\marko\.kube\config")]
    [DataRow(@"C:\Users\marko\AppData\Roaming\NuGet\NuGet.Config")]
    [DataRow(@"C:\Users\marko\AppData\Roaming\NuGet\nuget.config")]
    [DataRow(@"D:\vault\Passwords.kdbx")]
    [DataRow(@"C:\Users\marko\Documents\keys\id_rsa")]
    [DataRow(@"C:\repos\app\deploy\id_ed25519")]
    [DataRow(@"C:\Users\marko\backup\id_ecdsa")]
    [DataRow(@"C:\Users\marko\backup\id_ed25519_sk")]
    [DataRow(@"C:\Users\marko\Documents\id_rsa_work")]
    [DataRow(@"C:\repos\app\deploy\id_ed25519_github")]
    [DataRow(@"%APPDATA%\GitHub CLI\hosts.yml")]
    [DataRow(@"~\.kube\config")]
    public void CredentialStoresCannotBeReadOrWritten(string path)
    {
        AssertDecision(GuardDecision.Deny, Evaluate(ToolRequest.Read(path)), $"read {path}");
        AssertDecision(GuardDecision.Deny, Evaluate(ToolRequest.Write(path)), $"write {path}");
    }

    [TestMethod]
    [DataRow(@"type %APPDATA%\""GitHub CLI""\hosts.yml")]
    [DataRow(@"Get-Content ""$env:APPDATA\GitHub CLI\hosts.yml""")]
    [DataRow("cat ~/.git-credentials")]
    [DataRow(@"Get-Content $HOME\.npmrc")]
    [DataRow(@"type %USERPROFILE%\.docker\config.json")]
    [DataRow("cat ~/.kube/config")]
    [DataRow(@"Get-Content $env:APPDATA\NuGet\NuGet.Config")]
    [DataRow(@"copy D:\vault\Passwords.kdbx C:\repos\app\x")]
    [DataRow(@"type D:\backup\id_rsa")]
    public void CredentialStoresCannotBeReadViaShell(string command) =>
        AssertDecision(GuardDecision.Deny, Evaluate(ToolRequest.Shell(command)), command);

    [TestMethod]
    [DataRow(@"C:\repos\app\src\identity.cs")]
    [DataRow(@"C:\repos\app\src\id_generator.cs")]
    [DataRow(@"C:\repos\app\deploy\id_rsa_work.pub")]
    [DataRow(@"C:\repos\app\NuGet.Config")]
    [DataRow(@"C:\repos\app\docs\config.json")]
    [DataRow(@"C:\Users\marko\.ssh-notes.txt")]
    public void LookAlikesOutsideTheCredentialStoresStayReadable(string path) =>
        AssertDecision(GuardDecision.Allow, Evaluate(ToolRequest.Read(path)), path);

    [TestMethod]
    [DataRow(@"Get-Content src\*.cs")]
    [DataRow(@"dir C:\repos\app\src\*.json")]
    public void WildcardsThatCannotNameACredentialStoreStayAllowed(string command) =>
        AssertDecision(GuardDecision.Allow, Evaluate(ToolRequest.Shell(command)), command);

    // ---- S-06: pollux_navigate ----

    [TestMethod]
    public void NavigateIsAReadOfItsLocation()
    {
        var request = ToolRequest.Navigate(@"C:\docs");

        Assert.AreEqual(ToolKind.Read, request.Kind);
        CollectionAssert.AreEqual(new[] { @"C:\docs" }, request.Paths.ToArray());
        Assert.IsTrue(request.Navigates);
    }

    [TestMethod]
    [DataRow(@"C:\docs")]
    [DataRow(@"C:\repos\app\src")]
    [DataRow(@"D:\Projects")]
    [DataRow(@"\\localhost\C$\repos\app")]
    [DataRow(@"\\?\C:\repos\app")]
    public void NavigatingToALocalFolderIsAllowed(string location) =>
        AssertDecision(GuardDecision.Allow, Evaluate(ToolRequest.Navigate(location)), location);

    [TestMethod]
    [DataRow(@"\\attacker\x")]
    [DataRow(@"\\attacker.example.com\share\folder")]
    [DataRow(@"\\10.0.0.5\c$")]
    [DataRow(@"//attacker/x")]
    [DataRow(@"\\?\UNC\attacker\share")]
    [DataRow(@"\\.\UNC\attacker\share")]
    [DataRow(@"file://attacker/share")]
    public void NavigatingToANetworkLocationAsks(string location)
    {
        var verdict = Evaluate(ToolRequest.Navigate(location));

        AssertDecision(GuardDecision.Ask, verdict, location);
        StringAssert.Contains(verdict.Reason, GuardRules.NetworkLocation);
    }

    [TestMethod]
    [DataRow(@"\\?\GLOBALROOT\Device\HarddiskVolumeShadowCopy1\Users")]
    [DataRow(@"\\.\PhysicalDrive0")]
    [DataRow(@"\\.\pipe\x")]
    public void NavigatingToADeviceIsRefused(string location) =>
        AssertDecision(GuardDecision.Deny, Evaluate(ToolRequest.Navigate(location)), location);

    // Pollux opens a location from its own current folder, not the Working Folder the rules judge it against: only
    // absolute locations are judged the same way they are opened.
    [TestMethod]
    [DataRow("src")]
    [DataRow(".ssh")]
    [DataRow(@"..\..\Users\marko\.ssh")]
    [DataRow(@"~\.ssh")]
    [DataRow(@"%USERPROFILE%\Documents")]
    [DataRow("C:Users")]
    [DataRow(@"\Users\marko")]
    public void NavigatingToARelativeLocationIsRefused(string location)
    {
        var verdict = Evaluate(ToolRequest.Navigate(location));

        AssertDecision(GuardDecision.Deny, verdict, location);
        StringAssert.Contains(verdict.Reason, GuardRules.RelativeLocation);
    }

    [TestMethod]
    [DataRow(@"C:\Users\marko\.ssh")]
    [DataRow(@"~\.aws")]
    [DataRow(@"C:\Users\marko\AppData\Roaming\Microsoft\Credentials")]
    [DataRow(@"C:\Users\marko\AppData\Local\Google\Chrome\User Data\Default")]
    [DataRow(@"C:\Users\marko\AppData\Roaming\Pollux")]
    [DataRow(@"\\localhost\C$\Users\marko\.ssh")]
    public void NavigatingToAProtectedLocationIsRefused(string location) =>
        AssertDecision(GuardDecision.Deny, Evaluate(ToolRequest.Navigate(location)), location);

    [TestMethod]
    public void JudgingANetworkLocationDoesNotTouchIt()
    {
        var folders = new RecordingFolders();

        Evaluate(ToolRequest.Navigate(@"\\attacker\x"), folders);

        CollectionAssert.AreEqual(Array.Empty<string>(), folders.Touched, "resolving links on a UNC path would contact the host (NTLM)");
    }

    [TestMethod]
    public void NavigatingInATaintedChatToANetworkLocationStillAsks()
    {
        var tainted = Context with { Tainted = true };

        AssertDecision(GuardDecision.Ask, new GuardRules().Evaluate(ToolRequest.Navigate(@"\\attacker\x"), tainted), "tainted");
    }

    /// <summary>A file system that records every path the Guard Rules look at.</summary>
    private sealed class RecordingFolders : IFolderFiles
    {
        public List<string> Touched { get; } = [];

        public IEnumerable<FolderEntry> Below(string folder)
        {
            Touched.Add(folder);
            return [];
        }

        public string? Canonical(string path)
        {
            Touched.Add(path);
            return path;
        }
    }
}
