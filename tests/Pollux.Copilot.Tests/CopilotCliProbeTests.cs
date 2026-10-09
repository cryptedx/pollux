namespace Pollux.Copilot.Tests;

[TestClass]
public sealed class CopilotCliProbeTests
{
    private string _dir = null!;

    [TestInitialize]
    public void CreateDirectory()
    {
        _dir = Path.Combine(Path.GetTempPath(), "pollux-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [TestCleanup]
    public void DeleteDirectory() => Directory.Delete(_dir, recursive: true);

    private string FakeCli(int protocolVersion) => FakeCopilotCli.Write(_dir, protocolVersion);

    [TestMethod]
    public async Task HandshakeReportsTheCliVersionAndProtocol()
    {
        var cli = new CopilotCli.Configured(FakeCli(protocolVersion: 3));

        var handshake = await CopilotCliProbe.HandshakeAsync(cli, TestContext.CancellationToken);

        Assert.AreEqual(new CopilotHandshake("9.9.9-fake", 3), handshake);
    }

    [TestMethod]
    public async Task CliSpeakingAnotherProtocolFailsTheHandshake()
    {
        var cli = new CopilotCli.Configured(FakeCli(protocolVersion: 99));

        var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => CopilotCliProbe.HandshakeAsync(cli, TestContext.CancellationToken));

        StringAssert.Contains(ex.Message, "protocol version mismatch");
    }

    [TestMethod]
    public async Task MissingCliFailsWithItsReason()
    {
        var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => CopilotCliProbe.HandshakeAsync(new CopilotCli.Missing("no cli here"), TestContext.CancellationToken));

        Assert.AreEqual("no cli here", ex.Message);
    }

    public TestContext TestContext { get; set; } = null!;
}
