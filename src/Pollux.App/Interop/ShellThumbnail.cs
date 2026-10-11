using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace Pollux.App.Interop;

/// <summary>
/// The thumbnail Explorer shows for a file or folder (IShellItemImageFactory), as top-down BGRA pixels.
/// Uses source-generated COM interop so it works under Native AOT. Blocking: call it from an STA background thread.
/// </summary>
internal static unsafe partial class ShellThumbnail
{
    private const int SIIGBF_RESIZETOFIT = 0x0, SIIGBF_ICONONLY = 0x4;
    private const uint DIB_RGB_COLORS = 0;

    private static readonly StrategyBasedComWrappers Wrappers = new();

    internal readonly record struct Pixels(byte[] Bgra, int Width, int Height);

    /// <summary>The image fitted into <paramref name="size"/>×<paramref name="size"/> pixels; null when the shell has none.</summary>
    public static Pixels? Extract(string path, int size, bool iconOnly)
    {
        nint factoryPtr = 0, hbitmap = 0;
        IShellItemImageFactory? factory = null;
        try
        {
            var iid = typeof(IShellItemImageFactory).GUID;
            if (SHCreateItemFromParsingName(path, 0, &iid, out factoryPtr) != 0 || factoryPtr == 0) return null;
            // A unique instance so FinalRelease frees it right here, on this thread, instead of in a finalizer.
            factory = (IShellItemImageFactory)Wrappers.GetOrCreateObjectForComInstance(factoryPtr, CreateObjectFlags.UniqueInstance);
            var flags = iconOnly ? SIIGBF_ICONONLY : SIIGBF_RESIZETOFIT;
            if (factory.GetImage(new SIZE { cx = size, cy = size }, flags, out hbitmap) != 0 || hbitmap == 0) return null;
            return Read(hbitmap);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            Services.CrashLog.Trace($"thumbnail {path} failed: 0x{ex.HResult:X8} {ex.Message}");
            return null;
        }
        finally
        {
            if (hbitmap != 0) DeleteObject(hbitmap);
            if ((object?)factory is ComObject com) com.FinalRelease();
            if (factoryPtr != 0) Marshal.Release(factoryPtr);
        }
    }

    /// <summary>Copies a bitmap into 32-bit top-down BGRA (negative height), whatever its own layout.</summary>
    private static Pixels? Read(nint hbitmap)
    {
        BITMAP bm;
        if (GetObjectW(hbitmap, sizeof(BITMAP), &bm) == 0) return null;
        var width = bm.bmWidth;
        var height = Math.Abs(bm.bmHeight);
        if (width <= 0 || height <= 0) return null;

        var info = new BITMAPINFO
        {
            bmiHeader = new BITMAPINFOHEADER
            {
                biSize = (uint)sizeof(BITMAPINFOHEADER),
                biWidth = width,
                biHeight = -height,
                biPlanes = 1,
                biBitCount = 32,
            },
        };
        var bgra = new byte[width * height * 4];
        var dc = CreateCompatibleDC(0);
        try
        {
            fixed (byte* bits = bgra)
            {
                if (GetDIBits(dc, hbitmap, 0, (uint)height, bits, &info, DIB_RGB_COLORS) == 0) return null;
            }
        }
        finally
        {
            DeleteDC(dc);
        }

        Core.Files.ThumbnailPixels.MakeOpaqueIfNoAlpha(bgra);
        return new Pixels(bgra, width, height);
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SIZE { public int cx; public int cy; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int bmType;
        public int bmWidth;
        public int bmHeight;
        public int bmWidthBytes;
        public ushort bmPlanes;
        public ushort bmBitsPixel;
        public nint bmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
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

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        public uint bmiColors;
    }

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SHCreateItemFromParsingName(string path, nint bindCtx, Guid* riid, out nint ppv);

    [LibraryImport("gdi32.dll")]
    private static partial int GetObjectW(nint handle, int size, void* buffer);

    [LibraryImport("gdi32.dll")]
    private static partial int GetDIBits(nint dc, nint bitmap, uint start, uint lines, void* bits, BITMAPINFO* info, uint usage);

    [LibraryImport("gdi32.dll")]
    private static partial nint CreateCompatibleDC(nint dc);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteDC(nint dc);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteObject(nint handle);

    [GeneratedComInterface]
    [Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B")]
    internal partial interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(SIZE size, int flags, out nint phbm);
    }
}
