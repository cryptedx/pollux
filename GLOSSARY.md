# Pollux

A keyboard-friendly, offline file manager for Windows 11, built for a developer who lives in VS Code and agentic coding tools.

## Navigation

**Pane**:
One of up to two side-by-side file views in a window; each Pane holds its own Tabs.
_Avoid_: Panel, column, side

**Tab**:
A single open location inside a Pane.
_Avoid_: Page, view

**Bookmark**:
A location (folder or file) the user explicitly saved for fast access. Global unless it belongs to a Workspace.
_Avoid_: Favorite, pin, quick access, shortcut

**Recent**:
A location Pollux surfaces automatically because the user visited it recently or often; never created by hand.
_Avoid_: Frequent, history, MRU

**Home**:
The default view of a new Tab: Bookmarks, Recent and drives at a glance.
_Avoid_: Start page, This PC, dashboard

**Workspace**:
A saved working state the user switches between: Pane layout, open Tabs per Pane, Workspace-scoped Bookmarks and the terminal start folder.
_Avoid_: Session, project, profile

## Finding

**Filter**:
Narrowing the entries of the current location as you type.
_Avoid_: Search (when only the current location is meant)

**Quick Search**:
Fuzzy lookup of files and folders across all Index Roots, opened from anywhere.
_Avoid_: Global search, find

**Quick Look**:
A temporary full-size preview of the selected entry, opened and closed with Space.
_Avoid_: Preview pane, viewer

**Index Root**:
A folder whose contents Pollux keeps indexed for Quick Search.
_Avoid_: Search scope, watched folder

## Places

**Note**:
A plain Markdown file in the Notes folder, edited inside Pollux and saved automatically. Global, not part of any Workspace.
_Avoid_: Memo, scratch, snippet

**Tag**:
A label on a Note, written in the Note itself (front matter or inline `#tag`). Only Notes carry Tags.
_Avoid_: Label, category, keyword

**Inbox**:
The Pollux view of the Windows Desktop folder, treated as a landing zone to be emptied rather than a place to keep things.
_Avoid_: Desktop (when meaning the Pollux view)

**Downloads Stack**:
The compact popup listing the Downloads folder, opened from the tray without bringing up a full window.
_Avoid_: Downloads flyout, downloads popup

## Privileges

**Admin Instance**:
A separately started, visibly marked Pollux window running with elevated rights, alongside the normal non-elevated Pollux.
_Avoid_: Admin mode, elevated mode, root mode
