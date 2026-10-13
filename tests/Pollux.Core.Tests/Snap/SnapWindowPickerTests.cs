using Pollux.Core.Snap;

namespace Pollux.Core.Tests.Snap;

[TestClass]
public sealed class SnapWindowPickerTests
{
    private static SnapWindow Window(nint handle, int x, int y, int w, int h, bool visible = true, bool minimized = false, bool cloaked = false) =>
        new(handle, new PixelRect(x, y, w, h), visible, minimized, cloaked);

    [TestMethod]
    public void TheTopmostWindowUnderThePointerIsPicked()
    {
        var windows = new[] { Window(1, 100, 100, 300, 200), Window(2, 0, 0, 1920, 1080) };

        Assert.AreEqual((nint)1, SnapWindowPicker.At(new PixelPoint(150, 150), windows, [])?.Handle);
        Assert.AreEqual((nint)2, SnapWindowPicker.At(new PixelPoint(50, 50), windows, [])?.Handle);
    }

    [TestMethod]
    public void HiddenMinimizedCloakedAndEmptyWindowsAreSkipped()
    {
        var windows = new[]
        {
            Window(1, 0, 0, 500, 500, visible: false),
            Window(2, 0, 0, 500, 500, minimized: true),
            Window(3, 0, 0, 500, 500, cloaked: true),
            Window(4, 0, 0, 0, 0),
            Window(5, 0, 0, 500, 500),
        };

        Assert.AreEqual((nint)5, SnapWindowPicker.At(new PixelPoint(10, 10), windows, [])?.Handle);
    }

    [TestMethod]
    public void PolluxOwnOverlaysAreSkipped()
    {
        var windows = new[] { Window(9, 0, 0, 1920, 1080), Window(5, 0, 0, 500, 500) };

        Assert.AreEqual((nint)5, SnapWindowPicker.At(new PixelPoint(10, 10), windows, new HashSet<nint> { 9 })?.Handle);
    }

    [TestMethod]
    public void NothingUnderThePointerGivesNull()
    {
        Assert.IsNull(SnapWindowPicker.At(new PixelPoint(900, 900), [Window(1, 0, 0, 500, 500)], []));
    }
}
