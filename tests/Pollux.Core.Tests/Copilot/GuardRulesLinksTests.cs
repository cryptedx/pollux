using Pollux.Core.Copilot.Guards;

namespace Pollux.Core.Tests.Copilot;

/// <summary>
/// Junctions and symbolic links must not lead around the Guard Rules: a path is judged by where it really points
/// (C:\repos\app\keys -> ~\.ssh is ~\.ssh), and a path whose links cannot be resolved is asked (fail closed).
/// </summary>
[TestClass]
public sealed class GuardRulesLinksTests
{
    private const string WorkingFolder = @"C:\repos\app";
    private const string Ssh = @"C:\Users\marko\.ssh";

    private static GuardContext Context() => new(WorkingFolder, false, name => name switch
    {
        "USERPROFILE" => @"C:\Users\marko",
        "APPDATA" => @"C:\Users\marko\AppData\Roaming",
        "LOCALAPPDATA" => @"C:\Users\marko\AppData\Local",
        _ => null,
    });

    /// <summary>A file system with links (link path -> target) and paths whose links cannot be resolved.</summary>
    private sealed class LinkedFolders(Dictionary<string, string> links, Dictionary<string, FolderEntry[]>? below = null, params string[] unresolvable) : IFolderFiles
    {
        public IEnumerable<FolderEntry> Below(string folder) => below is not null && below.TryGetValue(folder, out var e) ? e : [];

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

    private static readonly LinkedFolders KeysToSsh = new(new() { [@"C:\repos\app\keys"] = Ssh });

    private static GuardVerdict Evaluate(ToolRequest request, IFolderFiles folders) => new GuardRules(folders: folders).Evaluate(request, Context());

    [TestMethod]
    public void ReadingAProtectedFileThroughAJunctionIsDenied()
    {
        var verdict = Evaluate(ToolRequest.Read(@"C:\repos\app\keys\id_rsa"), KeysToSsh);

        Assert.AreEqual(GuardDecision.Deny, verdict.Decision);
        Assert.AreEqual(@"Protected secret (~\.ssh)", verdict.Reason);
    }

    [TestMethod]
    [DataRow(@"type keys\id_rsa")]
    [DataRow(@"Get-Content .\keys\id_rsa")]
    public void AShellCommandNamingAProtectedFileThroughAJunctionIsDenied(string command)
    {
        Assert.AreEqual(GuardDecision.Deny, Evaluate(ToolRequest.Shell(command), KeysToSsh).Decision, command);
    }

    [TestMethod]
    public void WritingANewFileBelowALinkToAProtectedFolderIsDenied()
    {
        var verdict = Evaluate(ToolRequest.Write(@"C:\repos\app\keys\authorized_keys2"), KeysToSsh);

        Assert.AreEqual(GuardDecision.Deny, verdict.Decision);
    }

    [TestMethod]
    public void WritingThroughALinkThatLeavesTheWorkingFolderIsAsked()
    {
        var folders = new LinkedFolders(new() { [@"C:\repos\app\out"] = @"C:\Windows\System32" });

        var verdict = Evaluate(ToolRequest.Write(@"C:\repos\app\out\x.dll"), folders);

        Assert.AreEqual(GuardDecision.Ask, verdict.Decision);
        Assert.AreEqual("Writes outside the Working Folder", verdict.Reason);
    }

    [TestMethod]
    public void AWorkingFolderThatIsItselfALinkStillContainsItsFiles()
    {
        var folders = new LinkedFolders(new() { [WorkingFolder] = @"D:\src\app" });

        Assert.AreEqual(GuardDecision.Allow, Evaluate(ToolRequest.Write(@"C:\repos\app\x.txt"), folders).Decision);
    }

    [TestMethod]
    public void APathWhoseLinksCannotBeResolvedIsAsked()
    {
        var folders = new LinkedFolders([], null, @"C:\repos\app\broken");

        var verdict = Evaluate(ToolRequest.Read(@"C:\repos\app\broken\x.txt"), folders);

        Assert.AreEqual(GuardDecision.Ask, verdict.Decision);
        Assert.AreEqual(GuardRules.CannotResolveLinks, verdict.Reason);
    }

    [TestMethod]
    public void ARecursiveSearchOverALinkToAProtectedFolderIsDenied()
    {
        var verdict = Evaluate(ToolRequest.Shell("rg BEGIN keys"), KeysToSsh);

        Assert.AreEqual(GuardDecision.Deny, verdict.Decision);
    }

    [TestMethod]
    public void ARecursiveSearchOverAFolderContainingALinkToAProtectedFolderIsDenied()
    {
        var folders = new LinkedFolders(
            new() { [@"C:\repos\app\keys"] = Ssh },
            new() { [WorkingFolder] = [FolderEntry.File("README.md"), FolderEntry.Link("keys")] });

        var verdict = Evaluate(ToolRequest.Shell("grep -R BEGIN ."), folders);

        Assert.AreEqual(GuardDecision.Deny, verdict.Decision);
        Assert.AreEqual(@"Recursive search reaches a protected secret through a link (keys -> ~\.ssh)", verdict.Reason);
    }

    [TestMethod]
    public void ARecursiveSearchOverAFolderContainingALinkThatLeavesTheWorkingFolderIsAsked()
    {
        var folders = new LinkedFolders(
            new() { [@"C:\repos\app\shared"] = @"D:\shared" },
            new() { [WorkingFolder] = [FolderEntry.Link("shared")] });

        var verdict = Evaluate(ToolRequest.Shell("rg TODO"), folders);

        Assert.AreEqual(GuardDecision.Ask, verdict.Decision);
        Assert.AreEqual("Recursive search follows a link outside the Working Folder (shared)", verdict.Reason);
    }

    [TestMethod]
    public void ARecursiveSearchOverALinkThatCannotBeResolvedIsAsked()
    {
        var folders = new LinkedFolders([], new() { [WorkingFolder] = [FolderEntry.Link("odd")] }, @"C:\repos\app\odd");

        Assert.AreEqual(GuardRules.CannotResolveLinks, Evaluate(ToolRequest.Shell("rg TODO"), folders).Reason);
    }
}
