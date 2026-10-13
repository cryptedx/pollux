using Pollux.Core.Snap;

namespace Pollux.Core.Tests.Snap;

[TestClass]
public sealed class SnapRetentionTests
{
    private static readonly DateTime Now = new(2026, 10, 13, 12, 0, 0);

    [TestMethod]
    public void RecognisesItsOwnNamesOnly()
    {
        Assert.IsTrue(SnapFileName.IsSnap("Snap 2026-10-12 14-03-22.png"));
        Assert.IsTrue(SnapFileName.IsSnap("Snap 2026-10-12 14-03-22 (2).png"));
        Assert.IsFalse(SnapFileName.IsSnap("Snap 2026-10-12 14-03-22.jpg"));
        Assert.IsFalse(SnapFileName.IsSnap("holiday.png"));
        Assert.IsFalse(SnapFileName.IsSnap("Snap of my desk.png"));
        Assert.IsFalse(SnapFileName.IsSnap("Snap 2026-13-40 14-03-22.png"));
    }

    [TestMethod]
    public void SnapsTakenLongerAgoThanTheLimitAreDue()
    {
        string[] files =
        [
            "Snap 2026-09-01 08-00-00.png",      // 42 days
            "Snap 2026-10-06 11-59-59 (2).png",  // just over 7 days
            "Snap 2026-10-06 12-00-01.png",      // just under 7 days
            "Snap 2026-10-13 09-00-00.png",      // today
        ];

        CollectionAssert.AreEqual(
            new[] { "Snap 2026-09-01 08-00-00.png", "Snap 2026-10-06 11-59-59 (2).png" },
            SnapRetention.Due(files, Now, keepDays: 7).ToArray());
    }

    [TestMethod]
    public void OtherFilesAreNeverDue()
    {
        string[] files = ["notes.txt", "old.png", "Snap 2020-01-01 00-00-00.jpg", "Snap 2020-01-01 00-00-00.png"];

        CollectionAssert.AreEqual(new[] { "Snap 2020-01-01 00-00-00.png" }, SnapRetention.Due(files, Now, keepDays: 1).ToArray());
    }

    [TestMethod]
    public void ZeroKeepsEverything()
    {
        Assert.AreEqual(0, SnapRetention.Due(["Snap 2020-01-01 00-00-00.png"], Now, keepDays: 0).Count());
    }

    [TestMethod]
    public void KeepDaysIsOffByDefault()
    {
        Assert.AreEqual(0, Pollux.Core.Settings.SettingsStore.Parse("{}").Settings.Snap.KeepDays);
    }
}
