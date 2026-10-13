using Pollux.Core.Snap;

namespace Pollux.Core.Tests.Snap;

[TestClass]
public sealed class SnapFloatTests
{
    private const int Width = 400, Height = 200;

    [TestMethod]
    public void WheelZoomKeepsThePointUnderTheMouseInPlace()
    {
        var rect = new PixelRect(1000, 500, Width, Height);

        // One notch up: 110 %. The mouse at the Float's centre stays at 1200,600.
        Assert.AreEqual(new PixelRect(980, 490, 440, 220), SnapFloat.Zoom(rect, Width, Height, 1, new PixelPoint(1200, 600)));
        // At the top-left corner that corner stays put.
        Assert.AreEqual(new PixelRect(1000, 500, 440, 220), SnapFloat.Zoom(rect, Width, Height, 1, new PixelPoint(1000, 500)));
    }

    [TestMethod]
    public void ZoomStaysBetweenTenAndFourHundredPercent()
    {
        var rect = new PixelRect(0, 0, Width, Height);

        Assert.AreEqual(1600, SnapFloat.Zoom(rect, Width, Height, 100, new PixelPoint(0, 0)).Width);
        Assert.AreEqual(40, SnapFloat.Zoom(rect, Width, Height, -100, new PixelPoint(0, 0)).Width);
    }

    [TestMethod]
    public void OpacityMovesInTenPercentStepsBetweenTwentyAndHundred()
    {
        Assert.AreEqual(0.9, SnapFloat.Opacity(1.0, -1), 1e-9);
        Assert.AreEqual(0.2, SnapFloat.Opacity(0.3, -5), 1e-9);
        Assert.AreEqual(1.0, SnapFloat.Opacity(0.8, 7), 1e-9);
    }

    [TestMethod]
    public void AFloatOpensAtItsOwnSizeInTheMiddleOfTheMonitor()
    {
        var work = new PixelRect(0, 0, 2560, 1400);

        Assert.AreEqual(new PixelRect(1080, 600, 400, 200), SnapFloat.Place(Width, Height, work));
    }

    [TestMethod]
    public void ATooLargeFloatShrinksToEightyPercentOfTheWorkArea()
    {
        var work = new PixelRect(-1920, 0, 1920, 1000);

        // 3000×1000 fits 1536 wide (80 %), keeping 3:1.
        Assert.AreEqual(new PixelRect(-1920 + 192, 244, 1536, 512), SnapFloat.Place(3000, 1000, work));
    }
}
