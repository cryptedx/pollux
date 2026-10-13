using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Pollux.App.Interop;
using Pollux.App.Services;
using Pollux.Core.Snap;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Storage;
using Windows.System;

namespace Pollux.App.Views;

/// <summary>
/// A Float: one Snap kept on screen in a borderless, always-on-top window. Drag moves it, the wheel zooms, Ctrl+wheel
/// changes its opacity, T makes it click-through (Snap: Release Floats undoes that), Alt+drag drags the file out,
/// double-click opens it in the Snap Board, Esc or a middle click closes it. Floats are not restored after a restart.
/// </summary>
internal sealed partial class FloatWindow : Window
{
    private readonly FloatSurface _surface;
    private readonly byte[] _png;
    private readonly int _width;
    private readonly int _height;
    private double _opacity = 1.0;

    public FloatWindow(byte[] png, int width, int height, double scale, string? savedPath)
    {
        _png = png;
        _width = width;
        _height = height;
        Scale = scale;
        SavedPath = savedPath;
        Title = "Pollux Float";
        // Own presenter instead of "AppWindow.Presenter is OverlappedPresenter": that type test fails under Native AOT.
        var p = OverlappedPresenter.Create();
        p.IsAlwaysOnTop = true;
        p.IsMaximizable = false;
        p.IsMinimizable = false;
        p.IsResizable = false;
        p.PreferredMinimumWidth = 8;
        p.PreferredMinimumHeight = 8;
        p.SetBorderAndTitleBar(false, false);
        AppWindow.SetPresenter(p);
        AppWindow.IsShownInSwitchers = false;

        _surface = new FloatSurface(this);
        Content = _surface;
    }

    /// <summary>Double-click: open this Snap in the Snap Board.</summary>
    public event Action<FloatWindow>? BoardRequested;

    public byte[] Png => _png;
    public double Scale { get; }
    public string? SavedPath { get; }
    public nint Hwnd => WinRT.Interop.WindowNative.GetWindowHandle(this);
    public bool ClickThrough { get; private set; }

    /// <summary>Shows the Float at <paramref name="at"/> (physical pixels) and gives it the keyboard.</summary>
    public async Task ShowAtAsync(PixelRect at)
    {
        await _surface.LoadAsync(_png);
        AppWindow.MoveAndResize(new RectInt32(at.X, at.Y, at.Width, at.Height));
        AppWindow.Show(true);
        Activate();
        Native.SetForegroundWindow(Hwnd);
        _surface.Focus(FocusState.Programmatic);
    }

    /// <summary>Makes the Float take clicks again.</summary>
    public void Release()
    {
        if (!ClickThrough) return;
        ClickThrough = false;
        ApplyStyle();
    }

    private PixelRect Frame => new(AppWindow.Position.X, AppWindow.Position.Y, AppWindow.Size.Width, AppWindow.Size.Height);

    private void ZoomBy(int notches)
    {
        Native.GetCursorPos(out var c);
        var r = SnapFloat.Zoom(Frame, _width, _height, notches, new PixelPoint(c.X, c.Y));
        AppWindow.MoveAndResize(new RectInt32(r.X, r.Y, r.Width, r.Height));
    }

    private void FadeBy(int notches)
    {
        _opacity = SnapFloat.Opacity(_opacity, notches);
        ApplyStyle();
    }

    private void ToggleClickThrough()
    {
        ClickThrough = !ClickThrough;
        ApplyStyle();
    }

    /// <summary>Layered while faded or click-through (WS_EX_LAYERED with a constant alpha); transparent to the mouse while click-through.</summary>
    private void ApplyStyle()
    {
        var style = (long)Native.GetWindowLongPtr(Hwnd, Native.GWL_EXSTYLE);
        var layered = _opacity < 1.0 || ClickThrough;
        style = layered ? style | Native.WS_EX_LAYERED : style & ~Native.WS_EX_LAYERED;
        style = ClickThrough ? style | Native.WS_EX_TRANSPARENT : style & ~Native.WS_EX_TRANSPARENT;
        Native.SetWindowLongPtr(Hwnd, Native.GWL_EXSTYLE, (nint)style);
        if (layered) Native.SetLayeredWindowAttributes(Hwnd, 0, (byte)Math.Round(_opacity * 255), Native.LWA_ALPHA);
    }

