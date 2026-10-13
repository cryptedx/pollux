using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Pollux.App.Interop;
using Pollux.Core.Commands;

namespace Pollux.App.Services;

/// <summary>
/// Hidden message-only window that owns the tray icon and global hotkeys of the resident process (ADR 0003).
/// </summary>
internal sealed unsafe class TrayHost : IDisposable
{
    private const uint TrayCallback = Native.WM_APP + 1;
    private const int MenuNewWindow = 1, MenuDownloads = 2, MenuNotes = 3, MenuSettings = 4, MenuUpdates = 5, MenuClipboardPause = 6, MenuQuit = 9;
    private const int MenuSnapArea = 20, MenuSnapWindow = 21, MenuSnapScreen = 22, MenuSnapDelay = 23, MenuSnapHistory = 24, MenuSnapRelease = 25;

    private static TrayHost? _instance;
    private readonly DispatcherQueue _dispatcher;
    private readonly Dictionary<int, Action> _hotkeys = [];
    private nint _hwnd;
    private nint _icon;
    private int _nextHotkeyId = 1;
    private static readonly uint TaskbarCreated = Native.RegisterWindowMessageW("TaskbarCreated");
    private string? _tooltip;

    public TrayHost(DispatcherQueue dispatcher, string iconPath)
    {
        _dispatcher = dispatcher;
        _instance = this;
        CreateMessageWindow();
        _icon = Native.LoadImageW(0, iconPath, Native.IMAGE_ICON, 16, 16, Native.LR_LOADFROMFILE);
    }

    public event Action? OpenWindowRequested;
    public event Action? DownloadsRequested;
    public event Action? NotesRequested;
    public event Action? SettingsRequested;
    public event Action? UpdatesRequested;
    public event Action? ClipboardPauseRequested;
    public event Action<SnapMode>? SnapRequested;
    public event Action? SnapHistoryRequested;
    public event Action? SnapReleaseRequested;
    public event Action? QuitRequested;

    /// <summary>Shows "Pause Clipboard History" in the menu while the Clipboard History is on.</summary>
    public bool ClipboardHistoryOn { get; set; }

    /// <summary>Checkmark of "Pause Clipboard History".</summary>
    public bool ClipboardPaused { get; set; }

    /// <summary>Snap is on: the menu has a Snap submenu.</summary>
    public bool SnapOn { get; set; }

    public nint Handle => _hwnd;

    public void ShowIcon(string tooltip)
    {
        _tooltip = tooltip;
        var data = NewData();
        data.uFlags = Native.NIF_MESSAGE | Native.NIF_ICON | Native.NIF_TIP | Native.NIF_SHOWTIP;
        data.uCallbackMessage = TrayCallback;
        data.hIcon = _icon;
        SetTip(ref data, tooltip);
        Native.Shell_NotifyIconW(Native.NIM_ADD, &data);
        data.uVersion = 4;
        Native.Shell_NotifyIconW(Native.NIM_SETVERSION, &data);
    }

    public void SetTooltip(string tooltip)
    {
        var data = NewData();
        data.uFlags = Native.NIF_TIP | Native.NIF_SHOWTIP;
        SetTip(ref data, tooltip);
        Native.Shell_NotifyIconW(Native.NIM_MODIFY, &data);
    }

    public void HideIcon()
    {
        _tooltip = null; // not re-added when Explorer restarts
        var data = NewData();
        Native.Shell_NotifyIconW(Native.NIM_DELETE, &data);
    }

    /// <summary>Registers a system-wide hotkey like "win+alt+d". Returns false if another app owns it.</summary>
    public bool RegisterHotkey(string chordText, Action action)
    {
        if (!KeyChord.TryParse(chordText, out var chord)) return false;
        var vk = KeyInput.VirtualKeyFor(chord.Key);
        if (vk == 0) return false;
        uint mods = Native.MOD_NOREPEAT;
        if (chord.Modifiers.HasFlag(KeyModifiers.Control)) mods |= Native.MOD_CONTROL;
        if (chord.Modifiers.HasFlag(KeyModifiers.Shift)) mods |= Native.MOD_SHIFT;
        if (chord.Modifiers.HasFlag(KeyModifiers.Alt)) mods |= Native.MOD_ALT;
        if (chord.Modifiers.HasFlag(KeyModifiers.Windows)) mods |= Native.MOD_WIN;
        var id = _nextHotkeyId++;
        if (!Native.RegisterHotKey(_hwnd, id, mods, vk)) return false;
        _hotkeys[id] = action;
        return true;
    }

    public void UnregisterAllHotkeys()
    {
        foreach (var id in _hotkeys.Keys) Native.UnregisterHotKey(_hwnd, id);
        _hotkeys.Clear();
    }

    private Native.NOTIFYICONDATAW NewData() => new()
    {
        cbSize = (uint)sizeof(Native.NOTIFYICONDATAW),
        hWnd = _hwnd,
        uID = 1,
    };

    private static void SetTip(ref Native.NOTIFYICONDATAW data, string tip)
    {
        var t = tip.Length > 127 ? tip[..127] : tip;
        fixed (char* dst = data.szTip)
        {
            t.AsSpan().CopyTo(new Span<char>(dst, 128));
            dst[t.Length] = '\0';
        }
    }

