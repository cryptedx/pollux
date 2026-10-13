using Pollux.Core.Files;

namespace Pollux.Core.Tests.Snap;

[TestClass]
public sealed class SnapBoardMessageTests
{
    private const string Board = "https://pollux.assets/snap.html";

    private static WebMessage Export(string png) =>
        WebMessage.Read(Board, $$"""{"type":"export","action":"copy","png":"{{png}}"}""", WebViewKind.SnapBoard)!;

    [TestMethod]
    public void ExportedImageIsDecoded()
    {
        // "iVBORw0KGgo=" is the 8-byte PNG signature.
        CollectionAssert.AreEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, Export("iVBORw0KGgo=").Bytes("png", 1024));
    }

    [TestMethod]
    public void BrokenOrMissingImageIsNull()
    {
        Assert.IsNull(Export("not base64!").Bytes("png", 1024));
        Assert.IsNull(Export("").Bytes("png", 1024));
        Assert.IsNull(Export("iVBORw0KGgo=").Bytes("missing", 1024));
        Assert.IsNull(WebMessage.Read(Board, """{"type":"export","png":42}""", WebViewKind.SnapBoard)!.Bytes("png", 1024));
    }

    [TestMethod]
    public void ImageLargerThanTheLimitIsNull()
    {
        Assert.IsNull(Export("iVBORw0KGgo=").Bytes("png", 7));
        Assert.IsNotNull(Export("iVBORw0KGgo=").Bytes("png", 8));
    }
}
