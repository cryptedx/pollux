# WebView2 hosts the Note editor and the terminal

The Note editor is Monaco (VS Code's editor) and the embedded terminal is xterm.js plus a small own ConPTY wrapper, both running in a lazily created WebView2. Native WinUI text controls cannot deliver VS Code key behavior (multi-cursor, Ctrl+D, Alt+Up/Down), which is the whole point of replacing Notepad++. Both libraries are MIT-licensed, vendored into the repo and loaded from local files only (see ADR 0001); the WebView2 runtime ships with Windows 11.

## Consequences

- WebView2 is created on first use, never at startup, so it does not affect launch time.
- These are the first external dependencies; updates to them are manual and reviewed.
- WebView2 runs with a private profile in `%LocalAppData%\Pollux\WebView2`, Edge background networking switched off (`--disable-background-networking`, no component updates, no SmartScreen/domain reliability/pings, no DNS prefetch, WebRTC without non-proxied UDP), all navigation outside `https://pollux.assets/` blocked, and every other web request answered with 403. Downloads, default script dialogs and default context menus are off in every view.
- Quick Look and the Preview Pane show HTML, SVG, PDF and media in a separate file preview view with scripts and web messages off. Its request handler serves exactly the previewed file at `https://pollux.preview/<name>` (HTML and SVG with a CSP: no script, connect, frames or objects) and answers everything else with 404; no folder is mapped, so a previewed page cannot read the files next to it (preview policy in `Pollux.Core.Files.PreviewPolicy`).
- Monaco 0.57's AMD build loads its language workers eagerly, so the complete `min/vs` folder (~25 MB) is vendored; trimming it broke loading.
