using Pollux.Core.Snap;

namespace Pollux.Core.Tests.Snap;

[TestClass]
public sealed class SnapFileNameTests
{
    private static readonly DateTime Taken = new(2026, 10, 12, 14, 3, 22);

    [TestMethod]
    public void SnapIsNamedAfterTheTimeItWasTaken()
    {
        Assert.AreEqual("Snap 2026-10-12 14-03-22.png", SnapFileName.For(Taken, _ => false));
    }

    [TestMethod]
    public void TwoSnapsInTheSameSecondGetANumber()
    {
        var existing = new HashSet<string> { "Snap 2026-10-12 14-03-22.png", "Snap 2026-10-12 14-03-22 (2).png" };

        Assert.AreEqual("Snap 2026-10-12 14-03-22 (3).png", SnapFileName.For(Taken, existing.Contains));
    }
}
