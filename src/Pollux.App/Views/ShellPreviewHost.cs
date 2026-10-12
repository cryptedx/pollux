using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Pollux.App.Interop;
using Pollux.App.Services;
using Pollux.Core.Files;
using Pollux.Core.Operations;
using Windows.Foundation;
using static Pollux.App.Interop.PreviewHandlerInterop;

namespace Pollux.App.Views;

/// <summary>How showing a file with a shell preview handler ended.</summary>
internal enum ShellPreviewResult
{
    Shown,
    /// <summary>Not shown; the caller falls back to Pollux's own preview.</summary>
    Failed,
    /// <summary>A newer Show or Clear replaced it; the caller does nothing.</summary>
    Superseded,
}

/// <summary>
/// Shows a file with its Windows preview handler inside a preview area (ADR 0009). XAML cannot draw over a child HWND
/// (airspace), so the handler draws into a Win32 child window of the main window that is kept over the area's bounds
/// in physical pixels. It is hidden while the area is collapsed, unloaded, covered by anything (Quick Look, the command
/// palette, dialogs, flyouts: checked by hit-testing the area) or the window is minimised. The handler runs out of
/// process and is driven from <see cref="PreviewHandlerWorker"/>; a handler that hangs past the timeout is cancelled,
/// skipped for the rest of the session and Pollux's own preview is shown instead. UI thread only.
/// </summary>
internal sealed class ShellPreviewHost
{
    private const string ClassName = "PolluxPreviewHandlerHost";
    private static readonly TimeSpan LoadTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan CancelGrace = TimeSpan.FromSeconds(2);

    /// <summary>The handler registrations, shared by every preview area.</summary>
    public static ShellPreviewHandlers Handlers { get; } = new(SystemAssociationRegistry.Instance);

    private static bool _classRegistered;
    private static nint _brush;
    private static uint _brushColor = uint.MaxValue;

    private readonly Grid _area;
    private readonly Border _placeholder;
    private readonly TextBlock _status;
    private readonly PreviewHandlerFrame _frame;
    private readonly DispatcherQueueTimer _timer;
    private nint _hwnd;
    private nint _xamlFocus;
    private PreviewHandlerWorker? _worker;
    private PreviewHandlerSession? _session;
    private int _generation;
    private bool _loaded, _shown, _placementQueued, _rectPending;
    private Rect _pixels;
    private (int Width, int Height) _sentSize;

