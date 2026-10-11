using Pollux.Copilot.Tools;

namespace Pollux.Copilot.Tests.Tools;

/// <summary>How a tool's pane name (left, right, active, other) picks a Pane.</summary>
[TestClass]
public sealed class PaneTargetsTests
{
    private static PaneSnapshot Snap(string pane, bool active) => new(pane, active, null, [], 0, [], []);

    [TestMethod]
    [DataRow("left", 0, true, 0)]
    [DataRow("left", 1, true, 0)]
    [DataRow("right", 0, true, 1)]
    [DataRow("right", 1, true, 1)]
    [DataRow("active", 0, true, 0)]
    [DataRow("active", 1, true, 1)]
    [DataRow("other", 0, true, 1)]
    [DataRow("other", 1, true, 0)]
    [DataRow("left", 0, false, 0)]
    [DataRow("active", 0, false, 0)]
    public void ADualOrSinglePaneWindowResolvesTheTarget(string target, int active, bool dual, int expected)
    {
        Assert.AreEqual(expected, PaneTargets.ResolveIndex(target, active, dual));
    }

    [TestMethod]
    [DataRow("right")]
    [DataRow("other")]
    public void ASingleWindowHasNoRightOrOtherPane(string target)
    {
        Assert.IsNull(PaneTargets.ResolveIndex(target, 0, dual: false));
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("nonsense")]
    [DataRow("LEFT")]
    public void AnythingElseIsTheActivePane(string target)
    {
        Assert.AreEqual(1, PaneTargets.ResolveIndex(target, 1, dual: true));
        Assert.AreEqual(0, PaneTargets.ResolveIndex(target, 0, dual: false));
    }

    [TestMethod]
    public void NormalizeIsCaseInsensitiveAndDefaultsToActive()
    {
        Assert.AreEqual("left", PaneTargets.Normalize(" LEFT "));
        Assert.AreEqual("other", PaneTargets.Normalize("Other"));
        Assert.AreEqual("active", PaneTargets.Normalize(null));
        Assert.AreEqual("active", PaneTargets.Normalize(""));
        Assert.IsNull(PaneTargets.Normalize("middle"));
    }

    [TestMethod]
    public void SnapshotsResolveTheSameWayAsIndexes()
    {
        PaneSnapshot[] dual = [Snap("left", false), Snap("right", true)];

        Assert.AreSame(dual[0], PaneTargets.Resolve(dual, "left"));
        Assert.AreSame(dual[1], PaneTargets.Resolve(dual, "right"));
        Assert.AreSame(dual[1], PaneTargets.Resolve(dual, "active"));
        Assert.AreSame(dual[0], PaneTargets.Resolve(dual, "other"));
    }

    [TestMethod]
    public void ASinglePaneSnapshotHasNoRightOrOtherPane()
    {
        PaneSnapshot[] single = [Snap("left", true)];

        Assert.AreSame(single[0], PaneTargets.Resolve(single, "left"));
        Assert.AreSame(single[0], PaneTargets.Resolve(single, "active"));
        Assert.IsNull(PaneTargets.Resolve(single, "right"));
        Assert.IsNull(PaneTargets.Resolve(single, "other"));
    }

    [TestMethod]
    public void AnUnknownTargetResolvesToNoSnapshot()
    {
        Assert.IsNull(PaneTargets.Resolve([Snap("left", true)], "middle"));
        Assert.IsNull(PaneTargets.Resolve([], "active"));
    }
}
