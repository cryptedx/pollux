using Pollux.Core.Launch;

namespace Pollux.Core.Tests.Launch;

[TestClass]
public sealed class CommandLineTests
{
    [TestMethod]
    public void QuoteFollowsTheWindowsArgumentRules()
    {
        Assert.AreEqual("\"C:\\a b\"", CommandLine.Quote(@"C:\a b"));
        Assert.AreEqual("\"C:\\\\\"", CommandLine.Quote(@"C:\"));
        Assert.AreEqual("\"say \\\"hi\\\"\"", CommandLine.Quote("say \"hi\""));
        Assert.AreEqual("\"a\\\\\\\"b\"", CommandLine.Quote("a\\\"b"));
        Assert.AreEqual("\"\"", CommandLine.Quote(""));
    }

    [TestMethod]
    public void S20_EmbeddedCmdChangesIntoAPercentFolderWithoutExpandingIt()
    {
        // Verified in the Windows VM (interactive cmd and cmd /c): the prompt then shows C:\x\a%USERNAME%b & c^d!e.
        Assert.AreEqual("cd /d \"C:\\x\\a\"^%\"USERNAME\"^%\"b & c^d!e\"", CommandLine.CmdChangeDirectory(@"C:\x\a%USERNAME%b & c^d!e"));
        Assert.AreEqual("cd /d \"C:\\x\"^%\"\"", CommandLine.CmdChangeDirectory(@"C:\x%"));
        Assert.AreEqual("cd /d \"D:\\\"", CommandLine.CmdChangeDirectory(@"D:\"));
        Assert.AreEqual("cd /d \"C:\\plain folder\"", CommandLine.CmdChangeDirectory(@"C:\plain folder"));
    }
}
