using Pollux.Copilot.Models;

namespace Pollux.Copilot.Tests.Models;

/// <summary>The remaining premium requests as the Panel and the status bar show them.</summary>
[TestClass]
public sealed class CopilotQuotaTests
{
    [TestMethod]
    public void RemainingRequestsAreEntitlementMinusUsed()
    {
        var q = new CopilotQuota(false, 300, 158, 47.3, new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero), false);

        Assert.AreEqual(142, q.Remaining);
        Assert.AreEqual(CopilotQuotaLevel.Ok, q.Level);
        Assert.AreEqual("142 of 300 premium requests left (resets 1 Nov)", q.Text);
        Assert.AreEqual("142/300", q.Compact);
    }

    [TestMethod]
    public void LowQuotaIsFlaggedAtTenPercent()
    {
        var q = new CopilotQuota(false, 300, 275, 8.3, null, false);

        Assert.AreEqual(CopilotQuotaLevel.Low, q.Level);
        Assert.AreEqual("25 of 300 premium requests left", q.Text);
    }

    [TestMethod]
    public void NothingLeftIsExhaustedUnlessUsageIsStillAllowed()
    {
        Assert.AreEqual(CopilotQuotaLevel.Exhausted, new CopilotQuota(false, 300, 300, 0, null, false).Level);
        Assert.IsTrue(new CopilotQuota(false, 300, 300, 0, null, false).Exhausted);

        var overage = new CopilotQuota(false, 300, 310, 0, null, true);
        Assert.IsFalse(overage.Exhausted);
        Assert.AreEqual(CopilotQuotaLevel.Low, overage.Level);
        Assert.AreEqual("0 of 300 premium requests left", overage.Text);
        Assert.AreEqual(0, overage.Remaining);
    }

    [TestMethod]
    public void UnlimitedEntitlementShowsNoCount()
    {
        var q = new CopilotQuota(true, -1, 12, 100, null, true);

        Assert.AreEqual(CopilotQuotaLevel.Unlimited, q.Level);
        Assert.IsFalse(q.Exhausted);
        Assert.AreEqual("Unlimited premium requests", q.Text);
        Assert.AreEqual("unlimited", q.Compact);
    }

}
