# Windows preview handlers show Office documents and other registered types

Quick Look and the Preview Pane show Office documents, mails (`.msg`, `.eml`), Visio drawings, fonts and any other type with a registered Windows preview handler (IPreviewHandler) the way Explorer's preview pane does. Pollux's own viewers keep images, HTML/SVG/PDF/media and text/code (`PreviewPolicy.PrefersShellHandler`); the handler for everything else is looked up like Explorer does (user choice, ProgID, extension, `SystemFileAssociations`, perceived type; `ShellPreviewHandlers`). `preview.useShellHandlers` (default on) switches it off.

Handlers are third-party code, so Pollux only creates them with `CLSCTX_LOCAL_SERVER`: they run in Windows' prevhost.exe surrogate, never in the Pollux process (a crashing handler cannot take Pollux down, and no foreign DLL is loaded into the Native AOT process). Handlers that only work in process (no AppID or local server, or `DisableProcessIsolation`) are not used. The handler gets a read-only stream over the file where it supports one (else the path or a shell item), runs on a dedicated STA thread that pumps messages, and has ten seconds to show the file; a hung call is cancelled with `CoCancelCall`, that handler is skipped for the rest of the session and Pollux's own preview is shown instead.

WinUI 3 cannot draw XAML over a child HWND (airspace), and handlers draw into an HWND. The handler's window is therefore a Win32 child window of the main window kept over the preview area's bounds in physical pixels, and hidden whenever anything else is drawn over the area.

## Considered Options

- Converting documents ourselves (e.g. Office to PDF or HTML): needs Office automation or a document library per format; slow, large and still incomplete.
- Loading handlers in process: simpler hosting and no cross-process stream, but third-party DLLs (often in a different runtime, some unstable) inside Pollux, and Explorer itself isolates them.
- A separate Pollux helper process hosting the handlers: isolation like prevhost, but a second binary to ship and keep AOT-clean for what prevhost already does.

## Consequences

- When the user previews a file, code from the handler's vendor runs in prevhost.exe with the user's rights; it may open the document's linked content. Pollux itself still opens no network connections (ADR 0001); like Explorer since its 2025 hardening, files with a zone mark from the Internet or Restricted zone are never handed to a handler, and the Admin Instance never uses handlers (prevhost would run elevated).
- The handler window sits above all XAML. It is hidden while the area is collapsed, covered (Quick Look over the pane, command palette, dialogs, flyouts; detected by hit-testing nine points of the area and by open popups) or the window is minimised. Covered partly means hidden entirely; tooltips over the area are ignored. XAML rounded corners and borders of the area are not clipped into the handler window.
- Keyboard focus moves into prevhost's window when the user clicks into the preview. The frame (IPreviewHandlerFrame) passes Escape back, which returns focus to Pollux; other Pollux shortcuts do not work until the user clicks back.
- First previews are slow (prevhost and the vendor's viewer start, Office in particular); the area shows "Loading preview…" meanwhile, and arrowing on cancels nothing already running but drops what is stale.
- Theme: background and text colour are passed through IPreviewHandlerVisuals; handlers that ignore it (most Office ones) draw their own light background.
