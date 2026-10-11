namespace Pollux.Core.Files;

/// <summary>Pollux's WebView2 instances; each has its own origin allow-list and message source.</summary>
public enum WebViewKind
{
    /// <summary>xterm.js terminal (terminal.html).</summary>
    Terminal,

    /// <summary>Notes editor (notes.html).</summary>
    Notes,

    /// <summary>Monaco text view of Quick Look and the Preview Pane (preview.html).</summary>
    CodePreview,

    /// <summary>Copilot Panel (copilot.html).</summary>
    CopilotPanel,

    /// <summary>Sandboxed view of Quick Look and the Preview Pane for HTML, SVG, PDF and media.</summary>
    FilePreview,
}

/// <summary>
/// The web view policy (S-07, S-16; ADR 0001, ADR 0004): the process-wide browser arguments and, for each of Pollux's
/// web views, which navigations and requests are allowed and which web messages are its own. Pure decisions; the App's
/// WebView2 wiring only executes them.
/// </summary>
public static class WebViewPolicy
{
    /// <summary>
    /// Process-wide WebView2 browser arguments: Edge background networking off (ADR 0001, ADR 0004), WebRTC limited
    /// to proxied connections so no UDP leaves the machine, DNS prefetch off (S-07). All traffic, loopback included, goes
    /// to a dead proxy on 127.0.0.1:9: Chromium opens connections for cancelled frame navigations and speculative
    /// preconnects before any request filter sees them, so without a route out nothing reaches a host (S-07). Pollux's
    /// own pages come from virtual hosts and never touch the network. Chromium honours only the last
    /// <c>--disable-features</c>, so all features go into one.
    /// </summary>
    public static string BrowserArguments { get; } = string.Join(' ',
        "--disable-background-networking",
        "--disable-component-update",
        "--disable-domain-reliability",
        "--disable-sync",
        "--no-pings",
        "--disable-client-side-phishing-detection",
        "--dns-prefetch-disable",
        "--proxy-server=http://127.0.0.1:9",
        "--proxy-bypass-list=<-loopback>",
        "--force-webrtc-ip-handling-policy=disable_non_proxied_udp",
        "--disable-features=msSmartScreenProtection,msEdgeTranslate,AutofillServerCommunication,OptimizationHints,MediaRouter");

    /// <summary>The virtual host serving Pollux's own pages from Assets\web.</summary>
    public const string AssetsHost = "pollux.assets";

    private const string BlankPage = "https://pollux.assets/blank.html";

    /// <summary>
    /// True when a web message from <paramref name="source"/> (the posting document's URL) comes from the own page
    /// of <paramref name="view"/>. The file preview never accepts messages.
    /// </summary>
    public static bool IsOwnSource(string? source, WebViewKind view)
    {
        var page = view switch
        {
            WebViewKind.Terminal => "terminal.html",
            WebViewKind.Notes => "notes.html",
            WebViewKind.CodePreview => "preview.html",
            WebViewKind.CopilotPanel => "copilot.html",
            _ => null,
        };
        return page is not null
            && Uri.TryCreate(source, UriKind.Absolute, out var uri)
            && IsAssets(uri)
            && string.Equals(uri.AbsolutePath, "/" + page, StringComparison.Ordinal);
    }

    /// <summary>
    /// Whether <paramref name="view"/> may navigate (top level or any frame) to <paramref name="uri"/>: own views only
    /// to app assets, the file preview only to the file it shows (<paramref name="currentFile"/>) or the blank page.
    /// </summary>
    public static bool IsAllowedNavigation(string uri, WebViewKind view, string? currentFile)
    {
        if (uri.StartsWith("about:", StringComparison.OrdinalIgnoreCase)) return true;
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var u)) return false;
        if (view != WebViewKind.FilePreview) return IsAssets(u);
        return string.Equals(u.AbsoluteUri, BlankPage, StringComparison.OrdinalIgnoreCase) && IsAssets(u)
            || PreviewPolicy.Respond(uri, currentFile)?.Status == 200;
    }

    /// <summary>Whether a resource request of <paramref name="view"/> may proceed; everything else is answered with an error.</summary>
    public static bool IsAllowedRequest(string uri, WebViewKind view, string? currentFile) =>
        uri.StartsWith("blob:", StringComparison.OrdinalIgnoreCase)
        || uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
        || IsAllowedNavigation(uri, view, currentFile);

    private static bool IsAssets(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && uri.UserInfo.Length == 0
        && string.Equals(uri.Host, AssetsHost, StringComparison.OrdinalIgnoreCase);
}
