namespace Pollux.Copilot.Tests;

[TestClass]
public sealed class CopilotCliConnectionTests
{
    private static readonly TimeSpan FastHealthCheck = TimeSpan.FromMilliseconds(50);
    private string _dir = null!;

    [TestInitialize]
    public void CreateDirectory()
    {
        _dir = Path.Combine(Path.GetTempPath(), "pollux-conn-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [TestCleanup]
    public void DeleteDirectory() => Directory.Delete(_dir, recursive: true);

    [TestMethod]
    public async Task HostStartsTheCliOverStdioOnFirstUse()
    {
        var cli = new CopilotCli.Configured(FakeCopilotCli.Write(_dir));
        await using var host = new CopilotHost(() => new CopilotCliConnection(cli, FastHealthCheck), available: true);

        var connection = await host.ConnectAsync(TestContext.CancellationToken);

        Assert.IsInstanceOfType<CopilotCliConnection>(connection);
        Assert.AreEqual(CopilotHostStatus.Running, host.State.Status);
        await ((CopilotCliConnection)connection).Client.PingAsync(cancellationToken: TestContext.CancellationToken);
    }

    [TestMethod]
    public async Task HostReportsACliThatDiesWhileRunning()
    {
        var cli = new CopilotCli.Configured(FakeCopilotCli.Write(_dir, exitAfterConnectMs: 100));
        await using var host = new CopilotHost(() => new CopilotCliConnection(cli, FastHealthCheck), available: true);
        var crashed = new TaskCompletionSource<CopilotHostState>(TaskCreationOptions.RunContinuationsAsynchronously);
        host.StateChanged += s =>
        {
            if (s.Status == CopilotHostStatus.Crashed) crashed.TrySetResult(s);
        };

        await host.ConnectAsync(TestContext.CancellationToken);
        var state = await crashed.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.CancellationToken);

        StringAssert.StartsWith(state.Reason, "The Copilot CLI stopped unexpectedly");
    }

    [TestMethod]
    public async Task MissingCliFailsTheStartWithItsReason()
    {
        await using var host = new CopilotHost(() => new CopilotCliConnection(new CopilotCli.Missing("no cli here"), FastHealthCheck), available: true);

        Assert.IsNull(await host.ConnectAsync(TestContext.CancellationToken));

        Assert.AreEqual(new CopilotHostState(CopilotHostStatus.Failed, "no cli here"), host.State);
    }

    [TestMethod]
    public async Task StoppedCliDoesNotCountAsExited()
    {
        var cli = new CopilotCli.Configured(FakeCopilotCli.Write(_dir));
        await using var connection = new CopilotCliConnection(cli, FastHealthCheck);
        await connection.StartAsync(TestContext.CancellationToken);

        await connection.StopAsync();
        await Task.Delay(200, TestContext.CancellationToken);

        Assert.IsFalse(connection.Exited.IsCompleted);
    }

    public TestContext TestContext { get; set; } = null!;
}
