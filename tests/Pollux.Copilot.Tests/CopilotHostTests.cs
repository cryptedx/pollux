namespace Pollux.Copilot.Tests;

[TestClass]
public sealed class CopilotHostTests
{
    private readonly List<FakeConnection> _started = [];
    private Func<FakeConnection, Task>? _onStart;

    private CopilotHost Host(bool available = true) => new(() =>
    {
        var c = new FakeConnection(_onStart);
        _started.Add(c);
        return c;
    }, available);

    [TestMethod]
    public async Task NoCliRunsUntilFirstUse()
    {
        await using var host = Host();

        Assert.AreEqual(CopilotHostStatus.NotStarted, host.State.Status);
        Assert.IsEmpty(_started);
    }

    [TestMethod]
    public async Task FirstUseStartsTheCli()
    {
        await using var host = Host();

        var connection = await host.ConnectAsync(TestContext.CancellationToken);

        Assert.AreSame(_started.Single(), connection);
        Assert.IsTrue(_started[0].Started);
        Assert.AreEqual(CopilotHostStatus.Running, host.State.Status);
    }

    [TestMethod]
    public async Task AllUsersShareOneCli()
    {
        await using var host = Host();
        var gate = new TaskCompletionSource();
        _onStart = _ => gate.Task;

        var first = host.ConnectAsync(TestContext.CancellationToken);
        var second = host.ConnectAsync(TestContext.CancellationToken);
        gate.SetResult();
        var third = await host.ConnectAsync(TestContext.CancellationToken);

        Assert.HasCount(1, _started);
        Assert.AreSame(await first, await second);
        Assert.AreSame(await first, third);
    }

    [TestMethod]
    public async Task DisabledCopilotNeverStartsTheCli()
    {
        await using var host = Host(available: false);

        var connection = await host.ConnectAsync(TestContext.CancellationToken);

        Assert.IsNull(connection);
        Assert.IsEmpty(_started);
        Assert.AreEqual(CopilotHostStatus.Disabled, host.State.Status);
    }

    [TestMethod]
    public async Task FailedStartIsReportedWithItsCauseAndRetriedOnNextUse()
    {
        await using var host = Host();
        _onStart = _ => throw new InvalidOperationException("The bundled Copilot CLI is missing.");

        var connection = await host.ConnectAsync(TestContext.CancellationToken);

        Assert.IsNull(connection);
        Assert.AreEqual(new CopilotHostState(CopilotHostStatus.Failed, "The bundled Copilot CLI is missing."), host.State);
        Assert.IsTrue(_started[0].Disposed);

        _onStart = null;
        Assert.IsNotNull(await host.ConnectAsync(TestContext.CancellationToken));
        Assert.AreEqual(CopilotHostStatus.Running, host.State.Status);
    }

    [TestMethod]
    public async Task CrashIsDetectedAndSurfaced()
    {
        await using var host = Host();
        var crashed = WaitForStatus(host, CopilotHostStatus.Crashed);
        await host.ConnectAsync(TestContext.CancellationToken);

        _started[0].Crash("CLI process exited unexpectedly.");

        Assert.AreEqual(new CopilotHostState(CopilotHostStatus.Crashed, "CLI process exited unexpectedly."), await crashed);
        Assert.IsTrue(_started[0].Disposed);
    }

    [TestMethod]
    public async Task NextUseAfterACrashStartsANewCli()
    {
        await using var host = Host();
        var crashed = WaitForStatus(host, CopilotHostStatus.Crashed);
        var first = await host.ConnectAsync(TestContext.CancellationToken);
        _started[0].Crash("gone");
        await crashed;

        var second = await host.ConnectAsync(TestContext.CancellationToken);

        Assert.HasCount(2, _started);
        Assert.AreNotSame(first, second);
        Assert.AreEqual(CopilotHostStatus.Running, host.State.Status);
    }

    [TestMethod]
    public async Task DisablingStopsTheRunningCli()
    {
        await using var host = Host();
        await host.ConnectAsync(TestContext.CancellationToken);

        await host.SetAvailableAsync(false);

        Assert.IsTrue(_started[0].Stopped);
        Assert.IsTrue(_started[0].Disposed);
        Assert.AreEqual(CopilotHostStatus.Disabled, host.State.Status);
    }

    [TestMethod]
    public async Task StoppingTheCliOnPurposeIsNotACrash()
    {
        await using var host = Host();
        await host.ConnectAsync(TestContext.CancellationToken);
        await host.SetAvailableAsync(false);

        _started[0].Crash("exited after stop");
        await Task.Delay(50, TestContext.CancellationToken);

        Assert.AreEqual(CopilotHostStatus.Disabled, host.State.Status);
    }

    [TestMethod]
    public async Task ReEnablingWaitsForTheNextUse()
    {
        await using var host = Host(available: false);

        await host.SetAvailableAsync(true);

        Assert.AreEqual(CopilotHostStatus.NotStarted, host.State.Status);
        Assert.IsEmpty(_started);
    }

    [TestMethod]
    public async Task StateChangesAreAnnounced()
    {
        await using var host = Host();
        var seen = new List<CopilotHostStatus>();
        host.StateChanged += s => seen.Add(s.Status);

        await host.ConnectAsync(TestContext.CancellationToken);
        await host.SetAvailableAsync(false);

        CollectionAssert.AreEqual(new[] { CopilotHostStatus.Starting, CopilotHostStatus.Running, CopilotHostStatus.Disabled }, seen);
    }

    [TestMethod]
    public async Task ExitingPolluxStopsTheCli()
    {
        var host = Host();
        await host.ConnectAsync(TestContext.CancellationToken);

        await host.DisposeAsync();

        Assert.IsTrue(_started[0].Stopped);
        Assert.IsTrue(_started[0].Disposed);
        Assert.AreEqual(CopilotHostStatus.Stopped, host.State.Status);
        Assert.IsNull(await host.ConnectAsync(TestContext.CancellationToken));
        Assert.HasCount(1, _started);
    }

    private static Task<CopilotHostState> WaitForStatus(CopilotHost host, CopilotHostStatus status)
    {
        var tcs = new TaskCompletionSource<CopilotHostState>(TaskCreationOptions.RunContinuationsAsynchronously);
        host.StateChanged += s =>
        {
            if (s.Status == status) tcs.TrySetResult(s);
        };
        return tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    public TestContext TestContext { get; set; } = null!;

    private sealed class FakeConnection(Func<FakeConnection, Task>? onStart) : ICopilotConnection
    {
        private readonly TaskCompletionSource<string> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Started { get; private set; }
        public bool Stopped { get; private set; }
        public bool Disposed { get; private set; }

        public Task<string> Exited => _exited.Task;

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            if (onStart is not null) await onStart(this);
            Started = true;
        }

        public Task StopAsync()
        {
            Stopped = true;
            return Task.CompletedTask;
        }

        public void Crash(string reason) => _exited.TrySetResult(reason);

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
