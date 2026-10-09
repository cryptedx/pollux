using Pollux.Copilot.Connection;

namespace Pollux.Copilot.Tests.Connection;

[TestClass]
public sealed class CopilotConnectionMonitorTests
{
    private Func<ICopilotConnection>? _make;
    private Exception? _startFails;

    private CopilotConnectionMonitor Monitor(out CopilotHost host, bool available = true)
    {
        host = new CopilotHost(() =>
        {
            if (_startFails is not null) return new FailingConnection(_startFails);
            return _make!();
        }, available);
        return new CopilotConnectionMonitor(host);
    }

    private async Task<CopilotConnectionState> Check(CopilotAccountStatus? status = null, Exception? accountFails = null)
    {
        _make = () => new FakeAccountConnection(status ?? new CopilotAccountStatus(true, "octocat"), accountFails);
        var monitor = Monitor(out var host);
        await using (host) return await monitor.CheckAsync(TestContext.CancellationToken);
    }

    [TestMethod]
    public async Task SignedInAccountIsReady()
    {
        var state = await Check();
        Assert.AreEqual(CopilotConnectionIssue.Ready, state.Issue);
        Assert.AreEqual(CopilotPanelAction.None, state.Action);
    }

    [TestMethod]
    public async Task NotSignedInOffersSignIn()
    {
        var state = await Check(new CopilotAccountStatus(false));
        Assert.AreEqual(CopilotConnectionIssue.NotSignedIn, state.Issue);
        Assert.AreEqual(CopilotPanelAction.SignIn, state.Action);
    }

    [TestMethod]
    public async Task NoSeatIsExplainedWithoutSignIn()
    {
        var state = await Check(new CopilotAccountStatus(false, Problem: "User does not have a Copilot subscription"));
        Assert.AreEqual(CopilotConnectionIssue.NoSeat, state.Issue);
        Assert.AreEqual(CopilotPanelAction.Recheck, state.Action);
    }

    [TestMethod]
    public async Task OrgPolicyBlock()
    {
        var state = await Check(accountFails: new InvalidOperationException("Copilot access disabled by your organization policy"));
        Assert.AreEqual(CopilotConnectionIssue.OrgPolicy, state.Issue);
    }

    [TestMethod]
    public async Task ProxyAndCertificateFailureOffersSettings()
    {
        var state = await Check(accountFails: new IOException("unable to verify the first certificate"));
        Assert.AreEqual(CopilotConnectionIssue.Network, state.Issue);
        Assert.AreEqual(CopilotPanelAction.OpenSettings, state.Action);
    }

    [TestMethod]
    public async Task QuotaExhausted()
    {
        var state = await Check(new CopilotAccountStatus(true, "octocat", QuotaExhausted: true));
        Assert.AreEqual(CopilotConnectionIssue.QuotaExhausted, state.Issue);
    }

    [TestMethod]
    public async Task UnknownAccountErrorShowsItsMessageAndRetry()
    {
        var state = await Check(accountFails: new InvalidOperationException("boom"));
        Assert.AreEqual(CopilotConnectionIssue.CliFailed, state.Issue);
        Assert.Contains("boom", state.Message);
        Assert.AreEqual(CopilotPanelAction.Retry, state.Action);
    }

    [TestMethod]
    public async Task MissingCliOffersSettings()
    {
        _startFails = new InvalidOperationException("The bundled Copilot CLI is missing.");
        var monitor = Monitor(out var host);
        await using (host)
        {
            var state = await monitor.CheckAsync(TestContext.CancellationToken);
            Assert.AreEqual(CopilotConnectionIssue.CliMissing, state.Issue);
            Assert.AreEqual(CopilotPanelAction.OpenSettings, state.Action);
        }
    }

    [TestMethod]
    public async Task FailedCliStartOffersRetry()
    {
        _startFails = new IOException("CLI process exited unexpectedly.");
        var monitor = Monitor(out var host);
        await using (host)
        {
            var state = await monitor.CheckAsync(TestContext.CancellationToken);
            Assert.AreEqual(CopilotConnectionIssue.CliFailed, state.Issue);
            Assert.AreEqual(CopilotPanelAction.Retry, state.Action);
        }
    }

    [TestMethod]
    public async Task DisabledCopilotHasNoState()
    {
        var monitor = Monitor(out var host, available: false);
        await using (host)
            Assert.AreEqual(CopilotConnectionIssue.Disabled, (await monitor.CheckAsync(TestContext.CancellationToken)).Issue);
    }

    [TestMethod]
    public void EveryProblemStateHasItsOwnTextAndAction()
    {
        var views = Enum.GetValues<CopilotConnectionIssue>()
            .Where(i => i is not (CopilotConnectionIssue.Ready or CopilotConnectionIssue.Disabled))
            .Select(i => CopilotConnectionState.For(i, "x"))
            .ToList();
        Assert.AreEqual(views.Count, views.Select(v => v.Message).Distinct().Count());
        Assert.IsTrue(views.All(v => v.Title.Length > 0 && v.Action != CopilotPanelAction.None));
    }

    public TestContext TestContext { get; set; } = null!;

    private sealed class FakeAccountConnection(CopilotAccountStatus status, Exception? fails) : ICopilotConnection, ICopilotAccountSource
    {
        public Task<string> Exited { get; } = new TaskCompletionSource<string>().Task;
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public Task<CopilotAccountStatus> GetAccountStatusAsync(CancellationToken cancellationToken) =>
            fails is null ? Task.FromResult(status) : Task.FromException<CopilotAccountStatus>(fails);
    }

    private sealed class FailingConnection(Exception ex) : ICopilotConnection
    {
        public Task<string> Exited { get; } = new TaskCompletionSource<string>().Task;
        public Task StartAsync(CancellationToken cancellationToken) => Task.FromException(ex);
        public Task StopAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