    private void CreateMessageWindow()
    {
        const string className = "PolluxTrayHost";
        fixed (char* cls = className)
        {
            var wc = new Native.WNDCLASSEXW
            {
                cbSize = (uint)sizeof(Native.WNDCLASSEXW),
                lpfnWndProc = &WndProc,
                hInstance = Native.GetModuleHandleW(null),
                lpszClassName = cls,
            };
            Native.RegisterClassExW(&wc);
        }

        // Not HWND_MESSAGE: the tray needs a window that can become foreground for its context menu.
        _hwnd = Native.CreateWindowExW(0, className, "Pollux", 0, 0, 0, 0, 0, 0, 0, Native.GetModuleHandleW(null), 0);
    }

    [UnmanagedCallersOnly]
    private static nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        var self = _instance;
        if (self is not null)
        {
            if (msg == TrayCallback)
            {
                var evt = (uint)(lParam & 0xFFFF);
                if (evt == Native.WM_LBUTTONUP) self.Post(self.DownloadsRequested);
                else if (evt == Native.WM_LBUTTONDBLCLK) self.Post(self.OpenWindowRequested);
                else if (evt == Native.WM_CONTEXTMENU) self.ShowMenu();
                return 0;
            }

            if (msg == TaskbarCreated && self._tooltip is { } tip)
            {
                // Explorer restarted: the tray icon has to be added again.
                self.ShowIcon(tip);
                return 0;
            }

            if (msg == Native.WM_HOTKEY && self._hotkeys.TryGetValue((int)wParam, out var action))
            {
                self._dispatcher.TryEnqueue(() => action());
                return 0;
            }
        }

        return Native.DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    private void Post(Action? action)
    {
        if (action is not null) _dispatcher.TryEnqueue(() => action());
    }

    private void ShowMenu()
    {
        var menu = Native.CreatePopupMenu();
        Native.AppendMenuW(menu, Native.MF_STRING, MenuNewWindow, "New window");
        Native.AppendMenuW(menu, Native.MF_STRING, MenuDownloads, "Downloads");
        Native.AppendMenuW(menu, Native.MF_STRING, MenuNotes, "Notes");
        Native.AppendMenuW(menu, Native.MF_STRING, MenuUpdates, "App updates (winget)…");
        nint snap = 0;
        if (SnapOn)
        {
            snap = Native.CreatePopupMenu();
            Native.AppendMenuW(snap, Native.MF_STRING, MenuSnapArea, "Area");
            Native.AppendMenuW(snap, Native.MF_STRING, MenuSnapWindow, "Window");
            Native.AppendMenuW(snap, Native.MF_STRING, MenuSnapScreen, "Screen");
            Native.AppendMenuW(snap, Native.MF_STRING, MenuSnapDelay, "Area after delay");
            Native.AppendMenuW(snap, Native.MF_SEPARATOR, 0, null);
            Native.AppendMenuW(snap, Native.MF_STRING, MenuSnapHistory, "Open history");
            Native.AppendMenuW(snap, Native.MF_STRING, MenuSnapRelease, "Release Floats");
            Native.AppendMenuW(menu, Native.MF_POPUP, (nuint)snap, "Snap");
        }

        if (ClipboardHistoryOn)
        {
            Native.AppendMenuW(menu, Native.MF_STRING | (ClipboardPaused ? Native.MF_CHECKED : 0), MenuClipboardPause, "Pause Clipboard History");
        }

        Native.AppendMenuW(menu, Native.MF_SEPARATOR, 0, null);
        Native.AppendMenuW(menu, Native.MF_STRING, MenuSettings, "Settings");
        Native.AppendMenuW(menu, Native.MF_STRING, MenuQuit, "Quit Pollux");
        Native.GetCursorPos(out var pt);
        Native.SetForegroundWindow(_hwnd);
        var cmd = Native.TrackPopupMenuEx(menu, Native.TPM_RETURNCMD | Native.TPM_RIGHTBUTTON, pt.X, pt.Y, _hwnd, 0);
        Native.DestroyMenu(menu); // destroys the submenu with it
        switch (cmd)
        {
            case MenuSnapArea: Post(() => SnapRequested?.Invoke(SnapMode.Area)); return;
            case MenuSnapWindow: Post(() => SnapRequested?.Invoke(SnapMode.Window)); return;
            case MenuSnapScreen: Post(() => SnapRequested?.Invoke(SnapMode.Screen)); return;
            case MenuSnapDelay: Post(() => SnapRequested?.Invoke(SnapMode.AreaAfterDelay)); return;
            case MenuSnapHistory: Post(SnapHistoryRequested); return;
            case MenuSnapRelease: Post(SnapReleaseRequested); return;
        }

        Post(cmd switch
        {
            MenuNewWindow => OpenWindowRequested,
            MenuDownloads => DownloadsRequested,
            MenuNotes => NotesRequested,
            MenuSettings => SettingsRequested,
            MenuUpdates => UpdatesRequested,
            MenuClipboardPause => ClipboardPauseRequested,
            MenuQuit => QuitRequested,
            _ => null,
        });
    }

    public void Dispose()
    {
        UnregisterAllHotkeys();
        HideIcon();
        _instance = null;
    }
}
