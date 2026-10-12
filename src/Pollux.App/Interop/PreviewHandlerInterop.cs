using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace Pollux.App.Interop;

/// <summary>
/// The COM interfaces and Win32 calls for hosting shell preview handlers (ADR 0009). Source-generated COM interop so it
/// works under Native AOT; interface pointers that only pass through (IStream, IShellItem, IUnknown) stay raw.
/// </summary>
internal static unsafe partial class PreviewHandlerInterop
{
    public const uint CLSCTX_LOCAL_SERVER = 0x4;
    public const uint STGM_READ = 0x0, STGM_SHARE_DENY_NONE = 0x40;
    public const int S_OK = 0, S_FALSE = 1;

    public const uint WS_CHILD = 0x40000000, WS_CLIPCHILDREN = 0x02000000, WS_CLIPSIBLINGS = 0x04000000;
    public const uint WS_EX_NOPARENTNOTIFY = 0x4;
    public const uint SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40, SWP_HIDEWINDOW = 0x80, SWP_NOZORDER = 0x4, SWP_NOMOVE = 0x2, SWP_NOSIZE = 0x1;
    public static readonly nint HWND_TOP = 0;
    public const uint WM_ERASEBKGND = 0x0014, WM_KEYDOWN = 0x0100, WM_SYSKEYDOWN = 0x0104;
    public const ushort VK_ESCAPE = 0x1B;
    public const byte FVIRTKEY = 0x01;
    public const uint QS_ALLINPUT = 0x04FF, MWMO_INPUTAVAILABLE = 0x4, INFINITE = 0xFFFFFFFF, PM_REMOVE = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public nint hwnd;
        public uint message;
        public nint wParam;
        public nint lParam;
        public uint time;
        public Native.POINT pt;
        public uint lPrivate;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ACCEL { public byte fVirt; public ushort key; public ushort cmd; }

    [StructLayout(LayoutKind.Sequential)]
    public struct PREVIEWHANDLERFRAMEINFO { public nint haccel; public uint cAccelEntries; }

    [LibraryImport("ole32.dll")]
    public static partial int CoCreateInstance(Guid* clsid, nint outer, uint context, Guid* iid, out nint ppv);

    [LibraryImport("ole32.dll")]
    public static partial int CoEnableCallCancellation(nint reserved);

    [LibraryImport("ole32.dll")]
    public static partial int CoCancelCall(uint threadId, uint timeout);

    [LibraryImport("shlwapi.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int SHCreateStreamOnFileEx(string path, uint mode, uint attributes, [MarshalAs(UnmanagedType.Bool)] bool create, nint template, out nint stream);

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int SHCreateItemFromParsingName(string path, nint bindCtx, Guid* riid, out nint ppv);

    [LibraryImport("kernel32.dll")]
    public static partial uint GetCurrentThreadId();

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint CreateEventW(nint attributes, [MarshalAs(UnmanagedType.Bool)] bool manualReset, [MarshalAs(UnmanagedType.Bool)] bool initialState, string? name);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetEvent(nint handle);

    [LibraryImport("user32.dll")]
    public static partial uint MsgWaitForMultipleObjectsEx(uint count, nint* handles, uint milliseconds, uint wakeMask, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PeekMessageW(out MSG msg, nint hwnd, uint filterMin, uint filterMax, uint remove);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool TranslateMessage(in MSG msg);

    [LibraryImport("user32.dll")]
    public static partial nint DispatchMessageW(in MSG msg);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int cx, int cy, uint flags);

    [LibraryImport("user32.dll")]
    public static partial nint GetFocus();

    [LibraryImport("user32.dll")]
    public static partial nint SetFocus(nint hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindow(nint hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsChild(nint parent, nint hwnd);

    [LibraryImport("user32.dll")]
    public static partial nint CreateAcceleratorTableW(ACCEL* accel, int count);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetClientRect(nint hwnd, out RECT rect);

    [LibraryImport("user32.dll")]
    public static partial int FillRect(nint dc, in RECT rect, nint brush);

    [LibraryImport("gdi32.dll")]
    public static partial nint CreateSolidBrush(uint colorRef);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeleteObject(nint handle);

    /// <summary>IShellItem; only its IID is needed (the item is passed through as a raw pointer).</summary>
    public static readonly Guid IID_IShellItem = new("43826d1e-e718-42ee-bc55-a1e261c37bfe");

    /// <summary>IUnknown, the interface a handler is created for (each specific interface is then queried).</summary>
    public static readonly Guid IID_IUnknown = new("00000000-0000-0000-C000-000000000046");

    [GeneratedComInterface]
    [Guid("8895b1c6-b41f-4c1c-a562-0d564250836f")]
    internal partial interface IPreviewHandler
    {
        [PreserveSig] int SetWindow(nint hwnd, RECT* rect);
        [PreserveSig] int SetRect(RECT* rect);
        [PreserveSig] int DoPreview();
        [PreserveSig] int Unload();
        [PreserveSig] int SetFocus();
        [PreserveSig] int QueryFocus(out nint hwnd);
        [PreserveSig] int TranslateAccelerator(MSG* msg);
    }

    [GeneratedComInterface]
    [Guid("fec87aaf-35f9-447a-adb7-20234491401a")]
    internal partial interface IPreviewHandlerFrame
    {
        [PreserveSig] int GetWindowContext(PREVIEWHANDLERFRAMEINFO* info);
        [PreserveSig] int TranslateAccelerator(MSG* msg);
    }

    [GeneratedComInterface]
    [Guid("196bf9a5-b346-4ef0-aa1e-5dcdb76768b1")]
    internal partial interface IPreviewHandlerVisuals
    {
        [PreserveSig] int SetBackgroundColor(uint color);
        [PreserveSig] int SetFont(nint logFont);
        [PreserveSig] int SetTextColor(uint color);
    }

    [GeneratedComInterface]
    [Guid("b824b49d-22ac-4161-ac8a-9916e8fa3f7f")]
    internal partial interface IInitializeWithStream
    {
        [PreserveSig] int Initialize(nint stream, uint mode);
    }

    [GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
    [Guid("b7d14566-0509-4cce-a71f-0a554233bd9b")]
    internal partial interface IInitializeWithFile
    {
        [PreserveSig] int Initialize(string path, uint mode);
    }

    [GeneratedComInterface]
    [Guid("7f73be3f-fb79-493c-a6c7-7ee14e245841")]
    internal partial interface IInitializeWithItem
    {
        [PreserveSig] int Initialize(nint item, uint mode);
    }

    [GeneratedComInterface]
    [Guid("fc4801a3-2ba9-11cf-a229-00aa003d7352")]
    internal partial interface IObjectWithSite
    {
        [PreserveSig] int SetSite(nint site);
        [PreserveSig] int GetSite(Guid* iid, out nint site);
    }
}
