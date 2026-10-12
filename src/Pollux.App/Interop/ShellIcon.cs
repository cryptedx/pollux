using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Pollux.Core.Files;

namespace Pollux.App.Interop;

/// <summary>
/// Shell icons from the system image list: SHGetFileInfo finds an entry's icon index (for a type without touching the
/// disk), IImageList::GetIcon draws it with its overlay, and the HICON is read as premultiplied top-down BGRA.
/// Explorer uses the same lists, so equal icons share one index. Source-generated COM interop (Native AOT).
/// Blocking: call it from one STA background thread; the image lists are kept for that thread.
/// </summary>
internal static unsafe partial class ShellIcon
{
    private const uint SHGFI_ICON = 0x100, SHGFI_SYSICONINDEX = 0x4000, SHGFI_USEFILEATTRIBUTES = 0x10;
    private const uint SHGFI_SMALLICON = 0x1, SHGFI_OVERLAYINDEX = 0x40, SHGFI_PIDL = 0x8;
    private const uint FILE_ATTRIBUTE_DIRECTORY = 0x10, FILE_ATTRIBUTE_NORMAL = 0x80;
    private const uint ILD_TRANSPARENT = 0x1;
    private const uint DIB_RGB_COLORS = 0;
    private const uint DRIVE_REMOTE = 4;
    private const uint CLSCTX_INPROC_SERVER = 1;
    private const uint SLGP_RAWPATH = 0x4;

    /// <summary>The shell's own shortcut arrow is overlay image 2 (1 is the share hand).</summary>
    public const int LinkOverlay = 2;

    private static readonly StrategyBasedComWrappers Wrappers = new();
    private static readonly Guid ClsidShellLink = new("00021401-0000-0000-C000-000000000046");
    private static readonly IImageList?[] Lists = new IImageList?[3];

    /// <summary>
    /// The icon of <paramref name="name"/> in the system image list and its overlay (0 for none); null when the shell
    /// has none. <paramref name="fromDisk"/> reads the entry itself; otherwise <paramref name="name"/> is a made-up
    /// name whose type alone decides the icon, and no overlay is known.
    /// </summary>
    public static (int Index, int Overlay)? Find(string name, bool isDirectory, bool fromDisk)
    {
        SHFILEINFOW info;
        var attributes = isDirectory ? FILE_ATTRIBUTE_DIRECTORY : FILE_ATTRIBUTE_NORMAL;
        // The overlay index needs SHGFI_ICON as well; the small icon that comes with it is thrown away.
        var flags = SHGFI_SYSICONINDEX | (fromDisk ? SHGFI_ICON | SHGFI_SMALLICON | SHGFI_OVERLAYINDEX : SHGFI_USEFILEATTRIBUTES);
        var result = SHGetFileInfoW(name, attributes, &info, (uint)sizeof(SHFILEINFOW), flags);
        if (info.hIcon != 0) DestroyIcon(info.hIcon);
        if (result == 0) return null;
        return fromDisk ? (info.iIcon & 0x00FFFFFF, (info.iIcon >> 24) & 0xFF) : (info.iIcon, 0);
    }

    /// <summary>The icon of a shell item without a path (a Control Panel applet, a phone) by its parsing name, read through its ID list; null when the shell cannot resolve it.</summary>
    public static (int Index, int Overlay)? FindShell(string parsingName)
    {
        if (SHParseDisplayName(parsingName, 0, out var pidl, 0, out _) < 0 || pidl == 0) return null;
        try
        {
            SHFILEINFOW info;
            var result = SHGetFileInfoPidl(pidl, 0, &info, (uint)sizeof(SHFILEINFOW), SHGFI_PIDL | SHGFI_SYSICONINDEX | SHGFI_ICON | SHGFI_SMALLICON | SHGFI_OVERLAYINDEX);
            if (info.hIcon != 0) DestroyIcon(info.hIcon);
            return result == 0 ? null : (info.iIcon & 0x00FFFFFF, (info.iIcon >> 24) & 0xFF);
        }
        finally
        {
            Marshal.FreeCoTaskMem(pidl);
        }
    }

