# Pollux

A fast, offline, keyboard-friendly file manager for Windows 11, built with WinUI 3, .NET and Native AOT.
It is made for developers who work in VS Code and with agentic coding tools.

![Pollux: dual pane, bookmarks, embedded terminal, status bar](docs/images/pollux.png)

- **Instant.** Pollux keeps running in the tray (optional), so a new window takes about 50 ms. A cold start takes about 400 ms.
- **Offline.** Pollux makes no network connections: no telemetry, no update pings, no cloud ([ADR 0001](docs/adr/0001-pollux-never-opens-network-connections.md)). The only exception is `winget` in the Updates view, and only if you start it.
- **No admin.** Pollux installs per user and runs without elevation. An Admin Instance is available when you need it (it asks for consent through UAC or Delinea).

## Features

| | |
|---|---|
| **Dual pane + tabs** | Two Panes, each with its own Tabs. `Ctrl+<` toggles dual pane, `Ctrl+1/2` or `Tab` switches pane, `F5`/`F6` copies or moves to the other pane |
| **Bookmarks** | Grouped and reorderable. `Ctrl+D` adds the current folder, `Ctrl+Shift+1…9` jumps to a bookmark |
| **Quick Search** | `Ctrl+P` does a fuzzy search over an in-memory index of your Index Roots (bookmarks, repos, Downloads, Desktop, Documents and Notes). `node_modules`, `.git`, `bin` and `obj` are skipped |
| **Filter** | `Ctrl+F` filters the current folder live by substring or word initials (`dbp` → `Directory.Build.props`) |
| **Command palette** | `Ctrl+Shift+P` runs any command. All keys can be remapped in `keybindings.json` (VS Code format) or on the Settings page |
| **Embedded terminal** | `Ctrl+ö` toggles a PowerShell, pwsh or cmd panel (ConPTY + xterm.js) that opens in the active folder. `Ctrl+Alt+T` opens Windows Terminal instead |
| **Quick Look** | `Space` previews the selection: images, code with syntax highlighting (Monaco), PDFs, a hex view for binaries, and a summary for folders |
| **Notes** | Plain Markdown files edited with Monaco, so VS Code keys work, including multi-cursor. Notes are saved automatically and named after their first line. Tags come from `#tags` or front matter. `Win+Alt+N` creates a new note from anywhere |
| **Downloads Stack** | Left-click the tray icon or press `Win+Alt+D` to see your newest downloads, like a macOS Dock stack. You can drag files out of it |
| **Workspaces** | Save a window's panes, tabs, Workspace Bookmarks and terminal folder, then switch with `Ctrl+Alt+W` or `pollux --workspace NAME` |
| **Inbox** | Treat the Desktop as an Inbox: hide its icons, then sort it into `Documents\Inbox\<category>` (with a preview first) |
| **App updates** | A `winget upgrade` list where you pick which updates to install, either normally or as admin. Automatic checks are opt-in |
| **Status bar** | CPU, GPU, VRAM, RAM and free space on the current drive. It refreshes only while a window is visible |
| **Git aware** | Shows the current branch in the path bar and in Quick Look |
| **Hand-offs** | Open in VS Code (`Ctrl+Shift+E`), open in the GitHub Copilot app (configurable command) and copy paths (plain, quoted or with `/`) |
| **Win+E** | Optional: open Pollux instead of Explorer with Win+E |

## Install

1. Download `Pollux-win-x64.zip` (or `-arm64`) from the [releases](../../releases), or build it yourself (see below).
2. Unzip it, then run this from the unzipped folder:

```powershell
powershell -ExecutionPolicy Bypass -File .\install.ps1
```

Pollux is copied to `%LocalAppData%\Programs\Pollux` and gets a Start menu shortcut. To remove it, run `uninstall.ps1` from that folder (add `-RemoveData` to also delete settings and state; notes are kept).

## Configuration

Everything Pollux uses lives in a few readable files:

| File | Contents |
|---|---|
| `%AppData%\Pollux\settings.json` | All settings (JSONC). The Settings page (`Ctrl+,`) edits the same file, and changes apply immediately |
| `%AppData%\Pollux\keybindings.json` | `{ "key": "ctrl+n", "command": "tab.new" }`; a `-command` entry removes a default |
| `%AppData%\Pollux\bookmarks.json`, `workspaces\*.json` | Bookmarks and Workspaces |
| `%LocalAppData%\Pollux\` | Recent, session, the WebView2 profile and logs (`startup.log` has the startup timings, `errors.log` has exceptions) |
| `Documents\Pollux\Notes\` | Your notes as `.md` files (the folder is configurable) |

## Command line

```text
Pollux.exe [folder|file]           open a window at a folder, or with the file selected
Pollux.exe --workspace "Name"      open a Workspace
Pollux.exe --command id[,id…]      run commands in the frontmost window (e.g. view.toggleDual)
Pollux.exe --notes | --downloads   open a new note / the Downloads Stack
Pollux.exe --background            start in the tray only (used for autostart)
Pollux.exe --quit                  stop the background process
```

## Build

Requirements: Windows 11, the .NET 10 SDK and Visual Studio 2026 with the *WinUI application development* and *Desktop development with C++* workloads (Native AOT links with the C++ toolchain).

```powershell
dotnet test --project tests/Pollux.Core.Tests
dotnet publish src/Pollux.App -c Release -p:Platform=x64 -r win-x64 -o artifacts/win-x64
```

`Pollux.Core` has no Windows dependency, so its tests also run on macOS and Linux. Pushing a tag `v*` builds release zips for x64 and ARM64.

Domain language: [GLOSSARY.md](GLOSSARY.md). Decisions: [docs/adr](docs/adr). Spec and tickets: [.scratch/v1-core](.scratch/v1-core).

## Third-party code

| Component | License | Used for |
|---|---|---|
| Windows App SDK (WinUI 3, WebView2) | MIT / Microsoft | UI |
| [Monaco Editor](https://github.com/microsoft/monaco-editor) 0.57.0 | MIT | Notes editor, code preview (bundled in `Assets/web/monaco`, loaded from disk only) |
| [xterm.js](https://github.com/xtermjs/xterm.js) 6.0.0 + addon-fit | MIT | Embedded terminal (bundled in `Assets/web/xterm`) |
