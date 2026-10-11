using Pollux.Core.Session;

namespace Pollux.Core.Tests.Session;

[TestClass]
public sealed class LaunchRequestTests
{
    private static LaunchRequest Parse(params string[] args) =>
        LaunchRequest.Parse(args, p => p.StartsWith("/dir", StringComparison.Ordinal), p => p.StartsWith("/file", StringComparison.Ordinal));

    [TestMethod]
    public void CommandsAreSplitAndTrimmed()
    {
        CollectionAssert.AreEqual(new[] { "a", "b", "c" }, Parse("--command", "a, b ,c,,").Commands.ToArray());
        Assert.AreEqual(0, Parse("--command").Commands.Count);
    }

    [TestMethod]
    public void ValuesAfterFlagsAreNotTargets()
    {
        var r = Parse("--workspace", "/dirX");
        Assert.AreEqual("/dirX", r.Workspace);
        Assert.IsNull(r.Target);
        Assert.IsNull(Parse("--command", "/dirX").Target);
    }

    [TestMethod]
    public void LeadingExeIsStripped()
    {
        var r = Parse(@"C:\Apps\Pollux.exe", "--quit");
        Assert.IsTrue(r.Quit);
        Assert.IsNull(r.Target);
        Assert.IsNull(Parse("pollux.EXE").Target);
    }

    [TestMethod]
    public void TargetIsFirstExistingPositional()
    {
        Assert.AreEqual(Path.GetFullPath("/dirA"), Parse("/dirA", "--notes").Target);
        Assert.AreEqual(Path.GetFullPath("/file.txt"), Parse("--background", "/file.txt").Target);
        Assert.IsNull(Parse("/missing").Target);
    }

    [TestMethod]
    public void FlagsAreRecognised()
    {
        var r = Parse("--background", "--notes", "--downloads", "--clipboard");
        Assert.IsTrue(r.Background && r.Notes && r.Downloads && r.Clipboard && r.WantsNoWindow);
        Assert.IsFalse(Parse("/dirA").WantsNoWindow);
        Assert.IsFalse(Parse("--quit").WantsNoWindow);
    }

    [TestMethod]
    public void S09_AdminInstanceIgnoresForwardedCommandsAndWorkspaces()
    {
        // A process running as the user launches Pollux.exe with commands for the elevated instance to run.
        var r = Parse(@"C:\Apps\Pollux.exe", "/dirA", "--command", "integration.vscode,terminal.open", "--workspace", "evil").ForAdminInstance();

        Assert.AreEqual(0, r.Commands.Count);
        Assert.IsNull(r.Workspace);
        Assert.AreEqual(Path.GetFullPath("/dirA"), r.Target);
    }

    [TestMethod]
    public void AdminInstanceKeepsTheOtherFlags()
    {
        var r = Parse("--quit", "--notes", "--background").ForAdminInstance();
        Assert.IsTrue(r.Quit && r.Notes && r.Background);
    }
}
