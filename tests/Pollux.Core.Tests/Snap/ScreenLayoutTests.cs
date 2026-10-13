using Pollux.Core.Snap;

namespace Pollux.Core.Tests.Snap;

[TestClass]
public sealed class ScreenLayoutTests
{
    // A 150 % laptop (2880×1800) left of a 100 % primary monitor (2560×1440): the virtual screen starts at x = -2880.
    private static readonly SnapMonitor Laptop = new(new PixelRect(-2880, 200, 2880, 1800), 1.5);
    private static readonly SnapMonitor Primary = new(new PixelRect(0, 0, 2560, 1440), 1.0);
    private static readonly ScreenLayout Layout = new([Primary, Laptop]);

    [TestMethod]
    public void VirtualScreenSpansAllMonitors()
    {
        Assert.AreEqual(new PixelRect(-2880, 0, 5440, 2000), Layout.VirtualBounds);
    }

    [TestMethod]
    public void OverlayPointOnAScaledMonitorMapsToPhysicalPixels()
    {
        Assert.AreEqual(new PixelPoint(-2880 + 150, 200 + 75), Laptop.ToPhysical(100, 50));
        Assert.AreEqual(new PixelPoint(100, 50), Primary.ToPhysical(100, 50));
    }

    [TestMethod]
    public void PhysicalRectMapsIntoTheFrozenImage()
    {
        // The frozen image's pixel (0,0) is the top-left of the virtual screen.
        Assert.AreEqual(new PixelRect(150, 275, 300, 200), Layout.ToImage(new PixelRect(-2730, 275, 300, 200)));
        Assert.AreEqual(new PixelRect(2880, 0, 2560, 1440), Layout.ToImage(Primary.Bounds));
    }
}
