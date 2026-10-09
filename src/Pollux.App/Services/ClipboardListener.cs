using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Pollux.App.Interop;

namespace Pollux.App.Services;

/// <summary>
/// Hidden message-only window that receives WM_CLIPBOARDUPDATE after every copy (any app) and reports it on the UI thread.
/// </summary>
internal sealed unsafe class ClipboardListener : IDisposable
{
    private const string ClassName = "PolluxClipboardListener";
    private static ClipboardListener? _instance;
    private static bool _classRegistered;
    private readonly DispatcherQueue _dispatcher;
    private nint _hwnd;

    public ClipboardListener(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
        _instance = this;
        CreateMessageWindow();
        if (_hwnd != 0) Native.AddClipboardFormatListener(_hwnd);
    }

    /// <summary>Raised on the UI thread when the clipboard content changed.</summary>
    public event Action? ClipboardChanged;

    public nint Handle => _hwnd;

    private void CreateMessageWindow()
    {
        if (!_classRegistered)
        {
            fixed (char* cls = ClassName)
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

            _classRegistered = true;
        }

        _hwnd = Native.CreateWindowExW(0, ClassName, "Pollux Clipboard", 0, 0, 0, 0, 0, Native.HWND_MESSAGE, 0, Native.GetModuleHandleW(null), 0);
    }

    [UnmanagedCallersOnly]
    private static nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        if (msg == Native.WM_CLIPBOARDUPDATE && _instance is { } self && self._hwnd == hwnd)
        {
            self._dispatcher.TryEnqueue(() => self.ClipboardChanged?.Invoke());
            return 0;
        }

        return Native.DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        if (_hwnd != 0)
        {
            Native.RemoveClipboardFormatListener(_hwnd);
            Native.DestroyWindow(_hwnd);
            _hwnd = 0;
        }

        if (_instance == this) _instance = null;
    }
}