    /// <summary>Edge in pixels of the icons in each image list, as (small, large, extra large); 0 for a list that is missing.</summary>
    public static (int Small, int Large, int ExtraLarge) Sizes() =>
        (Size(ShellImageList.Small), Size(ShellImageList.Large), Size(ShellImageList.ExtraLarge));

    private static int Size(ShellImageList list) =>
        List(list) is { } l && l.GetIconSize(out var cx, out _) == 0 ? cx : 0;

    /// <summary>Icon <paramref name="index"/> of an image list with <paramref name="overlay"/> drawn over it; null on failure.</summary>
    public static ShellThumbnail.Pixels? Draw(ShellImageList list, int index, int overlay)
    {
        if (List(list) is not { } l) return null;
        nint icon = 0;
        try
        {
            // INDEXTOOVERLAYMASK: the overlay number goes in bits 8-11 of the draw flags.
            if (l.GetIcon(index, ILD_TRANSPARENT | ((uint)(overlay & 0xF) << 8), out icon) != 0 || icon == 0) return null;
            return Read(icon);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            Services.CrashLog.Trace($"icon {index} failed: 0x{ex.HResult:X8} {ex.Message}");
            return null;
        }
        finally
        {
            if (icon != 0) DestroyIcon(icon);
        }
    }

    private static IImageList? List(ShellImageList list)
    {
        var i = (int)list;
        if (Lists[i] is { } cached) return cached;
        var iid = typeof(IImageList).GUID;
        if (SHGetImageList(i, &iid, out var ptr) != 0 || ptr == 0) return null;
        try
        {
            // Kept for the life of the thread: the shell owns the list and hands out the same one every time.
            return Lists[i] = (IImageList)Wrappers.GetOrCreateObjectForComInstance(ptr, CreateObjectFlags.None);
        }
        finally
        {
            Marshal.Release(ptr);
        }
    }

    /// <summary>A mapped network drive: reading through it can hang or reach another machine.</summary>
    public static bool IsNetworkDrive(char letter) => GetDriveTypeW($"{letter}:\\") == DRIVE_REMOTE;

    /// <summary>A shortcut's target and icon file as stored (environment variables unexpanded); null when it cannot be read.</summary>
    public static (string Target, string Icon)? ReadLink(string linkPath)
    {
        var clsid = ClsidShellLink;
        var iid = typeof(ShellLink.IShellLinkW).GUID;
        if (CoCreateInstance(&clsid, 0, CLSCTX_INPROC_SERVER, &iid, out var ptr) < 0 || ptr == 0) return null;
        var link = (ShellLink.IShellLinkW)Wrappers.GetOrCreateObjectForComInstance(ptr, CreateObjectFlags.UniqueInstance);
        try
        {
            if (link is not ShellLink.IPersistFile file || file.Load(linkPath, 0) < 0) return null;
            var buffer = stackalloc char[1024];
            buffer[0] = '\0';
            if (link.GetPath(buffer, 1024, 0, SLGP_RAWPATH) < 0) buffer[0] = '\0';
            var target = new string(buffer);
            buffer[0] = '\0';
            int iconIndex;
            if (link.GetIconLocation(buffer, 1024, &iconIndex) < 0) buffer[0] = '\0';
            return (target, new string(buffer));
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            Services.CrashLog.Trace($"icon link {linkPath} failed: 0x{ex.HResult:X8} {ex.Message}");
            return null;
        }
        finally
        {
            if ((object)link is ComObject com) com.FinalRelease();
            Marshal.Release(ptr);
        }
    }