    public ShellPreviewHost(Grid area)
    {
        _area = area;
        _status = new TextBlock { FontSize = 12, Margin = new Thickness(20), Foreground = Theme.Brush("PolluxMutedBrush") };
        // Transparent but hit-testable: whatever is hit above it covers the handler window.
        _placeholder = new Border { Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), Child = _status };
        var ui = area.DispatcherQueue;
        _frame = new PreviewHandlerFrame(key => ui.TryEnqueue(() => OnForwardedKey(key)));
        _timer = ui.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(250);
        _timer.Tick += (_, _) => UpdatePlacement();
        area.LayoutUpdated += (_, _) => QueuePlacement();
        area.Unloaded += (_, _) => Clear();
    }

    /// <summary>A key the handler passed back while it had focus (only Escape); focus is already back in Pollux.</summary>
    public event Action<Windows.System.VirtualKey>? ForwardedKey;

    /// <summary>
    /// The handler to show <paramref name="path"/> with, or null for Pollux's own preview: none registered, it failed
    /// before, or the file came from the Internet (zone mark). Reads the registry and the zone stream: call it off the UI thread.
    /// </summary>
    public static Guid? HandlerFor(string extension, string path) =>
        PreviewPolicy.PrefersShellHandler(extension) && Handlers.Find(extension) is { } clsid && PreviewPolicy.AllowsShellHandler(ZoneMark.Read(path))
            ? clsid
            : null;

    /// <summary>Shows <paramref name="path"/> with handler <paramref name="clsid"/> in the area (the area's children are replaced).</summary>
    public async Task<ShellPreviewResult> ShowAsync(Guid clsid, string path)
    {
        var generation = ++_generation;
        Hide();
        _loaded = false;
        if (!EnsureWindow()) return ShellPreviewResult.Failed;

        _status.Text = "Loading preview…";
        _area.Children.Clear();
        _area.Children.Add(_placeholder);
        _area.UpdateLayout();
        var size = PixelRect() is { } r ? ((int)r.Width, (int)r.Height) : (1, 1);
        _sentSize = size;
        UpdatePlacement();

        var worker = PreviewHandlerWorker.Current;
        if (_worker != worker)
        {
            _worker = worker;
            _session = new PreviewHandlerSession();
        }

        var session = _session!;
        var request = new PreviewHandlerRequest(clsid, path, _hwnd, size.Item1, size.Item2, ColorRef("PolluxSurface2Brush"), ColorRef("PolluxTextBrush"), _frame);
        var focus = GetFocus();
        if (focus != 0 && !IsOurs(focus)) _xamlFocus = focus;

        var (started, done) = worker.Invoke(() => generation == Volatile.Read(ref _generation) ? session.Load(request) : PreviewHandlerOutcome.Superseded);
        PreviewHandlerOutcome outcome;
        try
        {
            if (!await StartedAsync(worker, started)) return generation == _generation ? ShellPreviewResult.Failed : ShellPreviewResult.Superseded;
            outcome = await done.WaitAsync(LoadTimeout);
        }
        catch (TimeoutException)
        {
            outcome = await CancelHungAsync(worker, done);
            Handlers.MarkFailed(clsid);
            CrashLog.Info($"Preview handler {clsid:B} did not answer within {LoadTimeout.TotalSeconds:0} s on {Path.GetFileName(path)}; Pollux's own preview is used for it from now on.");
            if (outcome == PreviewHandlerOutcome.Shown) outcome = PreviewHandlerOutcome.Failed;
            if (generation == _generation) Unload();
        }
        catch (Exception ex)
        {
            // Cancelled with an abandoned worker, or the handler threw: Pollux's own preview instead.
            CrashLog.Trace($"preview handler {clsid:B} on {path}: {ex.GetType().Name} {ex.Message}");
            outcome = PreviewHandlerOutcome.Failed;
        }

        RestoreFocus();
        if (generation != _generation) return ShellPreviewResult.Superseded;
        switch (outcome)
        {
            case PreviewHandlerOutcome.Shown:
                _loaded = true;
                _status.Text = "";
                _timer.Start();
                UpdatePlacement();
                return ShellPreviewResult.Shown;
            case PreviewHandlerOutcome.Unavailable:
                Handlers.MarkFailed(clsid);
                return ShellPreviewResult.Failed;
            case PreviewHandlerOutcome.Superseded:
                return ShellPreviewResult.Superseded;
            default:
                return ShellPreviewResult.Failed;
        }
    }

    /// <summary>Hides the handler window and unloads the handler (another file or kind of preview is shown, or the area went away).</summary>
    public void Clear()
    {
        _generation++;
        Hide();
        Unload();
    }

    /// <summary>
    /// Waits for the worker to begin a load. Work queued before it (another area's load) has its own timeout; anything
    /// still blocking the thread after that (a handler hanging in Unload or SetRect) is cancelled, and the worker abandoned.
    /// </summary>
    private async Task<bool> StartedAsync(PreviewHandlerWorker worker, Task started)
    {
        try
        {
            await started.WaitAsync(LoadTimeout + CancelGrace + CancelGrace);
            return true;
        }
        catch (TimeoutException)
        {
            await Task.Run(worker.CancelCurrentCall);
            try
            {
                await started.WaitAsync(CancelGrace);
                return true;
            }
            catch (TimeoutException)
            {
                worker.Abandon();
                AbandonWindow();
            }
        }

        if (_worker == worker)
        {
            _worker = null;
            _session = null;
        }

        return false;
    }

    /// <summary>Breaks a hung call (CoCancelCall); if the thread stays stuck, the worker is abandoned with whatever it held.</summary>
    private async Task<PreviewHandlerOutcome> CancelHungAsync(PreviewHandlerWorker worker, Task<PreviewHandlerOutcome> done)
    {
        await Task.Run(worker.CancelCurrentCall);
        try
        {
            return await done.WaitAsync(CancelGrace);
        }
        catch (TimeoutException)
        {
            worker.Abandon();
            AbandonWindow();
        }
        catch (Exception ex) when (ex is OperationCanceledException or COMException or InvalidCastException)
        {
            // Failed after the cancel: nothing is shown.
        }

        if (_worker == worker)
        {
            _worker = null;
            _session = null;
        }

        return PreviewHandlerOutcome.Failed;
    }

    /// <summary>
    /// Leaves the host window to the stuck handler (destroying it would wait for prevhost's hung thread): hidden, and a
    /// new one is created for the next file.
    /// </summary>
    private void AbandonWindow()
    {
        Hide();
        _hwnd = 0;
    }

    private void Unload()
    {
        _loaded = false;
        _timer.Stop();
        if (_worker is not { } worker || _session is not { } session) return;
        _ = worker.Invoke(() =>
        {
            session.Unload();
            return true;
        }).Done.ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
    }

    private void OnForwardedKey(ushort key)
    {
        RestoreFocus();
        if (key == VK_ESCAPE) ForwardedKey?.Invoke(Windows.System.VirtualKey.Escape);
    }

    /// <summary>Gives keyboard focus back to Pollux when the handler took it.</summary>
    private void RestoreFocus()
    {
        var focus = GetFocus();
        if (_xamlFocus != 0 && IsWindow(_xamlFocus) && (focus == 0 || IsOurs(focus))) SetFocus(_xamlFocus);
    }

    private bool IsOurs(nint hwnd) => _hwnd != 0 && (hwnd == _hwnd || IsChild(_hwnd, hwnd));

    // ---------------- placement ----------------

    private void QueuePlacement()
    {
        if (_placementQueued || _hwnd == 0) return;
        _placementQueued = true;
        _area.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            _placementQueued = false;
            UpdatePlacement();
        });
    }

    /// <summary>Moves the handler window over the area and shows it, or hides it when the area is not fully visible.</summary>
    private void UpdatePlacement()
    {
        if (_hwnd == 0) return;
        if (!IsWindow(_hwnd))
        {
            _hwnd = 0;
            return;
        }

        if (PixelRect() is not { } r)
        {
            Hide();
            return;
        }

        var show = _loaded && !IsCovered();
        var flags = SWP_NOACTIVATE | (show ? SWP_SHOWWINDOW : SWP_HIDEWINDOW);
        if (r != _pixels || show != _shown)
        {
            SetWindowPos(_hwnd, HWND_TOP, (int)r.X, (int)r.Y, (int)r.Width, (int)r.Height, flags);
            _pixels = r;
            _shown = show;
        }

        if (_loaded && ((int)r.Width, (int)r.Height) != _sentSize) _ = PushSizeAsync();
    }

    private void Hide()
    {
        if (_hwnd == 0 || !_shown) return;
        SetWindowPos(_hwnd, 0, 0, 0, 0, 0, SWP_HIDEWINDOW | SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
        _shown = false;
    }

    /// <summary>Sends the latest size to the handler, one call at a time.</summary>
    private async Task PushSizeAsync()
    {
        if (_rectPending || _worker is not { } worker || _session is not { } session) return;
        _rectPending = true;
        try
        {
            while (_loaded && ((int)_pixels.Width, (int)_pixels.Height) != _sentSize)
            {
                var size = ((int)_pixels.Width, (int)_pixels.Height);
                _sentSize = size;
                await worker.Invoke(() =>
                {
                    session.SetRect(size.Item1, size.Item2);
                    return true;
                }).Done;
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or COMException)
        {
            // The worker was abandoned or the handler is gone; the next Show starts over.
        }
        finally
        {
            _rectPending = false;
        }
    }

    /// <summary>The area in the main window's client pixels; null when it is not on screen (collapsed, unloaded, empty).</summary>
    private Rect? PixelRect()
    {
        if (_area.XamlRoot is not { } root || !_area.IsLoaded || !root.IsHostVisible || _area.ActualWidth < 1 || _area.ActualHeight < 1) return null;
        for (DependencyObject? e = _area; e is not null; e = VisualTreeHelper.GetParent(e))
        {
            if (e is UIElement { Visibility: Visibility.Collapsed }) return null;
        }

        var b = Bounds();
        var scale = root.RasterizationScale;
        var x = Math.Round(b.X * scale);
        var y = Math.Round(b.Y * scale);
        return new Rect(x, y, Math.Max(1, Math.Round((b.X + b.Width) * scale) - x), Math.Max(1, Math.Round((b.Y + b.Height) * scale) - y));
    }

    private Rect Bounds() => _area.TransformToVisual(null).TransformBounds(new Rect(0, 0, _area.ActualWidth, _area.ActualHeight));

    /// <summary>
    /// Whether anything is drawn over part of the area: an open popup (dialog, flyout, menu; tooltips aside) that
    /// overlaps it, or an element above it at one of nine sample points (Quick Look over the pane, the palette).
    /// </summary>
    private bool IsCovered()
    {
        if (_area.XamlRoot is not { } root || root.Content is not UIElement content) return true;
        var b = Bounds();
        foreach (var popup in VisualTreeHelper.GetOpenPopupsForXamlRoot(root))
        {
            if (popup.Child is not FrameworkElement child || child is ToolTip) continue;
            var p = child.TransformToVisual(null).TransformBounds(new Rect(0, 0, child.ActualWidth, child.ActualHeight));
            p.Intersect(b);
            if (!p.IsEmpty && p.Width > 0 && p.Height > 0) return true;
        }

        for (var i = 0; i < 3; i++)
        {
            for (var j = 0; j < 3; j++)
            {
                var point = new Point(b.X + 2 + (b.Width - 4) * i / 2, b.Y + 2 + (b.Height - 4) * j / 2);
                // Topmost first: the placeholder, something inside it or one of its ancestors means nothing covers it.
                var top = VisualTreeHelper.FindElementsInHostCoordinates(point, content).FirstOrDefault();
                if (top is not null && !IsPlaceholderLine(top)) return true;
            }
        }

        return false;
    }

    private bool IsPlaceholderLine(UIElement element)
    {
        for (DependencyObject? e = element; e is not null; e = VisualTreeHelper.GetParent(e))
        {
            if (e == _placeholder) return true;
        }

        for (DependencyObject? e = _placeholder; e is not null; e = VisualTreeHelper.GetParent(e))
        {
            if (e == element) return true;
        }

        return false;
    }

    // ---------------- window ----------------

    private bool EnsureWindow()
    {
        if (_hwnd != 0 && IsWindow(_hwnd)) return true;
        _hwnd = 0;
        if (_area.XamlRoot?.ContentIslandEnvironment is not { } environment) return false;
        var parent = Microsoft.UI.Win32Interop.GetWindowFromWindowId(environment.AppWindowId);
        if (parent == 0) return false;

        RegisterClass();
        _hwnd = Native.CreateWindowExW(WS_EX_NOPARENTNOTIFY, ClassName, "", WS_CHILD | WS_CLIPCHILDREN | WS_CLIPSIBLINGS, 0, 0, 1, 1, parent, 0, Native.GetModuleHandleW(null), 0);
        _shown = false;
        _pixels = default;
        if (_hwnd == 0) CrashLog.Trace($"preview handler host window not created: {Marshal.GetLastPInvokeError()}");
        return _hwnd != 0;
    }

    private static unsafe void RegisterClass()
    {
        if (_classRegistered) return;
        fixed (char* cls = ClassName)
        {
            var wc = new Native.WNDCLASSEXW
            {
                cbSize = (uint)sizeof(Native.WNDCLASSEXW),
                lpfnWndProc = &HostProc,
                hInstance = Native.GetModuleHandleW(null),
                lpszClassName = cls,
            };
            Native.RegisterClassExW(&wc);
        }

        _classRegistered = true;
    }

    /// <summary>A Pollux theme colour as COLORREF (0x00BBGGRR); also the brush the host window fills its background with.</summary>
    private static uint ColorRef(string key)
    {
        var c = Theme.Brush(key) is SolidColorBrush s ? s.Color : Microsoft.UI.Colors.White;
        var colorRef = (uint)(c.R | (c.G << 8) | (c.B << 16));
        if (key == "PolluxSurface2Brush" && colorRef != _brushColor)
        {
            var old = _brush;
            _brush = CreateSolidBrush(colorRef);
            _brushColor = colorRef;
            if (old != 0) DeleteObject(old);
        }

        return colorRef;
    }

    /// <summary>Paints the theme background where the handler does not draw (while it loads, at its edges).</summary>
    [UnmanagedCallersOnly]
    private static nint HostProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        if (msg == WM_ERASEBKGND && _brush != 0 && GetClientRect(hwnd, out var rect))
        {
            FillRect(wParam, rect, _brush);
            return 1;
        }

        return Native.DefWindowProcW(hwnd, msg, wParam, lParam);
    }
}
