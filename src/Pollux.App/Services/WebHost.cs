using System.Text;
using System.Text.Json;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Pollux.Core.Files;

namespace Pollux.App.Services;

/// <summary>
/// Sets up WebView2 for local-only pages (Monaco, xterm.js): files are served from Assets\web through a virtual host,
/// every other navigation is blocked and Edge's background networking is switched off (ADR 0001, ADR 0004).
/// Each view has its own allow-list from the preview policy; the file preview serves exactly one file (S-07, S-16).
/// </summary>
internal static class WebHost
{
    public const string Origin = "https://pollux.assets/";

    private static Task<CoreWebView2Environment>? _environment;

    /// <summary>
    /// The one WebView2 environment all views share: Pollux's own profile folder and the policy's browser arguments.
    /// They are passed as options, not as WEBVIEW2_* environment variables, which every program Pollux starts would
    /// inherit (a WebView2 app opened from Pollux would get its profile and its dead proxy). UI thread only.
    /// </summary>
    private static Task<CoreWebView2Environment> SharedEnvironment() =>
        _environment ??= CoreWebView2Environment.CreateWithOptionsAsync(
            "",
            Path.Combine(Core.PolluxPaths.Current().DataDirectory, "WebView2"),
            new CoreWebView2EnvironmentOptions { AdditionalBrowserArguments = WebViewPolicy.BrowserArguments }).AsTask();

    /// <summary>Creates one of Pollux's own pages (terminal, notes, editor, Copilot Panel).</summary>
    public static Task<CoreWebView2> InitializeAsync(WebView2 view, string page, WebViewKind kind) =>
        InitializeAsync(view, page, kind, null, null);

    /// <summary>Creates the Snap Board; its request handler answers https://pollux.snap/image with the PNG <paramref name="snap"/> returns.</summary>
    public static Task<CoreWebView2> InitializeSnapBoardAsync(WebView2 view, Func<byte[]?> snap) =>
        InitializeAsync(view, "snap.html", WebViewKind.SnapBoard, null, snap);

    /// <summary>
    /// Creates the sandboxed file preview: scripts and web messages off, and its request handler serves only the
    /// file <paramref name="currentFile"/> returns at request time, with the preview policy's CSP. No folder is mapped.
    /// </summary>
    public static Task<CoreWebView2> InitializeFilePreviewAsync(WebView2 view, Func<string?> currentFile) =>
        InitializeAsync(view, "blank.html", WebViewKind.FilePreview, currentFile, null);

    private static async Task<CoreWebView2> InitializeAsync(WebView2 view, string page, WebViewKind kind, Func<string?>? currentFile, Func<byte[]?>? snap)
    {
        await view.EnsureCoreWebView2Async(await SharedEnvironment());
        var core = view.CoreWebView2;
        core.SetVirtualHostNameToFolderMapping(WebViewPolicy.AssetsHost, Path.Combine(AppContext.BaseDirectory, "Assets", "web"), CoreWebView2HostResourceAccessKind.Allow);
        var settings = core.Settings;
        settings.AreDevToolsEnabled = false;
        settings.IsStatusBarEnabled = false;
        settings.AreBrowserAcceleratorKeysEnabled = false;
        settings.IsZoomControlEnabled = false;
        settings.IsPasswordAutosaveEnabled = false;
        settings.IsGeneralAutofillEnabled = false;
        settings.AreDefaultScriptDialogsEnabled = false;
        settings.AreDefaultContextMenusEnabled = false;
        settings.AreHostObjectsAllowed = false;
        if (kind == WebViewKind.FilePreview)
        {
            settings.IsScriptEnabled = false;
            settings.IsWebMessageEnabled = false;
        }

        string? Shown() => currentFile?.Invoke();
        core.NavigationStarting += (_, e) =>
        {
            if (!WebViewPolicy.IsAllowedNavigation(e.Uri, kind, Shown())) e.Cancel = true;
        };
        core.FrameNavigationStarting += (_, e) =>
        {
            if (!WebViewPolicy.IsAllowedNavigation(e.Uri, kind, Shown())) e.Cancel = true;
        };
        core.NewWindowRequested += (_, e) => e.Handled = true;
        core.DownloadStarting += (_, e) =>
        {
            e.Cancel = true;
            e.Handled = true;
        };
        core.WebResourceRequested += (_, e) =>
        {
            var uri = e.Request.Uri;
            if (snap is not null && WebViewPolicy.IsSnapImage(uri, kind))
            {
                var png = snap();
                e.Response = png is null
                    ? core.Environment.CreateWebResourceResponse(null, 404, "Not Found", "")
                    : core.Environment.CreateWebResourceResponse(new MemoryStream(png, writable: false).AsRandomAccessStream(), 200, "OK", "Content-Type: image/png\r\nCache-Control: no-store\r\nAccess-Control-Allow-Origin: https://pollux.assets");
                return;
            }

            if (kind == WebViewKind.FilePreview)
            {
                var headers = e.Request.Headers;
                var range = headers.Contains("Range") ? headers.GetHeader("Range") : null;
                var content = PreviewPolicy.Open(uri, Shown(), range);
                if (content is not null)
                {
                    // WebView2 reads the stream after the handler returns and releases it when done.
                    e.Response = core.Environment.CreateWebResourceResponse(content.Body?.AsRandomAccessStream(), content.Status, content.Reason, content.Headers);
                    return;
                }
            }

            // Defence in depth: anything outside this view's allow-list is answered with 403.
            if (!WebViewPolicy.IsAllowedRequest(uri, kind, Shown()))
                e.Response = core.Environment.CreateWebResourceResponse(null, 403, "Blocked", "");
        };
        // All source kinds: requests from workers and frames go through the same filter as the document's.
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
        core.Navigate(Origin + page);
        return core;
    }

    /// <summary>
    /// The string <paramref name="e"/> carries, or null when it was not posted by <paramref name="kind"/>'s own page
    /// (preview policy) or is not a string (S-16). Handlers never act on messages from foreign frames or documents.
    /// </summary>
    public static string? OwnMessageText(CoreWebView2WebMessageReceivedEventArgs e, WebViewKind kind)
    {
        if (!WebViewPolicy.IsOwnSource(e.Source, kind)) return null;
        try { return e.TryGetWebMessageAsString(); }
        catch (ArgumentException) { return null; } // a posted object or number instead of a JSON string
    }

    /// <summary>The message from <paramref name="kind"/>'s own page, parsed defensively; null for anything else (S-16).</summary>
    public static WebMessage? Receive(CoreWebView2WebMessageReceivedEventArgs e, WebViewKind kind) =>
        WebMessage.Read(e.Source, OwnMessageText(e, kind), kind);

    /// <summary>Builds a small JSON message without reflection (AOT-safe).</summary>
    public static string Json(params (string Key, object? Value)[] fields)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            foreach (var (k, v) in fields)
            {
                switch (v)
                {
                    case null: w.WriteNull(k); break;
                    case string s: w.WriteString(k, s); break;
                    case bool b: w.WriteBoolean(k, b); break;
                    case int i: w.WriteNumber(k, i); break;
                    case double d: w.WriteNumber(k, d); break;
                    case IEnumerable<string> list:
                        w.WriteStartArray(k);
                        foreach (var x in list) w.WriteStringValue(x);
                        w.WriteEndArray();
                        break;
                    default: w.WriteString(k, v.ToString()); break;
                }
            }

            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(ms.ToArray());
    }
}
