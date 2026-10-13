using Pollux.Core.Snap;

namespace Pollux.Core.Tests.Snap;

[TestClass]
public sealed class SnapColorTests
{
    [TestMethod]
    public void FrozenImagePixelIsWrittenAsHexInRgbOrder()
    {
        // The frozen image is BGRA: Windows accent blue #0F6CBD is stored as B=0xBD, G=0x6C, R=0x0F.
        Assert.AreEqual("#0F6CBD", SnapColor.ToHex(0xBD, 0x6C, 0x0F));
        Assert.AreEqual("#000000", SnapColor.ToHex(0, 0, 0));
        Assert.AreEqual("#FFFFFF", SnapColor.ToHex(255, 255, 255));
    }
}
