using System.Text;
using System.Text.Json;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;

namespace Pollux.App.Services;

/// <summary>
/// Sets up WebView2 for local-only pages (Monaco, xterm.js): files are served from Assets\web through a virtual host,
/// every other navigation is blocked and Edge's background networking is switched off (ADR 0001, ADR 0004).
/// </summary>
internal static class WebHost
{
    public const string Origin = "https://pollux.assets/";

    /// <summary>Local files shown by the preview (e.g. PDFs) are served from this host, mapped to the file's folder.</summary>
    public const string PreviewOrigin = "https://pollux.preview/";

    private static bool IsLocal(string uri) =>
        uri.StartsWith(Origin, StringComparison.OrdinalIgnoreCase) || uri.StartsWith(PreviewOrigin, StringComparison.OrdinalIgnoreCase) || uri.StartsWith("about:", StringComparison.OrdinalIgnoreCase);
    private static bool _configured;

    private static void ConfigureEnvironment()
    {
        if (_configured) return;
        _configured = true;
        var data = Path.Combine(Core.PolluxPaths.Current().DataDirectory, "WebView2");
        Environment.SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER", data);
        Environment.SetEnvironmentVariable("WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS", string.Join(' ',
            "--disable-background-networking",
            "--disable-component-update",
            "--disable-domain-reliability",
            "--disable-sync",
            "--no-pings",
            "--disable-client-side-phishing-detection",
            "--disable-features=msSmartScreenProtection,msEdgeTranslate,AutofillServerCommunication,OptimizationHints,MediaRouter"));
    }

    public static async Task<CoreWebView2> InitializeAsync(WebView2 view, string page)
    {
        ConfigureEnvironment();
        await view.EnsureCoreWebView2Async();
        var core = view.CoreWebView2;
        core.SetVirtualHostNameToFolderMapping("pollux.assets", Path.Combine(AppContext.BaseDirectory, "Assets", "web"), CoreWebView2HostResourceAccessKind.Allow);
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
        core.Settings.IsPasswordAutosaveEnabled = false;
        core.Settings.IsGeneralAutofillEnabled = false;
        core.NavigationStarting += (_, e) =>
        {
            if (!IsLocal(e.Uri)) e.Cancel = true;
        };
        core.NewWindowRequested += (_, e) => e.Handled = true;
        core.WebResourceRequested += (_, e) =>
        {
            // Defence in depth: anything not from the local virtual host is answered with 403.
            if (!IsLocal(e.Request.Uri) && !e.Request.Uri.StartsWith("blob:", StringComparison.OrdinalIgnoreCase) && !e.Request.Uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                e.Response = core.Environment.CreateWebResourceResponse(null, 403, "Blocked", "");
        };
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
        core.Navigate(Origin + page);
        return core;
    }

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
