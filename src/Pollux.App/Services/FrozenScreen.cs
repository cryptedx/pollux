using System.Runtime.InteropServices;
using Pollux.App.Interop;
using Pollux.Core.Snap;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Pollux.App.Services;

/// <summary>
/// A still copy of all monitors, taken the moment a Snap starts (GDI, physical pixels, BGRA top-down).
/// The Snap overlay shows it and the chosen area is cut from it, so what changes on screen meanwhile does not matter.
/// </summary>
internal sealed class FrozenScreen
{
    private static List<SnapMonitor>? _found;
    private static List<SnapWindow>? _windows;

    private FrozenScreen(ScreenLayout layout, byte[] bgra, IReadOnlyList<SnapWindow> windows)
    {
        Layout = layout;
        Bgra = bgra;
        Windows = windows;
    }

    public ScreenLayout Layout { get; }

    /// <summary>The top-level windows at the moment of the Snap, topmost first, with their visible frames.</summary>
    public IReadOnlyList<SnapWindow> Windows { get; }
    public byte[] Bgra { get; }
    public int Width => Layout.VirtualBounds.Width;
    public int Height => Layout.VirtualBounds.Height;

    /// <summary>Copies the whole virtual screen; null when GDI fails (e.g. on the secure desktop).</summary>
    public static unsafe FrozenScreen? Take()
    {
        var layout = new ScreenLayout(Monitors());
        var windows = TopLevelWindows();
        var v = layout.VirtualBounds;
        var screen = Native.GetDC(0);
        if (screen == 0) return null;
        var memory = Native.CreateCompatibleDC(screen);
        try
        {
            var header = new Native.BITMAPINFOHEADER
            {
                biSize = (uint)sizeof(Native.BITMAPINFOHEADER),
                biWidth = v.Width,
                biHeight = -v.Height, // top-down
                biPlanes = 1,
                biBitCount = 32,
            };
            var bitmap = Native.CreateDIBSection(screen, &header, Native.DIB_RGB_COLORS, out var bits, 0, 0);
            if (bitmap == 0) return null;
            try
            {
                var old = Native.SelectObject(memory, bitmap);
                var ok = Native.BitBlt(memory, 0, 0, v.Width, v.Height, screen, v.X, v.Y, Native.SRCCOPY | Native.CAPTUREBLT);
                Native.SelectObject(memory, old);
                if (!ok) return null;
                var bgra = new byte[v.Width * v.Height * 4];
                Marshal.Copy((nint)bits, bgra, 0, bgra.Length);
                return new FrozenScreen(layout, bgra, windows);
            }
            finally { Native.DeleteObject(bitmap); }
        }
        finally
        {
            Native.DeleteDC(memory);
            Native.ReleaseDC(0, screen);
        }
    }

    /// <summary>The colour of one screen pixel as "#RRGGBB".</summary>
    public string ColorAt(PixelPoint screen)
    {
        var x = Math.Clamp(screen.X - Layout.VirtualBounds.X, 0, Width - 1);
        var y = Math.Clamp(screen.Y - Layout.VirtualBounds.Y, 0, Height - 1);
        var i = ((y * Width) + x) * 4;
        return SnapColor.ToHex(Bgra[i], Bgra[i + 1], Bgra[i + 2]);
    }

    /// <summary>The pixels of a screen rectangle, BGRA top-down, fully opaque (GDI leaves alpha at 0).</summary>
    public byte[] Crop(PixelRect screen)
    {
        var r = Layout.ToImage(screen);
        var result = new byte[r.Width * r.Height * 4];
        for (var row = 0; row < r.Height; row++)
            System.Buffer.BlockCopy(Bgra, (((r.Y + row) * Width) + r.X) * 4, result, row * r.Width * 4, r.Width * 4);
        for (var i = 3; i < result.Length; i += 4) result[i] = 255;
        return result;
    }

    /// <summary>A screen rectangle as PNG bytes.</summary>
    public async Task<byte[]> EncodePngAsync(PixelRect screen)
    {
        var pixels = Crop(screen);
        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, (uint)screen.Width, (uint)screen.Height, 96, 96, pixels);
        await encoder.FlushAsync();
        var png = new byte[stream.Size];
        stream.Seek(0);
        using var reader = new DataReader(stream);
        await reader.LoadAsync((uint)stream.Size);
        reader.ReadBytes(png);
        return png;
    }

    private static unsafe List<SnapMonitor> Monitors()
    {
        _found = [];
        Native.EnumDisplayMonitors(0, 0, &OnMonitor, 0);
        var monitors = _found;
        _found = null;
        return monitors;
    }

    private static unsafe List<SnapWindow> TopLevelWindows()
    {
        _windows = [];
        Native.EnumWindows(&OnWindow, 0);
        var windows = _windows;
        _windows = null;
        return windows;
    }

    /// <summary>EnumWindows lists top-level windows in z-order, topmost first. The DWM frame leaves out the invisible resize border.</summary>
    [UnmanagedCallersOnly]
    private static unsafe int OnWindow(nint hwnd, nint data)
    {
        Native.RECT r;
        if (Native.DwmGetWindowAttribute(hwnd, Native.DWMWA_EXTENDED_FRAME_BOUNDS, &r, sizeof(Native.RECT)) != 0) return 1;
        int cloaked;
        var isCloaked = Native.DwmGetWindowAttribute(hwnd, Native.DWMWA_CLOAKED, &cloaked, sizeof(int)) == 0 && cloaked != 0;
        _windows?.Add(new SnapWindow(hwnd, new PixelRect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top),
            Native.IsWindowVisible(hwnd), Native.IsIconic(hwnd), isCloaked));
        return 1;
    }

    [UnmanagedCallersOnly]
    private static unsafe int OnMonitor(nint monitor, nint hdc, Native.RECT* rect, nint data)
    {
        var info = new Native.MONITORINFO { cbSize = (uint)sizeof(Native.MONITORINFO) };
        if (!Native.GetMonitorInfoW(monitor, &info)) return 1;
        var scale = Native.GetDpiForMonitor(monitor, Native.MDT_EFFECTIVE_DPI, out var dpi, out _) == 0 ? dpi / 96.0 : 1.0;
        var m = info.rcMonitor;
        _found?.Add(new SnapMonitor(new PixelRect(m.Left, m.Top, m.Right - m.Left, m.Bottom - m.Top), scale));
        return 1;
    }
}
