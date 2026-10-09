using Pollux.Copilot.Models;

namespace Pollux.Copilot.Tests.Models;

[TestClass]
public sealed class CopilotQuotaTrackerTests
{
    private static CopilotQuota Quota(int used) => new(false, 300, used, (300 - used) / 3.0, null, false);

    [TestMethod]
    public async Task RefreshStoresTheValueAndRaisesChangedOnlyWhenItDiffers()
    {
        var next = Quota(10);
        var tracker = new CopilotQuotaTracker(_ => Task.FromResult<CopilotQuota?>(next));
        var raised = new List<CopilotQuota?>();
        tracker.Changed += raised.Add;

        await tracker.RefreshAsync(TestContext.CancellationToken);
        await tracker.RefreshAsync(TestContext.CancellationToken);
        next = Quota(11);
        await tracker.RefreshAsync(TestContext.CancellationToken);

        Assert.AreEqual(Quota(11), tracker.Latest);
        CollectionAssert.AreEqual(new[] { Quota(10), Quota(11) }, raised);
    }

    [TestMethod]
    public async Task FailedOrEmptyRefreshKeepsTheLastValue()
    {
        var calls = 0;
        var tracker = new CopilotQuotaTracker(_ => ++calls switch
        {
            1 => Task.FromResult<CopilotQuota?>(Quota(5)),
            2 => Task.FromException<CopilotQuota?>(new IOException("gone")),
            _ => Task.FromResult<CopilotQuota?>(null),
        });

        await tracker.RefreshAsync(TestContext.CancellationToken);
        Assert.AreEqual(Quota(5), await tracker.RefreshAsync(TestContext.CancellationToken));
        Assert.AreEqual(Quota(5), await tracker.RefreshAsync(TestContext.CancellationToken));
        Assert.AreEqual(Quota(5), tracker.Latest);
    }

    [TestMethod]
    public async Task ClearForgetsTheValue()
    {
        var tracker = new CopilotQuotaTracker(_ => Task.FromResult<CopilotQuota?>(Quota(1)));
        await tracker.RefreshAsync(TestContext.CancellationToken);
        var raised = new List<CopilotQuota?>();
        tracker.Changed += raised.Add;

        tracker.Clear();

        Assert.IsNull(tracker.Latest);
        CollectionAssert.AreEqual(new CopilotQuota?[] { null }, raised);
    }

    [TestMethod]
    public async Task AnOlderRefreshThatCompletesLastDoesNotOverwriteTheNewerValue()
    {
        var first = new TaskCompletionSource<CopilotQuota?>();
        var second = new TaskCompletionSource<CopilotQuota?>();
        var pending = new Queue<TaskCompletionSource<CopilotQuota?>>([first, second]);
        var tracker = new CopilotQuotaTracker(_ => pending.Dequeue().Task);
        var raised = new List<CopilotQuota?>();
        tracker.Changed += raised.Add;

        var older = tracker.RefreshAsync(TestContext.CancellationToken);
        var newer = tracker.RefreshAsync(TestContext.CancellationToken);
        second.SetResult(Quota(20));
        await newer;
        first.SetResult(Quota(10));
        var olderResult = await older;

        Assert.AreEqual(Quota(20), tracker.Latest);
        Assert.AreEqual(Quota(20), olderResult);
        CollectionAssert.AreEqual(new[] { Quota(20) }, raised);
    }

    [TestMethod]
    public async Task ARefreshStartedBeforeClearDoesNotBringTheOldValueBack()
    {
        var inFlight = new TaskCompletionSource<CopilotQuota?>();
        var tracker = new CopilotQuotaTracker(_ => inFlight.Task);

        var refresh = tracker.RefreshAsync(TestContext.CancellationToken);
        tracker.Clear();
        inFlight.SetResult(Quota(10));
        await refresh;

        Assert.IsNull(tracker.Latest);
    }

    public TestContext TestContext { get; set; } = null!;
}
