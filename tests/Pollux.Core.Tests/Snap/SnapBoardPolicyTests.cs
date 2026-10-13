using Pollux.Core.Files;

namespace Pollux.Core.Tests.Snap;

[TestClass]
public sealed class SnapBoardPolicyTests
{
    [TestMethod]
    [DataRow("https://pollux.snap/image")]
    [DataRow("https://pollux.snap/image?n=7")]
    [DataRow("https://POLLUX.SNAP/image?n=7")]
    public void TheSnapBoardAsksForTheSnapAtItsOwnAddress(string uri) =>
        Assert.IsTrue(WebViewPolicy.IsSnapImage(uri, WebViewKind.SnapBoard));

    [TestMethod]
    [DataRow("https://pollux.snap/image", WebViewKind.Notes)]
    [DataRow("https://pollux.snap/image", WebViewKind.CopilotPanel)]
    [DataRow("https://pollux.snap/image", WebViewKind.FilePreview)]
    [DataRow("https://pollux.snap/image/../x", WebViewKind.SnapBoard)]
    [DataRow("https://pollux.snap/images", WebViewKind.SnapBoard)]
    [DataRow("https://pollux.assets/image", WebViewKind.SnapBoard)]
    [DataRow("https://pollux.snap.evil.example/image", WebViewKind.SnapBoard)]
    [DataRow("https://pollux.snap:8443/image", WebViewKind.SnapBoard)]
    [DataRow("http://pollux.snap/image", WebViewKind.SnapBoard)]
    public void NoOtherViewOrAddressGetsTheSnap(string uri, WebViewKind view) =>
        Assert.IsFalse(WebViewPolicy.IsSnapImage(uri, view));

    [TestMethod]
    public void OnlyTheSnapBoardMayRequestTheSnapHost()
    {
        Assert.IsTrue(WebViewPolicy.IsAllowedRequest("https://pollux.snap/image?n=1", WebViewKind.SnapBoard, null));
        Assert.IsFalse(WebViewPolicy.IsAllowedRequest("https://pollux.snap/image?n=1", WebViewKind.Notes, null));
        Assert.IsFalse(WebViewPolicy.IsAllowedNavigation("https://pollux.snap/image", WebViewKind.SnapBoard, null));
    }
}
