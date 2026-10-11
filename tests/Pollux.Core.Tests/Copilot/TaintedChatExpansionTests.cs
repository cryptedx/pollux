using Pollux.Core.Copilot.Guards;

namespace Pollux.Core.Tests.Copilot;

/// <summary>
/// SECURITY.md S-12: the Tainted Chat covers network shares, mapped network drives and cloud-synced folders, resolves
/// links before deciding, and asks before every action that is not a plain read.
/// </summary>
[TestClass]
public sealed class TaintedChatExpansionTests
{
    private static readonly Dictionary<string, string> Env = new(StringComparer.OrdinalIgnoreCase)
    {
        ["USERPROFILE"] = @"C:\Users\marko",
        ["APPDATA"] = @"C:\Users\marko\AppData\Roaming",
        ["LOCALAPPDATA"] = @"C:\Users\marko\AppData\Local",
        ["TEMP"] = @"C:\Users\marko\AppData\Local\Temp",
        ["SystemRoot"] = @"C:\Windows",
        ["OneDrive"] = @"C:\Users\marko\OneDrive - Contoso",
        ["OneDriveCommercial"] = @"C:\Users\marko\OneDrive - Contoso",
        ["OneDriveConsumer"] = @"C:\Users\marko\OneDrive",
    };

    private static GuardContext Context(bool tainted = false) => new(@"C:\repos\app", tainted, name => Env.TryGetValue(name, out var v) ? v : null);

    private static string? Taint(ToolRequest request) => new GuardRules().Taint(request, Context());

    [TestMethod]
    [DataRow(@"\\fileserver\team\handover.md")]
    [DataRow(@"\\?\UNC\fileserver\team\handover.md")]
    [DataRow(@"file://fileserver/team/handover.md")]
    public void ReadingFromANetworkShareTaints(string path)
    {
        var reason = Taint(ToolRequest.Read(path));

        Assert.IsNotNull(reason, path);
        StringAssert.Contains(reason, "network", StringComparison.OrdinalIgnoreCase);
    }

    [TestMethod]
    [DataRow(@"Get-Content \\fileserver\team\handover.md")]
    [DataRow(@"type \\fileserver\team\handover.md")]
    public void AShellCommandNamingANetworkShareTaints(string command) =>
        Assert.IsNotNull(Taint(ToolRequest.Shell(command)), command);

