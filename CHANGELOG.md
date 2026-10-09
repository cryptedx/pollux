# Changelog

## 1.3.0 — 2026-10-09

### Added

- **Clipboard History** (opt-in, Settings › Clipboard). Pollux records the text, images and file lists you copy, encrypted with DPAPI on this PC only. Limits (size per Clip, at most 500 Clips or 30 days) are configurable; copies the source app marks as private (as most password managers do) and copies from processes on the exclusion list are not recorded; there is no other password-manager detection.
- **Clipboard Stack** (`Win+Alt+V`, or `--clipboard`, or the command palette): search Clips by text, file name or Kept Clip title (substring or word initials, substring matches first), filter by source app with `@app`, paste with `Enter` or only copy with `Shift+Enter`.
- **Kept Clips**: keep a Clip so it never rotates out, give it a title, and reorder Kept Clips with `Alt+↑/↓`. Clear History keeps them.
- **Win+V takeover** (opt-in): opens the Clipboard Stack instead of Windows' clipboard history. Turning it off, and `uninstall.ps1`, restore Windows' previous setting.
- **Ctrl+V creates a file**: in a folder, Ctrl+V with text or an image (and no files) on the clipboard creates a `.txt` or `.png` (can be turned off).
- Command palette: *Clipboard: Open Clipboard Stack*, *Clipboard: Clear History*, *Clipboard: Pause Capture*, *Clipboard: Resume Capture*.

### Fixed

- Clicking anywhere in a Pane makes it the active Pane (1.2.5).
- The tab bar keeps its height when the active Pane changes (1.2.5).
