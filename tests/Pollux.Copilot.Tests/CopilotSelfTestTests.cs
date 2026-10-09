namespace Pollux.Copilot.Tests;

[TestClass]
public sealed class CopilotSelfTestTests
{
    private string _dir = null!;

    [TestInitialize]
    public void CreateDirectory()
    {
        _dir = Path.Combine(Path.GetTempPath(), "pollux-selftest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [TestCleanup]
    public void DeleteDirectory() => Directory.Delete(_dir, recursive: true);

    [TestMethod]
    public async Task WorkingCliPassesAndIsReported()
    {
        var cli = new CopilotCli.Configured(FakeCopilotCli.Write(_dir));
        using var report = new StringWriter();

        var exitCode = await CopilotSelfTest.RunAsync(cli, report, TestContext.CancellationToken);

        Assert.AreEqual(0, exitCode, report.ToString());
        StringAssert.Contains(report.ToString(), "CLI 9.9.9-fake, protocol 3");
        StringAssert.Contains(report.ToString(), "PASSED");
    }

    [TestMethod]
    public async Task SelfTestAlsoRunsASourceGeneratedTool()
    {
        var cli = new CopilotCli.Configured(FakeCopilotCli.Write(_dir));
        using var report = new StringWriter();

        await CopilotSelfTest.RunAsync(cli, report, TestContext.CancellationToken);

        StringAssert.Contains(report.ToString(), """tool: {"cliVersion":"9.9.9-fake","protocolVersion":3}""");
    }

    [TestMethod]
    public async Task MissingCliFailsWithItsReason()
    {
        using var report = new StringWriter();

        var exitCode = await CopilotSelfTest.RunAsync(new CopilotCli.Missing("nothing bundled"), report, TestContext.CancellationToken);

        Assert.AreEqual(1, exitCode);
        StringAssert.Contains(report.ToString(), "FAILED");
        StringAssert.Contains(report.ToString(), "nothing bundled");
    }

    [TestMethod]
    public async Task CliSpeakingAnotherProtocolFails()
    {
        var cli = new CopilotCli.Configured(FakeCopilotCli.Write(_dir, protocolVersion: 99));
        using var report = new StringWriter();

        var exitCode = await CopilotSelfTest.RunAsync(cli, report, TestContext.CancellationToken);

        Assert.AreEqual(1, exitCode);
        StringAssert.Contains(report.ToString(), "protocol version mismatch");
    }

    public TestContext TestContext { get; set; } = null!;
}