    /// <summary>The Float's content: the image and a thin frame. A control so it can take keys.</summary>
    private sealed partial class FloatSurface : UserControl
    {
        private readonly FloatWindow _owner;
        private readonly Image _image = new() { Stretch = Stretch.Fill };
        private readonly Border _frame = new() { BorderThickness = new Thickness(1) };
        private PointInt32? _grab;
        private PointInt32 _origin;

        public FloatSurface(FloatWindow owner)
        {
            _owner = owner;
            IsTabStop = true;
            UseSystemFocusVisuals = false;
            ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeAll);
            _frame.Child = _image;
            Content = _frame;
            PointerPressed += OnPressed;
            PointerMoved += OnMoved;
            PointerReleased += OnReleased;
            PointerWheelChanged += OnWheel;
            DoubleTapped += (_, _) => _owner.BoardRequested?.Invoke(_owner);
            CanDrag = false;
            DragStarting += OnDragStarting;
        }

        public async Task LoadAsync(byte[] png)
        {
            RequestedTheme = Theme.Current;
            _frame.BorderBrush = Theme.Brush("PolluxAccentBrush");
            var bitmap = new BitmapImage();
            using var stream = new MemoryStream(png, writable: false);
            await bitmap.SetSourceAsync(stream.AsRandomAccessStream());
            _image.Source = bitmap;
        }

        private void OnPressed(object sender, PointerRoutedEventArgs e)
        {
            var props = e.GetCurrentPoint(this).Properties;
            if (props.IsMiddleButtonPressed)
            {
                _owner.Close();
                return;
            }

            if (!props.IsLeftButtonPressed) return;
            if ((e.KeyModifiers & VirtualKeyModifiers.Menu) != 0)
            {
                _ = StartDragAsync(e.GetCurrentPoint(this));
                return;
            }

            Native.GetCursorPos(out var c);
            _grab = new PointInt32(c.X, c.Y);
            _origin = _owner.AppWindow.Position;
            CapturePointer(e.Pointer);
        }

        private void OnMoved(object sender, PointerRoutedEventArgs e)
        {
            if (_grab is not { } grab) return;
            Native.GetCursorPos(out var c);
            _owner.AppWindow.Move(new PointInt32(_origin.X + c.X - grab.X, _origin.Y + c.Y - grab.Y));
        }

        private void OnReleased(object sender, PointerRoutedEventArgs e)
        {
            _grab = null;
            ReleasePointerCapture(e.Pointer);
        }

        private void OnWheel(object sender, PointerRoutedEventArgs e)
        {
            var notches = e.GetCurrentPoint(this).Properties.MouseWheelDelta / 120;
            if (notches == 0) return;
            if ((e.KeyModifiers & VirtualKeyModifiers.Control) != 0) _owner.FadeBy(notches);
            else _owner.ZoomBy(notches);
            e.Handled = true;
        }

        protected override void OnKeyDown(KeyRoutedEventArgs e)
        {
            var ctrl = Native.GetKeyState((int)VirtualKey.Control) < 0;
            switch (e.Key)
            {
                case VirtualKey.Escape:
                    _owner.Close();
                    break;
                case VirtualKey.T:
                    _owner.ToggleClickThrough();
                    break;
                case VirtualKey.C when ctrl:
                    _ = SnapService.CopyAsync(_owner.Png);
                    break;
                default:
                    return;
            }

            e.Handled = true;
        }

        /// <summary>Alt+drag: the Snap's own file when it has one, else a temporary PNG.</summary>
        private async void OnDragStarting(UIElement sender, Microsoft.UI.Xaml.DragStartingEventArgs e)
        {
            var deferral = e.GetDeferral();
            try
            {
                var path = _owner.SavedPath is { } saved && File.Exists(saved)
                    ? saved
                    : Path.Combine(Path.GetTempPath(), "Pollux", "Snaps", SnapFileName.For(DateTime.Now, _ => false));
                if (path != _owner.SavedPath && !await SnapService.WriteAsync(path, _owner.Png))
                {
                    e.Cancel = true;
                    return;
                }

                e.Data.RequestedOperation = DataPackageOperation.Copy;
                e.Data.SetStorageItems([await StorageFile.GetFileFromPathAsync(path)]);
            }
            finally
            {
                deferral.Complete();
            }
        }
    }
}