    /// <summary>An HICON as premultiplied top-down BGRA; alpha comes from the mask when the color bitmap has none.</summary>
    private static ShellThumbnail.Pixels? Read(nint icon)
    {
        ICONINFO info;
        if (!GetIconInfo(icon, &info)) return null;
        try
        {
            // A monochrome icon has no color bitmap; the caller falls back to the badge.
            if (info.hbmColor == 0) return null;
            BITMAP bm;
            if (GetObjectW(info.hbmColor, sizeof(BITMAP), &bm) == 0) return null;
            var width = bm.bmWidth;
            var height = Math.Abs(bm.bmHeight);
            if (width <= 0 || height <= 0) return null;
            if (Bits(info.hbmColor, width, height) is not { } color) return null;
            if (info.hbmMask != 0 && Bits(info.hbmMask, width, height) is { } mask) IconPixels.ApplyMask(color, mask);
            IconPixels.Premultiply(color);
            return new ShellThumbnail.Pixels(color, width, height);
        }
        finally
        {
            if (info.hbmColor != 0) DeleteObject(info.hbmColor);
            if (info.hbmMask != 0) DeleteObject(info.hbmMask);
        }
    }

    /// <summary>A bitmap's top <paramref name="height"/> rows as 32-bit top-down BGRA, whatever its own layout.</summary>
    private static byte[]? Bits(nint hbitmap, int width, int height)
    {
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
                return GetDIBits(dc, hbitmap, 0, (uint)height, bits, &info, DIB_RGB_COLORS) == 0 ? null : bgra;
            }
        }
        finally
        {
            DeleteDC(dc);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SHFILEINFOW
    {
        public nint hIcon;
        public int iIcon;
        public uint dwAttributes;
        public fixed char szDisplayName[260];
        public fixed char szTypeName[80];
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        public int fIcon;
        public uint xHotspot;
        public uint yHotspot;
        public nint hbmMask;
        public nint hbmColor;
    }

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
        public fixed uint bmiColors[2];
    }

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint SHGetFileInfoW(string path, uint attributes, SHFILEINFOW* info, uint size, uint flags);

    [LibraryImport("shell32.dll", EntryPoint = "SHGetFileInfoW")]
    private static partial nint SHGetFileInfoPidl(nint pidl, uint attributes, SHFILEINFOW* info, uint size, uint flags);

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SHParseDisplayName(string name, nint bindContext, out nint pidl, uint attributesIn, out uint attributesOut);

    [LibraryImport("shell32.dll")]
    private static partial int SHGetImageList(int list, Guid* riid, out nint ppv);

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(Guid* clsid, nint outer, uint context, Guid* iid, out nint ppv);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint GetDriveTypeW(string root);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetIconInfo(nint icon, ICONINFO* info);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyIcon(nint icon);

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

    /// <summary>The leading methods of IImageList (commoncontrols.h), in vtable order up to GetIconSize.</summary>
    [GeneratedComInterface]
    [Guid("46EB5926-582E-4017-9FDF-E8998DAA0950")]
    internal partial interface IImageList
    {
        [PreserveSig] int Add(nint image, nint mask, int* index);
        [PreserveSig] int ReplaceIcon(int i, nint icon, int* index);
        [PreserveSig] int SetOverlayImage(int image, int overlay);
        [PreserveSig] int Replace(int i, nint image, nint mask);
        [PreserveSig] int AddMasked(nint image, uint mask, int* index);
        [PreserveSig] int Draw(nint parameters);
        [PreserveSig] int Remove(int i);
        [PreserveSig] int GetIcon(int i, uint flags, out nint icon);
        [PreserveSig] int GetImageInfo(int i, nint info);
        [PreserveSig] int Copy(int destination, nint source, int sourceIndex, uint flags);
        [PreserveSig] int Merge(int first, nint other, int second, int dx, int dy, Guid* riid, nint* ppv);
        [PreserveSig] int Clone(Guid* riid, nint* ppv);
        [PreserveSig] int GetImageRect(int i, nint rect);
        [PreserveSig] int GetIconSize(out int cx, out int cy);
    }
}
