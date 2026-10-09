using Pollux.Copilot.Connection;

namespace Pollux.Copilot.Tests.Connection;

[TestClass]
public sealed class CopilotSignInTests
{
    [TestMethod]
    public void ConfiguredCliRunsItsLoginInATerminal()
    {
        var line = CopilotSignIn.TerminalCommandLine(new CopilotCli.Configured("/tools/copilot"), _ => false);
        Assert.AreEqual("/k \"\"/tools/copilot\" login\"", line);
    }

    [TestMethod]
    public void BundledCliUsesItsInteractiveSiblingWhenPresent()
    {
        var wrapper = Path.Combine("x", "native", "copilot-runtime.exe");
        var sibling = Path.Combine("x", "native", "copilot.exe");

        Assert.Contains(sibling, CopilotSignIn.TerminalCommandLine(new CopilotCli.Bundled(wrapper), f => f == sibling)!);
        Assert.Contains(wrapper, CopilotSignIn.TerminalCommandLine(new CopilotCli.Bundled(wrapper), _ => false)!);
    }

    [TestMethod]
    public void MissingCliCannotSignIn()
    {
        Assert.IsNull(CopilotSignIn.TerminalCommandLine(new CopilotCli.Missing("gone"), _ => true));
    }
}