    [TestMethod]
    [DataRow(@"C:\Users\marko\OneDrive - Contoso\Shared with me\spec.docx")]
    [DataRow(@"C:\Users\marko\OneDrive\notes.txt")]
    [DataRow(@"%OneDrive%\notes.txt")]
    public void ReadingFromAOneDriveFolderTaints(string path)
    {
        var reason = Taint(ToolRequest.Read(path));

        Assert.IsNotNull(reason, path);
        StringAssert.Contains(reason, "OneDrive", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A file system where some folders are junctions (link -> target) and some paths cannot be resolved.</summary>
    private sealed class LinkedFolders(Dictionary<string, string> links, params string[] unresolvable) : IFolderFiles
    {
        public IEnumerable<FolderEntry> Below(string folder) => [];

        public string? Canonical(string path)
        {
            if (unresolvable.Any(u => path.StartsWith(u, StringComparison.OrdinalIgnoreCase))) return null;
            foreach (var (link, target) in links)
            {
                if (path.Equals(link, StringComparison.OrdinalIgnoreCase)) return target;
                if (path.StartsWith(link + @"\", StringComparison.OrdinalIgnoreCase)) return target + path[link.Length..];
            }

            return path;
        }
    }

    private static readonly LinkedFolders InboxToDownloads = new(new()
    {
        [@"C:\repos\app\inbox"] = @"C:\Users\marko\Downloads",
        [@"C:\repos\app\share"] = @"\\fileserver\team",
    });

    [TestMethod]
    [DataRow(@"C:\repos\app\inbox\invoice.txt", "Downloads")]
    [DataRow(@"inbox\invoice.txt", "Downloads")]
    [DataRow(@"C:\repos\app\share\handover.md", "network")]
    public void ReadingThroughAJunctionIntoAnUntrustedPlaceTaints(string path, string expected)
    {
        var reason = new GuardRules(folders: InboxToDownloads).Taint(ToolRequest.Read(path), Context());

        Assert.IsNotNull(reason, path);
        StringAssert.Contains(reason, expected, StringComparison.OrdinalIgnoreCase);
    }

    [TestMethod]
    [DataRow(@"Get-Content inbox\invoice.txt")]
    [DataRow(@"type C:\repos\app\share\handover.md")]
    public void AShellCommandReadingThroughAJunctionIntoAnUntrustedPlaceTaints(string command) =>
        Assert.IsNotNull(new GuardRules(folders: InboxToDownloads).Taint(ToolRequest.Shell(command), Context()), command);

    [TestMethod]
    public void AReadWhoseLinksCannotBeResolvedTaints()
    {
        var folders = new LinkedFolders([], @"C:\repos\app\loop");

        StringAssert.Contains(new GuardRules(folders: folders).Taint(ToolRequest.Read(@"C:\repos\app\loop\x.txt"), Context()), "links");
    }

    [TestMethod]
    public void ReadingAnOrdinaryFileThroughLinkResolutionDoesNotTaint() =>
        Assert.IsNull(new GuardRules(folders: InboxToDownloads).Taint(ToolRequest.Read(@"C:\repos\app\README.md"), Context()));

    /// <summary>A machine with drive N: mapped to a share and a SharePoint library synced below the profile.</summary>
    private sealed class Places : IRemotePlaces
    {
        public bool IsNetworkDrive(string path) => path.StartsWith("N:", StringComparison.OrdinalIgnoreCase);

        public IReadOnlyList<string> SyncRoots() => [@"C:\Users\marko\Contoso\Team Site - Documents"];
    }

    [TestMethod]
    [DataRow(@"N:\team\handover.md", "network drive")]
    [DataRow(@"C:\Users\marko\Contoso\Team Site - Documents\minutes.docx", "Team Site")]
    public void ReadingFromAMappedNetworkDriveOrASyncedLibraryTaints(string path, string expected)
    {
        var rules = new GuardRules(places: new Places());

        StringAssert.Contains(rules.Taint(ToolRequest.Read(path), Context()), expected, StringComparison.OrdinalIgnoreCase);
        Assert.IsNotNull(rules.Taint(ToolRequest.Shell($"Get-Content '{path}'"), Context()), "shell");
    }

    [TestMethod]
    public void ReadingThroughAJunctionOntoAMappedNetworkDriveTaints()
    {
        var rules = new GuardRules(folders: new LinkedFolders(new() { [@"C:\repos\app\docs"] = @"N:\team" }), places: new Places());

        Assert.IsNotNull(rules.Taint(ToolRequest.Read(@"C:\repos\app\docs\handover.md"), Context()));
    }

    [TestMethod]
    [DataRow(@"C:\Users\marko\Contoso\notes.txt")]
    [DataRow(@"D:\work\notes.txt")]
    public void ReadingNextToTheseRemotePlacesDoesNotTaint(string path) =>
        Assert.IsNull(new GuardRules(places: new Places()).Taint(ToolRequest.Read(path), Context()), path);

    [TestMethod]
    public void ATaintedChatAsksBeforeAToolWithoutPathArguments()
    {
        var verdict = new GuardRules().Evaluate(ToolRequest.Other(), Context(tainted: true));

        Assert.AreEqual(GuardDecision.Ask, verdict.Decision);
        StringAssert.Contains(verdict.Reason, "Tainted");
    }

    [TestMethod]
    public void ATaintedChatAsksBeforeAnOtherToolNamingPaths() =>
        Assert.AreEqual(GuardDecision.Ask, new GuardRules().Evaluate(new ToolRequest(ToolKind.Other, null, [@"C:\repos\app"], []), Context(tainted: true)).Decision);

    [TestMethod]
    [DataRow(@"C:\repos\app")]
    [DataRow(@"C:\Users\marko\Documents")]
    public void ATaintedChatAsksBeforeNavigatingPollux(string location)
    {
        var verdict = new GuardRules().Evaluate(ToolRequest.Navigate(location), Context(tainted: true));

        Assert.AreEqual(GuardDecision.Ask, verdict.Decision, location);
        StringAssert.Contains(verdict.Reason, "Tainted");
    }

    [TestMethod]
    public void ATaintedChatAsksBeforeNavigatingPolluxHome() =>
        Assert.AreEqual(GuardDecision.Ask, new GuardRules().Evaluate(new ToolRequest(ToolKind.Read, null, [], []) { Navigates = true }, Context(tainted: true)).Decision);

    [TestMethod]
    public void ATaintedChatStillReadsAndSearchesFreely()
    {
        var rules = new GuardRules(folders: new LinkedFolders([]));
        var tainted = Context(tainted: true);

        Assert.AreEqual(GuardDecision.Allow, rules.Evaluate(ToolRequest.Read(@"C:\repos\app\README.md"), tainted).Decision, "read");
        Assert.AreEqual(GuardDecision.Allow, rules.Evaluate(ToolRequest.Read(@"C:\repos\app\src") with { Recursive = true }, tainted).Decision, "search");
    }

    [TestMethod]
    public void AnUntaintedChatLetsToolsWithoutPathsAndNavigationGo()
    {
        Assert.AreEqual(GuardDecision.Allow, new GuardRules().Evaluate(ToolRequest.Other(), Context()).Decision, "other");
        Assert.AreEqual(GuardDecision.Allow, new GuardRules().Evaluate(ToolRequest.Navigate(@"C:\repos\app"), Context()).Decision, "navigate");
    }

    [TestMethod]
    public void WithoutOneDriveNothingBelowTheProfileTaints()
    {
        var noOneDrive = new GuardContext(@"C:\repos\app", false, name => name.StartsWith("OneDrive", StringComparison.OrdinalIgnoreCase) ? null : Env.GetValueOrDefault(name));

        Assert.IsNull(new GuardRules().Taint(ToolRequest.Read(@"C:\Users\marko\Documents\plan.txt"), noOneDrive));
    }
}
