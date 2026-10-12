# Pollux

A keyboard-friendly, offline file manager for Windows 11, built for a developer who lives in VS Code and agentic coding tools.

## Navigation

**Pane**:
One of up to two side-by-side file views in a window; each Pane holds its own Tabs.
_Avoid_: Panel, column, side

**Compare**:
Marking each entry of the two Panes' folders as only there, newer, older, same or different. It only reads; the one-way sync built on it copies what is new or newer and never deletes.
_Avoid_: Diff, sync (for the marking alone)

**Tab**:
A single open location inside a Pane.
_Avoid_: Page, view

**View Mode**:
How a Tab lays out its entries: Details (rows with columns), Tiles or Large Icons (with thumbnails). Part of the Folder View; without per-folder views each Tab keeps its own.
_Avoid_: Layout, view (alone)

**Folder View**:
What a location remembers about how it is shown: View Mode, sort, grouping and Details columns. A location without one uses the nearest ancestor applied to subfolders, else the global settings.
_Avoid_: Folder settings, view state

**Bookmark**:
A location (folder or file) the user explicitly saved for fast access. Global unless it belongs to a Workspace.
_Avoid_: Favorite, pin, quick access, shortcut

**Recent**:
A location Pollux surfaces automatically because the user visited it recently or often; never created by hand.
_Avoid_: Frequent, history, MRU

**Home**:
The default view of a new Tab: Bookmarks, Recent and drives at a glance.
_Avoid_: Start page, This PC, dashboard

**Virtual Location**:
A location in a Tab that is not a folder on disk: a Zip Folder, a Server Location or a Shell Location. Read-only: its items can be opened and (except a server's shares) copied out, but nothing is pasted, dropped, renamed or deleted there.
_Avoid_: Virtual folder, special folder

**Zip Folder**:
A .zip file, or a folder inside one, browsed like a folder: Enter on a .zip goes in. Opening a file extracts it read-only to Pollux's temp folder; copying items out extracts them.
_Avoid_: Archive view, compressed folder

**Server Location**:
A server alone (`\\server`), listing its shared folders. Reached only by the user's own navigation, like Network (ADR 0010).
_Avoid_: Network share (for the list), host

**Shell Location**:
A folder of the Windows shell without a path of its own: This PC, Network, Control Panel, Libraries, a phone or camera. Listed, opened and copied from through Windows itself.
_Avoid_: Special folder, namespace extension

**Preview Handler**:
A Windows component registered for a file type (Office, mail, fonts) that draws the preview for the Preview Pane and Quick Look. It runs in a separate Windows process, never in Pollux, and not for files downloaded from the Internet.
_Avoid_: Previewer, viewer plugin

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

**Folder Search**:
A search through the current folder and all its subfolders by name, content text, date and size; its results take the place of the Pane's listing until it is closed.
_Avoid_: Find in files, deep search, recursive filter

**Transfer**:
One copy or move job run by Pollux's own queue, with progress, pause, cancel and a conflict rule.
_Avoid_: Operation, task, job (alone)

**Preview Pane**:
A permanent side area that shows the selected entry of the active Pane and follows the selection.
_Avoid_: Quick Look (that one is temporary)

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

**Recycle Bin**:
The Windows Recycle Bin opened as a location in a Tab. Read-only: its entries can only be restored or deleted permanently.
_Avoid_: Trash, wastebasket

**Downloads Stack**:
The compact popup listing the Downloads folder, opened from the tray without bringing up a full window.
_Avoid_: Downloads flyout, downloads popup

**Trusted Folder**:
A folder the user explicitly marked as trusted, including its subfolders; only there may Pollux run tools whose behaviour the folder's own content can change (such as git).
_Avoid_: Safe folder, whitelist, workspace trust

## Clipboard

**Clip**:
One item Pollux captured when something was copied: text, an image or a list of files.
_Avoid_: Entry, item (alone)

**Clipboard History**:
All Clips Pollux has captured, newest first; older Clips rotate out unless kept.
_Avoid_: Clipboard manager, clipboard log

**Kept Clip**:
A Clip the user chose to keep; it never rotates out of the Clipboard History.
_Avoid_: Pin, favorite, snippet

**Clipboard Stack**:
The popup opened from anywhere to search the Clipboard History and paste a Clip.
_Avoid_: Clipboard flyout, Win+V menu

## Privileges

**Admin Instance**:
A separately started, visibly marked Pollux window running with elevated rights, alongside the normal non-elevated Pollux.
_Avoid_: Admin mode, elevated mode, root mode

## Copilot

**Copilot**:
The GitHub Copilot agent inside Pollux, switched on by the user; it can read and act on files and Pollux itself.
_Avoid_: Assistant, AI, bot

**Chat**:
One ongoing exchange with Copilot, kept and resumable later. Global, not part of any Workspace.
_Avoid_: Session, conversation, thread

**Copilot Panel**:
The area docked at the side of a window where Chats are held.
_Avoid_: Copilot Pane, chat window, sidebar

**Agent Mode**:
How far Copilot goes on its own in a Chat: Interactive (works step by step with the user), Plan (only plans, does not act) or Autopilot (keeps working until the task is done).
_Avoid_: Mode (unqualified)

**Permission Mode**:
Which of Copilot's actions need the user's approval in a Chat: Manual (asks for each), Assisted (a second model approves what it judges safe) or Allow All (asks for nothing). Guard Rules apply in every Permission Mode.
_Avoid_: Auto mode, yolo

**Working Folder**:
The folder a Chat acts in: changes inside it follow the Chat's Permission Mode, changes outside it always need approval.
_Avoid_: Workspace (that is a saved working state), cwd, root

**Guard Rules**:
Pollux's own limits on Copilot that hold in every Permission Mode: blocked commands, commands that always ask, protected secrets, writes outside the Working Folder.
_Avoid_: Policy (that is the organisation's), denylist, filter

**Tainted Chat**:
A Chat that has read content from an untrusted place (Downloads, Inbox, web, archives, network shares and mapped network drives, cloud-synced folders such as OneDrive and SharePoint), also through a junction or symbolic link into one; it asks before every action that is not a plain read (navigating Pollux and tools without path arguments included) until the user clears it.
_Avoid_: Unsafe chat, quarantine
