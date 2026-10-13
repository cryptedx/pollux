using System.Runtime.InteropServices;

namespace Pollux.App.Interop;

/// <summary>Win32 calls for Snap: monitors and their DPI, and a GDI copy of the screen.</summary>
internal static unsafe partial class Native
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO { public uint cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EnumDisplayMonitors(nint hdc, nint clip, delegate* unmanaged<nint, nint, RECT*, nint, int> callback, nint data);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetMonitorInfoW(nint monitor, MONITORINFO* info);

    public const int MDT_EFFECTIVE_DPI = 0;

    [LibraryImport("shcore.dll")]
    public static partial int GetDpiForMonitor(nint monitor, int dpiType, out uint dpiX, out uint dpiY);

    // ---- windows under the Snap overlay ----
    public const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    public const int DWMWA_CLOAKED = 14;

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsIconic(nint hwnd);

    [LibraryImport("dwmapi.dll")]
    public static partial int DwmGetWindowAttribute(nint hwnd, int attribute, void* value, int size);

    // ---- Float: opacity and click-through ----
    public const int GWL_EXSTYLE = -20;
    public const long WS_EX_LAYERED = 0x00080000, WS_EX_TRANSPARENT = 0x00000020;
    public const uint LWA_ALPHA = 0x2;

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    public static partial nint GetWindowLongPtr(nint hwnd, int index);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    public static partial nint SetWindowLongPtr(nint hwnd, int index, nint value);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetLayeredWindowAttributes(nint hwnd, uint key, byte alpha, uint flags);

    // ---- tray submenu ----
    public const uint MF_POPUP = 0x10;
    public const uint MF_GRAYED = 0x1;

    // ---- GDI ----
    public const uint SRCCOPY = 0x00CC0020;
    public const uint CAPTUREBLT = 0x40000000;
    public const uint DIB_RGB_COLORS = 0;

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [LibraryImport("user32.dll")]
    public static partial nint GetDC(nint hwnd);

    [LibraryImport("user32.dll")]
    public static partial int ReleaseDC(nint hwnd, nint hdc);

    [LibraryImport("gdi32.dll")]
    public static partial nint CreateCompatibleDC(nint hdc);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeleteDC(nint hdc);

    [LibraryImport("gdi32.dll")]
    public static partial nint CreateDIBSection(nint hdc, BITMAPINFOHEADER* info, uint usage, out void* bits, nint section, uint offset);

    [LibraryImport("gdi32.dll")]
    public static partial nint SelectObject(nint hdc, nint obj);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeleteObject(nint obj);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool BitBlt(nint dest, int x, int y, int w, int h, nint src, int srcX, int srcY, uint rop);
}
