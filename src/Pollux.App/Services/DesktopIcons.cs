using System.Runtime.InteropServices;
using Pollux.App.Interop;

namespace Pollux.App.Services;

/// <summary>Shows or hides the Windows desktop icons live (same as right-click desktop → View → Show desktop icons). No admin.</summary>
internal static unsafe class DesktopIcons
{
    private const int ToggleCommand = 0x7402;
    private static nint _found;

    public static bool? AreVisible()
    {
        var view = FindDefView();
        if (view == 0) return null;
        var list = Native.FindWindowExW(view, 0, "SysListView32", null);
        return list != 0 && Native.IsWindowVisible(list);
    }

    public static void SetVisible(bool visible)
    {
        if (AreVisible() is { } current && current != visible)
        {
            Native.SendMessageW(FindDefView(), Native.WM_COMMAND, ToggleCommand, 0);
        }
    }

    private static nint FindDefView()
    {
        var progman = Native.FindWindowW("Progman", null);
        var view = Native.FindWindowExW(progman, 0, "SHELLDLL_DefView", null);
        if (view != 0) return view;

        // After wallpaper changes the view moves into a WorkerW window.
        _found = 0;
        Native.EnumWindows(&FindInWorker, 0);
        return _found;
    }

    [UnmanagedCallersOnly]
    private static int FindInWorker(nint hwnd, nint param)
    {
        var view = Native.FindWindowExW(hwnd, 0, "SHELLDLL_DefView", null);
        if (view == 0) return 1;
        _found = view;
        return 0;
    }
}
