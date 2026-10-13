using Pollux.Core.Snap;

namespace Pollux.Core.Tests.Snap;

[TestClass]
public sealed class SnapSelectionTests
{
    // A monitor right of the primary one, 1920×1080 at x = 2560.
    private static readonly SnapMonitor Monitor = new(new PixelRect(2560, 0, 1920, 1080), 1.0);

    private static SnapSelection Drag(int x1, int y1, int x2, int y2) =>
        SnapSelection.Start(Monitor, new PixelPoint(x1, y1)).WithEnd(new PixelPoint(x2, y2));

    [TestMethod]
    public void DraggingInAnyDirectionGivesTheSameArea()
    {
        var expected = new PixelRect(2600, 100, 200, 50);

        Assert.AreEqual(expected, Drag(2600, 100, 2800, 150).Area);
        Assert.AreEqual(expected, Drag(2800, 150, 2600, 100).Area);
        Assert.AreEqual(expected, Drag(2600, 150, 2800, 100).Area);
    }

    [TestMethod]
    public void DraggingPastTheMonitorEdgeStopsAtTheEdge()
    {
        Assert.AreEqual(new PixelRect(4400, 1000, 80, 80), Drag(4400, 1000, 5000, 1300).Area);
        Assert.AreEqual(new PixelRect(2560, 0, 40, 100), Drag(2600, 100, 1000, -50).Area);
    }

    [TestMethod]
    public void ArrowKeysMoveTheDraggedCornerAndStopAtTheEdge()
    {
        var s = Drag(2600, 100, 2800, 150);

        Assert.AreEqual(new PixelRect(2600, 100, 201, 50), s.MoveEndBy(1, 0).Area);
        Assert.AreEqual(new PixelRect(2600, 100, 200, 40), s.MoveEndBy(0, -10).Area);
        Assert.AreEqual(new PixelRect(2600, 100, 1880, 50), s.MoveEndBy(5000, 0).Area);
    }

    [TestMethod]
    public void MovingTheWholeSelectionKeepsItsSizeAndStaysOnTheMonitor()
    {
        var s = Drag(2600, 100, 2800, 150);

        Assert.AreEqual(new PixelRect(2610, 90, 200, 50), s.MoveBy(10, -10).Area);
        Assert.AreEqual(new PixelRect(2560, 0, 200, 50), s.MoveBy(-500, -500).Area);
        Assert.AreEqual(new PixelRect(4280, 1030, 200, 50), s.MoveBy(5000, 5000).Area);
    }

    [TestMethod]
    public void ClickWithoutDragIsEmpty()
    {
        Assert.IsTrue(SnapSelection.Start(Monitor, new PixelPoint(2600, 100)).IsEmpty);
        Assert.IsTrue(Drag(2600, 100, 2900, 100).IsEmpty);
        Assert.IsFalse(Drag(2600, 100, 2601, 101).IsEmpty);
    }

    [TestMethod]
    public void WholeMonitorSelectsItsBounds()
    {
        Assert.AreEqual(Monitor.Bounds, SnapSelection.WholeMonitor(Monitor).Area);
    }

    [TestMethod]
    public void AWindowReachingPastTheMonitorIsCutAtItsEdge()
    {
        Assert.AreEqual(new PixelRect(2560, 50, 340, 400), SnapSelection.Of(Monitor, new PixelRect(2400, 50, 500, 400)).Area);
        Assert.AreEqual(new PixelRect(2600, 50, 300, 400), SnapSelection.Of(Monitor, new PixelRect(2600, 50, 300, 400)).Area);
    }

    // Primary 2560×1440 at 0,0 and the 1920×1080 monitor right of it.
    private static readonly SnapMonitor Primary = new(new PixelRect(0, 0, 2560, 1440), 1.0);
    private static readonly ScreenLayout TwoMonitors = new([Primary, Monitor]);

    [TestMethod]
    public void RepeatingTakesTheSameAreaOnTheSameMonitor()
    {
        var last = new PixelRect(2600, 100, 200, 50);

        var again = SnapSelection.Repeat(last, TwoMonitors);

        Assert.AreEqual(last, again?.Area);
        Assert.AreEqual(Monitor, again?.Monitor);
    }

    [TestMethod]
    public void RepeatingAfterTheMonitorsChangedCutsTheAreaToWhatIsLeft()
    {
        // The last area was on a monitor that is now gone; it overlapped the primary one by 100 px.
        var last = new PixelRect(2460, 100, 600, 50);

        Assert.AreEqual(new PixelRect(2460, 100, 100, 50), SnapSelection.Repeat(last, new ScreenLayout([Primary]))?.Area);
        Assert.IsNull(SnapSelection.Repeat(new PixelRect(5000, 100, 50, 50), new ScreenLayout([Primary])));
    }
}
