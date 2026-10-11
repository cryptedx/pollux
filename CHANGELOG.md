# Changelog

## 1.5.3 — 2026-10-11

### Fixed

- The x64 and ARM64 downloads are built again: the ARM64 release build picked the build machine's architecture and failed, so 1.5.0 to 1.5.2 were published without downloads. No other changes.

## 1.5.2 — 2026-10-11

### Fixed

- The x64 and ARM64 downloads are built again; 1.5.1 was published without them. No other changes.

## 1.5.1 — 2026-10-11

### Fixed

- The Copilot Panel no longer blames your organization's Copilot policy when the Copilot CLI refuses a Permission Mode for another reason (for example Assisted while your Copilot quota is used up). It now says the CLI refused the mode; the policy is only named when the CLI reports it or the managed settings block the mode.

## 1.5.0 — 2026-10-11

### Security

- All findings of the 2026-10-11 security review (SECURITY.md S-01 to S-20) are fixed or documented.
- git runs only in **Trusted Folders** (path bar action *Trust this folder*, `files.trustedFolders`; existing Bookmarks with repos are trusted once); elsewhere the branch is read from `HEAD`. Repos whose config defines filters, drivers or includes are refused, and `.git` files pointing to network paths are ignored.
- External tools (git, winget, VS Code, terminals, helpers) start by absolute path, never from the browsed folder; `;` and `%` in folder names are escaped.
- HTML and SVG previews run without scripts and only see the previewed file; web messages and downloads are restricted on all web views.
- Copilot Guard Rules canonicalise device, admin-share and `::$DATA` paths, protect more credential files, ask for unanalysable shell, DNS, persistence and environment reads, and treat `pollux_navigate` as a read. A Tainted Chat covers network and cloud-synced places and asks before all non-reads. Prompts are refused while the sandbox or mode policy is unconfirmed.
- Copies, moves and extraction keep the Mark-of-the-Web and EFS encryption; extraction asks before a zip bomb and refuses to write through links.
- The Admin Instance ignores command settings and forwarded requests, starts tools only from the system directory or Program Files and warns from user-writable folders; new `install.ps1 -AllUsers` per-machine install.
- Releases include `SHA256SUMS` and build attestations; CI actions are pinned, NuGet restores are locked and the bundled Copilot CLI hash is checked.

### Added

- Optional Details columns (Created, Attributes, Extension): right-click the column header to toggle them (`files.columns`); all sort. *View: Cycle Grouping* (`files.groupBy`) groups the list by Type or Modified date (Today, Yesterday, This week, This month, Earlier).
- *File: Compress to Zip*, *Extract Zip to New Folder* and *Extract Zip Here* (command palette) work in the background with a progress toast and can be cancelled; extraction refuses entries that would escape the target folder (zip-slip) and never overwrites files.
- **View: Calculate Folder Sizes** (`Alt+S`) computes the size of every folder in the listing in the background and shows it in the Size column, so folders can be sorted by size too. It stops when you navigate away. With `files.autoFolderSizes` it runs automatically on local drives.
- *File: Batch Rename Selection…* (`Ctrl+M`) renames 2 or more selected items at once: find/replace (plain or regex), lower/UPPER/Title case and numbering with a template like `{name}_{n:000}{ext}`, with a live old → new preview. Applying is blocked while names collide, already exist or are invalid; swapped names are handled safely.
- Create links: *File: Create Shortcut (.lnk) Here*, *File: Create Symbolic Link (in Other Pane)* and *File: Create Junction (in Other Pane)* for the selection. Shortcuts, symlinks and junctions show their target as a tooltip on the name. Symlinks need Developer Mode or an elevated Pollux.
- **Undo and redo** of file operations (`Ctrl+Z` / `Ctrl+Y` in the file list, *Edit: Undo File Operation* / *Edit: Redo File Operation*): rename, new file or folder, copy, move (F5/F6, cut and paste, drag and drop, also with the Windows engine and Copilot's file tools) and delete to the Recycle Bin, which is restored to the original place. Undo refuses, with the reason in the status message, when the disk changed meanwhile (an entry is gone or was edited, the original place is taken). A permanent delete cannot be undone.
- **View modes** per Tab: *Details* (`Ctrl+Alt+1`), *Tiles* (`Ctrl+Alt+2`) and *Large Icons* (`Ctrl+Alt+3`) with real Windows thumbnails, loaded in the background only for the entries on screen; *View: Cycle View Mode* in the command palette. Tabs remember their mode across restarts; `files.viewMode` sets the default.
- Selection commands: Select None (`Ctrl+Shift+A`), Invert Selection (`Ctrl+I`), and Select / Deselect by Pattern (wildcards like `*.log;*.tmp`, case-insensitive; palette only).
- **Folder Search** (`Ctrl+Shift+F`, *Search: Search in Current Folder and Subfolders*): find files and folders below the current folder by name (substring or `*.ext`), content text, date (`after:7d`, `before:2026-01-31`) and size (`size>1mb`). Results stream into the Pane with paths relative to the folder; `Esc` cancels, then closes, and `Ctrl+Enter` shows a result in its folder. Skips `search.excludeNames`, binaries and files over `search.maxContentFileMegabytes` (10 MB) when looking for text.
- **Compare folders** (`Ctrl+Alt+D`, *Pane: Compare Folders of Both Panes*): marks every row of both Panes as only left/right, newer, older, same or differs (by name, size and modified time; optionally subfolders and same-size file content, see `compare` in settings.json) with a summary in the status bar. *Pane: Select Differences to Other Pane* selects what `F5` would bring over; *Pane: Sync New and Newer Items to Other Pane…* copies it through the Transfer queue after a confirmation and never deletes.
- Drive management in the sidebar's Drives section (right-click) and the command palette: *Drive: Map Network Drive…* (Windows asks for the credentials), *Disconnect* for network drives and *Eject* for removable and optical drives.
- **Recycle Bin** as a location (sidebar, *Go: Recycle Bin*): lists recycled entries with original name, original location, date deleted and size. *Recycle Bin: Restore Selected* puts them back, `Delete` or *Recycle Bin: Delete Selected Permanently…* removes them for good and *Recycle Bin: Empty…* empties the bin (both ask first); other file commands are off there.

### Fixed

- The Copilot Panel works again: its script was blocked by the page's Content Security Policy (no dark mode, Send and Enter did nothing), and sending a message failed silently because the active folder was read off the UI thread. Errors of Chat actions now show in the Chat instead of being lost.
- Batch rename is off for Folder Search results (it built the paths from the search folder and could rename other files); the rest of the batch rename is now one undo step (`Ctrl+Z`).
- Undoing a copy no longer sends a whole copied folder to the Recycle Bin: only the files the copy made go (while unchanged), and the folder only if it is empty then, so files you saved into it since stay.
- Compress to Zip no longer follows junctions and directory symlinks (huge or looping trees) and skips folders it cannot read instead of failing.
- Shortcut (.lnk) tooltips no longer leak a COM object per row, are remembered until the file changes and are not read from network shares.
- Folder sizes and compare marks appear in the rows in place: the list no longer jumps back to the selection while you scroll. A list sorted by size is put in order once all sizes are known.
- Compare by content does not read cloud-only OneDrive files any more (that would download them); size and time decide for those.
- Restore, Delete Permanently and Empty in the Recycle Bin no longer freeze the window.
- Closing a window cancels its running zip job.
- Select None, Invert Selection and Select / Deselect by Pattern work in the Recycle Bin.
- Thumbnails of files on a mapped network drive show the file type icon instead of reading the file over the network.

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
