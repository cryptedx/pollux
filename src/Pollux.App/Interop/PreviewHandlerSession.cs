using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using static Pollux.App.Interop.PreviewHandlerInterop;

namespace Pollux.App.Interop;

/// <summary>What loading a preview handler came to.</summary>
internal enum PreviewHandlerOutcome
{
    /// <summary>DoPreview succeeded; the handler draws into the host window.</summary>
    Shown,

    /// <summary>The handler could not be created out of process: it will not work for any file this session.</summary>
    Unavailable,

    /// <summary>The handler refused this file (initialisation or DoPreview failed).</summary>
    Failed,

    /// <summary>A newer request replaced this one before it ran.</summary>
    Superseded,
}

/// <summary>One load: the handler, the file, the host window and its client size, the theme colours (COLORREF) and the frame.</summary>
internal sealed record PreviewHandlerRequest(Guid Clsid, string Path, nint Hwnd, int Width, int Height, uint Background, uint Text, PreviewHandlerFrame Frame);

/// <summary>
/// The preview handler shown in one host window (ADR 0009). STA worker thread only (<see cref="Services.PreviewHandlerWorker"/>).
/// The handler is created with CLSCTX_LOCAL_SERVER, so it runs in the prevhost.exe surrogate and never in Pollux;
/// it is initialised with a read-only stream over the file where it supports one (else with the path or a shell item),
/// gets the frame as its site and the theme colours, then draws into the host window.
/// </summary>
internal sealed unsafe class PreviewHandlerSession
{
    private static readonly StrategyBasedComWrappers Wrappers = new();

    private nint _ptr, _stream, _item;
    private IPreviewHandler? _handler;

    /// <summary>Unloads what is shown, then loads <paramref name="request"/>.</summary>
    public PreviewHandlerOutcome Load(PreviewHandlerRequest request)
    {
        Unload();
        var clsid = request.Clsid;
        var iid = IID_IUnknown;
        var hr = CoCreateInstance(&clsid, 0, CLSCTX_LOCAL_SERVER, &iid, out _ptr);
        if (hr < 0 || _ptr == 0)
        {
            Log($"create {clsid:B} failed: 0x{hr:X8}");
            _ptr = 0;
            return PreviewHandlerOutcome.Unavailable;
        }

        try
        {
            // A unique instance so FinalRelease frees it here, on this thread, instead of in a finalizer.
            var com = Wrappers.GetOrCreateObjectForComInstance(_ptr, CreateObjectFlags.UniqueInstance);
            if (com is not IPreviewHandler handler)
            {
                Release();
                return PreviewHandlerOutcome.Unavailable;
            }

            _handler = handler;
            hr = Initialize(com, request.Path);
            if (hr >= 0)
            {
                if (com is IObjectWithSite site) SetSite(site, request.Frame);
                var rect = new RECT { Right = request.Width, Bottom = request.Height };
                hr = handler.SetWindow(request.Hwnd, &rect);
                if (com is IPreviewHandlerVisuals visuals)
                {
                    visuals.SetBackgroundColor(request.Background);
                    visuals.SetTextColor(request.Text);
                }

                if (hr >= 0) hr = handler.DoPreview();
            }

            if (hr >= 0) return PreviewHandlerOutcome.Shown;
            Log($"{clsid:B} could not show {request.Path}: 0x{hr:X8}");
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            Log($"{clsid:B} failed on {request.Path}: 0x{ex.HResult:X8} {ex.Message}");
        }

        Unload();
        return PreviewHandlerOutcome.Failed;
    }

    /// <summary>Tells the handler the host window's new client size.</summary>
    public void SetRect(int width, int height)
    {
        if (_handler is null) return;
        var rect = new RECT { Right = width, Bottom = height };
        try { _handler.SetRect(&rect); }
        catch (COMException ex) { Log($"SetRect failed: 0x{ex.HResult:X8}"); }
    }

    /// <summary>Unloads and releases the handler and the file it was given; no-op when nothing is loaded.</summary>
    public void Unload()
    {
        if (_handler is not null)
        {
            try
            {
                _handler.Unload();
                if ((object)_handler is IObjectWithSite site) site.SetSite(0);
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException)
            {
                Log($"unload failed: 0x{ex.HResult:X8}");
            }
        }

        Release();
    }

    /// <summary>IInitializeWithStream first (no path handed out), then IInitializeWithFile, then IInitializeWithItem.</summary>
    private int Initialize(object com, string path)
    {
        if (com is IInitializeWithStream withStream)
        {
            var hr = SHCreateStreamOnFileEx(path, STGM_READ | STGM_SHARE_DENY_NONE, 0, false, 0, out _stream);
            return hr < 0 ? hr : withStream.Initialize(_stream, STGM_READ);
        }

        if (com is IInitializeWithFile withFile) return withFile.Initialize(path, STGM_READ);

        if (com is IInitializeWithItem withItem)
        {
            var iid = IID_IShellItem;
            var hr = SHCreateItemFromParsingName(path, 0, &iid, out _item);
            return hr < 0 ? hr : withItem.Initialize(_item, STGM_READ);
        }

        return unchecked((int)0x80004002); // E_NOINTERFACE
    }

    private static void SetSite(IObjectWithSite site, PreviewHandlerFrame frame)
    {
        var unknown = Wrappers.GetOrCreateComInterfaceForObject(frame, CreateComInterfaceFlags.None);
        try { site.SetSite(unknown); }
        finally { Marshal.Release(unknown); }
    }

    private void Release()
    {
        if ((object?)_handler is ComObject com) com.FinalRelease();
        _handler = null;
        if (_ptr != 0) Marshal.Release(_ptr);
        if (_stream != 0) Marshal.Release(_stream);
        if (_item != 0) Marshal.Release(_item);
        _ptr = _stream = _item = 0;
    }

    private static void Log(string message) => Services.CrashLog.Trace("preview handler " + message);
}

/// <summary>
/// The site Pollux gives a preview handler (IPreviewHandlerFrame). Its accelerator table holds only Escape: pressed while
/// the handler has focus, the handler passes it here and Pollux takes focus back. Called from prevhost.exe on any thread.
/// </summary>
[GeneratedComClass]
internal sealed unsafe partial class PreviewHandlerFrame(Action<ushort> keyPressed) : PreviewHandlerInterop.IPreviewHandlerFrame
{
    private static readonly nint Accelerators = CreateAccelerators();

    public int GetWindowContext(PREVIEWHANDLERFRAMEINFO* info)
    {
        info->haccel = Accelerators;
        info->cAccelEntries = Accelerators == 0 ? 0u : 1u;
        return S_OK;
    }

    public int TranslateAccelerator(MSG* msg)
    {
        if (msg->message is not (WM_KEYDOWN or WM_SYSKEYDOWN) || msg->wParam != VK_ESCAPE) return S_FALSE;
        keyPressed(VK_ESCAPE);
        return S_OK;
    }

    private static nint CreateAccelerators()
    {
        var accel = new ACCEL { fVirt = FVIRTKEY, key = VK_ESCAPE, cmd = 1 };
        return CreateAcceleratorTableW(&accel, 1);
    }
}
