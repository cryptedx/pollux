# WebView2 hosts the Note editor and the terminal

The Note editor is Monaco (VS Code's editor) and the embedded terminal is xterm.js plus a small own ConPTY wrapper, both running in a lazily created WebView2. Native WinUI text controls cannot deliver VS Code key behavior (multi-cursor, Ctrl+D, Alt+Up/Down), which is the whole point of replacing Notepad++. Both libraries are MIT-licensed, vendored into the repo and loaded from local files only (see ADR 0001); the WebView2 runtime ships with Windows 11.

## Consequences

- WebView2 is created on first use, never at startup, so it does not affect launch time.
- These are the first external dependencies; updates to them are manual and reviewed.
