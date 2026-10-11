# Changelog

## 1.4.2 — 2026-10-11

### Fixed

- The x64 and ARM64 downloads are built again; 1.4.1 was published without them. No other changes.

## 1.4.1 — 2026-10-11

### Added

- A Settings button (gear) in the title bar opens the Settings page. Its tooltip shows the current shortcut (`Ctrl+,` by default).
- The title bar shows the running version and short commit hash next to the logo (`1.4.0 · f69a251`). Click it to copy `1.4.0+f69a251`, for example for a bug report.

### Fixed

- Clicking between Panes no longer flashes a white focus frame around the selected entry. The frame still shows when you move focus with the keyboard.

## 1.4.0 — 2026-10-10

### Added

- **Copilot** (opt-in, Settings › Copilot): GitHub Copilot inside Pollux, built on the GitHub Copilot SDK. The bundled Copilot CLI runs as a separate process and is the only part that talks to GitHub; Pollux itself stays offline. It signs in with your existing `copilot login` / `gh auth` credentials, follows your organization's Copilot policies and is not available in the Admin Instance.
- **Copilot Panel** (`Ctrl+Shift+I`, *Copilot: Toggle Panel*): a Chat next to the Panes with streamed Markdown, diffs, approvals, model picker and premium-request quota. Chats are saved and can be resumed.
- **Context**: the active Pane's folder and selection go along as removable chips; add more with `@` mentions or drag and drop. Copilot selection actions are in the command palette.
- **Agent Mode** (Interactive, Plan, Autopilot) and **Permission Mode** (Manual, Assisted, Allow All) per Chat, plus a per-Chat sandbox toggle where supported.
- **Guard Rules** always apply: blocked and always-ask shell commands, protected secrets and Pollux's own data, a Working Folder, and a Tainted Chat marker once untrusted content (Downloads, Inbox, Temp, archives, web) is involved. Every tool call is recorded in the Chat's Action Log with decision and reason.
- Copilot tools for Pollux: read folders and selection, run file operations and manage Bookmarks, behind the Guard Rules.
- Settings for a Copilot-only proxy, extra CA certificates and a custom CLI path.

### Fixed

- The default shortcuts of *Downloads: Show Downloads Stack* (`Ctrl+Shift+O`) and *Notes: New Note* (`Ctrl+Alt+M`) work; they were shown in Settings but never bound.
- Every command run from the command palette moves to the top of its list, including Notes, Inbox, Clipboard and Copilot selection actions.
- Switching Tabs no longer carries the selection of the closed or moved Tab over to the next one.

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
