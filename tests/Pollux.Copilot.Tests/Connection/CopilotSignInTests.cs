using Pollux.Copilot.Connection;

namespace Pollux.Copilot.Tests.Connection;

[TestClass]
public sealed class CopilotSignInTests
{
    [TestMethod]
    public void ConfiguredCliIsTheLoginCli()
    {
        Assert.AreEqual("/tools/copilot", CopilotSignIn.CliPath(new CopilotCli.Configured("/tools/copilot"), _ => false));
    }

    [TestMethod]
    public void BundledCliUsesItsInteractiveSiblingWhenPresent()
    {
        var wrapper = Path.Combine("x", "native", "copilot-runtime.exe");
        var sibling = Path.Combine("x", "native", "copilot.exe");

        Assert.AreEqual(sibling, CopilotSignIn.CliPath(new CopilotCli.Bundled(wrapper), f => f == sibling));
        Assert.AreEqual(wrapper, CopilotSignIn.CliPath(new CopilotCli.Bundled(wrapper), _ => false));
    }

    [TestMethod]
    public void MissingCliCannotSignIn()
    {
        Assert.IsNull(CopilotSignIn.CliPath(new CopilotCli.Missing("gone"), _ => true));
    }
}
