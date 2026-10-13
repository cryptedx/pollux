# WebView2 hosts the Note editor and the terminal

The Note editor is Monaco (VS Code's editor) and the embedded terminal is xterm.js plus a small own ConPTY wrapper, both running in a lazily created WebView2. Native WinUI text controls cannot deliver VS Code key behavior (multi-cursor, Ctrl+D, Alt+Up/Down), which is the whole point of replacing Notepad++. Both libraries are MIT-licensed, vendored into the repo and loaded from local files only (see ADR 0001); the WebView2 runtime ships with Windows 11.

## Consequences

- WebView2 is created on first use, never at startup, so it does not affect launch time.
- These are the first external dependencies; updates to them are manual and reviewed.
- WebView2 runs with a private profile in `%LocalAppData%\Pollux\WebView2`, Edge background networking switched off (`--disable-background-networking`, no component updates, no SmartScreen/domain reliability/pings, no DNS prefetch, WebRTC without non-proxied UDP), all navigation outside `https://pollux.assets/` blocked, and every other web request answered with 403. Downloads, default script dialogs and default context menus are off in every view.
- Quick Look and the Preview Pane show HTML, SVG, PDF and media in a separate file preview view with scripts and web messages off. Its request handler serves exactly the previewed file at `https://pollux.preview/<name>` (HTML and SVG with a CSP: no script, connect, frames or objects) and answers everything else with 404; no folder is mapped, so a previewed page cannot read the files next to it (preview policy in `Pollux.Core.Files.PreviewPolicy`).
- Monaco 0.57's AMD build loads its language workers eagerly, so the complete `min/vs` folder (~25 MB) is vendored; trimming it broke loading.

## Addendum: Snap Board

The Snap Board (`snap.html`, `WebViewKind.SnapBoard`) is a third own page in the same shared WebView2 environment, with the same policy. It marks up a Snap on an HTML canvas because XAML has no pixel-exact export of drawn shapes with blur and pixelation. The page vendors one library: jsQR 1.4.0 (npm `jsqr`, Apache-2.0; tarball checked against the registry's sha512 `dxLob7q6…/A==`, no network, `eval` or DOM use). It runs in a worker (`snap-qr.js`) on the Snap's own pixels. A found code is only shown and copied, never opened. The host answers exactly one extra address, `https://pollux.snap/image`, with the current Snap's PNG, for this view only (`WebViewPolicy.IsSnapImage`). It is its own host because WebView2 does not let the request handler answer addresses on the folder-mapped `pollux.assets`. The response allows `https://pollux.assets` by CORS, so the canvas stays exportable. The page returns the result as a base64 PNG in a web message, capped at 256 MB (`WebMessage.Bytes`). The view is created on the first Snap and then kept hidden while Pollux is resident.
