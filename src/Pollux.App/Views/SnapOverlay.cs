using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Pollux.App.Interop;
using Pollux.App.Services;
using Pollux.Core.Snap;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics;
using Windows.System;

namespace Pollux.App.Views;

/// <summary>
/// The Snap overlay on one monitor: shows that monitor's part of the frozen screen, dims it, and lets the user
/// drag out an area with a magnifier at the cursor. One overlay per monitor; <see cref="SnapService"/> runs them together.
/// </summary>
internal sealed partial class SnapOverlay : Window
{
    private readonly SnapSurface _surface;

    public SnapOverlay()
    {
        Title = "Pollux Snap";
        // Own presenter instead of "AppWindow.Presenter is OverlappedPresenter": that type test fails under Native AOT.
        var p = OverlappedPresenter.Create();
        p.IsAlwaysOnTop = true;
        p.IsResizable = false;
        p.IsMaximizable = false;
        p.IsMinimizable = false;
        p.SetBorderAndTitleBar(false, false);
        AppWindow.SetPresenter(p);
        AppWindow.IsShownInSwitchers = false;

        _surface = new SnapSurface(this);
        Content = _surface;
        AppWindow.Closing += (_, e) =>
        {
            e.Cancel = true;
            Finished?.Invoke(this, null);
        };
        Activated += (_, e) =>
        {
            if (e.WindowActivationState == WindowActivationState.Deactivated) Deactivated?.Invoke(this);
        };
    }

    /// <summary>The user chose an area (or cancelled: null).</summary>
    public event Action<SnapOverlay, SnapSelection?>? Finished;

    /// <summary>The overlay lost activation; the service decides whether focus went to another overlay or elsewhere.</summary>
    public event Action<SnapOverlay>? Deactivated;

    /// <summary>The colour under the cursor was copied with C.</summary>
    public event Action<string>? ColorCopied;

    public nint Hwnd => WinRT.Interop.WindowNative.GetWindowHandle(this);

    public SnapMonitor? Monitor => _surface.Monitor;

    /// <summary>
    /// Covers <paramref name="monitor"/> with its part of <paramref name="screen"/>; activates it when <paramref name="activate"/>.
    /// With <paramref name="windowsOnly"/> a click takes the window under the cursor and dragging an area is off.
    /// </summary>
    public void Open(FrozenScreen screen, SnapMonitor monitor, bool activate, bool windowsOnly)
    {
        _surface.Load(screen, monitor, windowsOnly);
        var b = monitor.Bounds;
        AppWindow.MoveAndResize(new RectInt32(b.X, b.Y, b.Width, b.Height));
        AppWindow.Show(activate);
        if (activate) Focus();
    }

    public void Focus()
    {
        Activate();
        Native.SetForegroundWindow(Hwnd);
        _surface.Focus(FocusState.Programmatic);
    }

    /// <summary>Hides the overlay and drops the frozen image.</summary>
    public void Dismiss()
    {
        AppWindow.Hide();
        _surface.Unload();
    }

    internal void RaiseFinished(SnapSelection? selection) => Finished?.Invoke(this, selection);

    internal void RaiseColorCopied(string hex) => ColorCopied?.Invoke(hex);

    /// <summary>The overlay's content: frozen image, shade, selection frame and magnifier. A control so it can take keys and set the cursor.</summary>
    private sealed partial class SnapSurface : UserControl
    {
        private const int LoupeCells = 11;
        private const int LoupeZoom = 10;
        private const double LoupeGap = 24;

        private static readonly SolidColorBrush Shade = new(ColorHelper.FromArgb(0x70, 0, 0, 0));

        private readonly SnapOverlay _owner;
        private readonly Canvas _canvas = new();
        private readonly Image _image = new() { Stretch = Stretch.Fill };
        private readonly Rectangle[] _shades = [new(), new(), new(), new()];
        private readonly Rectangle _frame = new() { StrokeThickness = 1, Visibility = Visibility.Collapsed };
        private readonly Border _loupe = new() { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(0, 0, 0, 4) };
        private readonly Image _loupeImage = new() { Width = LoupeCells * LoupeZoom, Height = LoupeCells * LoupeZoom, Stretch = Stretch.Fill };
        private readonly TextBlock _loupeText = new() { FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(6, 4, 6, 0) };
        private readonly WriteableBitmap _loupeBitmap = new(LoupeCells * LoupeZoom, LoupeCells * LoupeZoom);
        private readonly byte[] _loupePixels = new byte[LoupeCells * LoupeZoom * LoupeCells * LoupeZoom * 4];

        private FrozenScreen? _screen;
        private SnapSelection? _selection;
        private SnapWindow? _hover;
        private bool _windowsOnly;
        private bool _dragging;
        private bool _spaceHeld;
        private PixelPoint _last;

        public SnapSurface(SnapOverlay owner)
        {
            _owner = owner;
            IsTabStop = true;
            UseSystemFocusVisuals = false;
            ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.Cross);

            _canvas.Children.Add(_image);
            foreach (var s in _shades)
            {
                s.Fill = Shade;
                _canvas.Children.Add(s);
            }

            _canvas.Children.Add(_frame);
            var loupe = new StackPanel();
            loupe.Children.Add(_loupeImage);
            loupe.Children.Add(_loupeText);
            _loupe.Child = loupe;
            _loupeImage.Source = _loupeBitmap;
            _canvas.Children.Add(_loupe);
            Content = _canvas;

            PointerPressed += OnPressed;
            PointerMoved += OnMoved;
            PointerReleased += OnReleased;
        }

        public SnapMonitor? Monitor { get; private set; }

        private double DisplayScale => Monitor?.Scale ?? 1.0;

        public void Load(FrozenScreen screen, SnapMonitor monitor, bool windowsOnly)
        {
            _screen = screen;
            Monitor = monitor;
            _windowsOnly = windowsOnly;
            _hover = null;
            _selection = null;
            _dragging = false;
            _spaceHeld = false;

            RequestedTheme = Theme.Current;
            _frame.Stroke = Theme.Brush("PolluxAccentBrush");
            _loupe.BorderBrush = Theme.Brush("PolluxAccentBrush");
            _loupe.Background = Theme.Brush("PolluxSurface2Brush");
            _loupeText.Foreground = Theme.Brush("PolluxTextBrush");
            _loupeText.FontFamily = Ui.Mono;

            var b = monitor.Bounds;
            var bitmap = new WriteableBitmap(b.Width, b.Height);
            using (var stream = bitmap.PixelBuffer.AsStream()) stream.Write(screen.Crop(b));
            _image.Source = bitmap;
            _image.Width = b.Width / DisplayScale;
            _image.Height = b.Height / DisplayScale;

            Native.GetCursorPos(out var c);
            _last = new PixelPoint(c.X, c.Y);
            _hover = HoveredWindow();
            Render();
        }

        public void Unload()
        {
            _image.Source = null;
            _screen = null;
        }

        private void OnPressed(object sender, PointerRoutedEventArgs e)
        {
            if (Monitor is null || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            CapturePointer(e.Pointer);
            _last = ToPhysical(e);
            _selection = _windowsOnly ? null : SnapSelection.Start(Monitor, _last);
            _dragging = true;
            Render();
        }

        private void OnMoved(object sender, PointerRoutedEventArgs e)
        {
            if (Monitor is null) return;
            var p = ToPhysical(e);
            if (_dragging && _selection is not null)
                _selection = _spaceHeld ? _selection.MoveBy(p.X - _last.X, p.Y - _last.Y) : _selection.WithEnd(p);
            _last = p;
            if (!_dragging) _hover = HoveredWindow();
            Render();
        }

        private void OnReleased(object sender, PointerRoutedEventArgs e)
        {
            if (!_dragging) return;
            _dragging = false;
            ReleasePointerCapture(e.Pointer);
            if (_selection is { IsEmpty: false }) _owner.RaiseFinished(_selection);
            else if (WindowSelection() is { } window) _owner.RaiseFinished(window);
            else
            {
                _selection = null;
                Render();
            }
        }

        protected override void OnKeyDown(KeyRoutedEventArgs e)
        {
            if (Monitor is null) return;
            var step = Native.GetKeyState((int)VirtualKey.Shift) < 0 ? 10 : 1;
            switch (e.Key)
            {
                case VirtualKey.Escape:
                    _owner.RaiseFinished(null);
                    break;
                case VirtualKey.Enter:
                    _owner.RaiseFinished(_selection is { IsEmpty: false } ? _selection
                        : _windowsOnly && WindowSelection() is { } window ? window
                        : SnapSelection.WholeMonitor(Monitor));
                    break;
                case VirtualKey.Left: Nudge(-step, 0); break;
                case VirtualKey.Right: Nudge(step, 0); break;
                case VirtualKey.Up: Nudge(0, -step); break;
                case VirtualKey.Down: Nudge(0, step); break;
                case VirtualKey.Space:
                    _spaceHeld = true;
                    break;
                case VirtualKey.C when _screen is not null:
                    _owner.RaiseColorCopied(_screen.ColorAt(_last));
                    break;
                default:
                    return;
            }

            e.Handled = true;
        }

        protected override void OnKeyUp(KeyRoutedEventArgs e)
        {
            if (e.Key == VirtualKey.Space) _spaceHeld = false;
        }

        /// <summary>Arrow keys move the cursor by whole physical pixels; while dragging they move the dragged corner.</summary>
        private void Nudge(int dx, int dy)
        {
            if (Monitor is null) return;
            PixelPoint target;
            if (_dragging && _selection is not null)
            {
                _selection = _selection.MoveEndBy(dx, dy);
                target = _selection.End;
            }
            else
            {
                var b = Monitor.Bounds;
                target = new PixelPoint(Math.Clamp(_last.X + dx, b.X, b.Right - 1), Math.Clamp(_last.Y + dy, b.Y, b.Bottom - 1));
            }

            _last = target;
            Native.SetCursorPos(target.X, target.Y);
            Render();
        }

        /// <summary>The window under the cursor in the frozen screen (a click without drag takes it).</summary>
        private SnapWindow? HoveredWindow() => _screen is null ? null : SnapWindowPicker.At(_last, _screen.Windows, []);

        /// <summary>The hovered window cut to this monitor; null when there is none.</summary>
        private SnapSelection? WindowSelection() =>
            _hover is null || Monitor is null ? null : SnapSelection.Of(Monitor, _hover.Bounds) is { IsEmpty: false } s ? s : null;

        private PixelPoint ToPhysical(PointerRoutedEventArgs e)
        {
            var p = e.GetCurrentPoint(this).Position;
            return Monitor!.ToPhysical(p.X, p.Y);
        }

        private double X(int physical) => (physical - Monitor!.Bounds.X) / DisplayScale;

        private double Y(int physical) => (physical - Monitor!.Bounds.Y) / DisplayScale;

        private void Render()
        {
            if (Monitor is null || _screen is null) return;
            var w = Monitor.Bounds.Width / DisplayScale;
            var h = Monitor.Bounds.Height / DisplayScale;

            var shown = _selection is { IsEmpty: false } ? _selection : _dragging ? null : WindowSelection();
            if (shown is not null)
            {
                var a = shown.Area;
                double left = X(a.X), top = Y(a.Y), right = X(a.Right), bottom = Y(a.Bottom);
                Place(_shades[0], 0, 0, w, top);
                Place(_shades[1], 0, bottom, w, h - bottom);
                Place(_shades[2], 0, top, left, bottom - top);
                Place(_shades[3], right, top, w - right, bottom - top);
                Place(_frame, left, top, right - left, bottom - top);
                _frame.Visibility = Visibility.Visible;
                _frame.StrokeThickness = shown == _selection ? 1 : 2;
                _loupeText.Text = shown == _selection ? $"{a.Width} × {a.Height}" : PositionAndColor();
            }
            else
            {
                Place(_shades[0], 0, 0, w, h);
                foreach (var r in _shades.Skip(1)) Place(r, 0, 0, 0, 0);
                _frame.Visibility = Visibility.Collapsed;
                _loupeText.Text = PositionAndColor();
            }

            RenderLoupe();
            var size = LoupeCells * LoupeZoom;
            var lx = X(_last.X) + LoupeGap;
            var ly = Y(_last.Y) + LoupeGap;
            if (lx + size > w) lx = X(_last.X) - LoupeGap - size;
            if (ly + size + 24 > h) ly = Y(_last.Y) - LoupeGap - size - 24;
            Canvas.SetLeft(_loupe, lx);
            Canvas.SetTop(_loupe, ly);
        }

        private string PositionAndColor() =>
            $"{_last.X - Monitor!.Bounds.X}, {_last.Y - Monitor.Bounds.Y}  {_screen!.ColorAt(_last)}";

        /// <summary>Draws the pixels around the cursor enlarged, without smoothing, with the centre pixel outlined.</summary>
        private void RenderLoupe()
        {
            var screen = _screen!;
            var v = screen.Layout.VirtualBounds;
            var side = LoupeCells * LoupeZoom;
            var half = LoupeCells / 2;
            for (var cy = 0; cy < LoupeCells; cy++)
            {
                for (var cx = 0; cx < LoupeCells; cx++)
                {
                    var sx = Math.Clamp(_last.X - half + cx - v.X, 0, screen.Width - 1);
                    var sy = Math.Clamp(_last.Y - half + cy - v.Y, 0, screen.Height - 1);
                    var src = ((sy * screen.Width) + sx) * 4;
                    var centre = cx == half && cy == half;
                    for (var py = 0; py < LoupeZoom; py++)
                    {
                        for (var px = 0; px < LoupeZoom; px++)
                        {
                            var dst = ((((cy * LoupeZoom) + py) * side) + (cx * LoupeZoom) + px) * 4;
                            var edge = centre && (px == 0 || py == 0 || px == LoupeZoom - 1 || py == LoupeZoom - 1);
                            _loupePixels[dst] = edge ? (byte)0 : screen.Bgra[src];
                            _loupePixels[dst + 1] = edge ? (byte)0 : screen.Bgra[src + 1];
                            _loupePixels[dst + 2] = edge ? (byte)255 : screen.Bgra[src + 2];
                            _loupePixels[dst + 3] = 255;
                        }
                    }
                }
            }

            using (var stream = _loupeBitmap.PixelBuffer.AsStream()) stream.Write(_loupePixels);
            _loupeBitmap.Invalidate();
        }

        private static void Place(FrameworkElement e, double x, double y, double w, double h)
        {
            Canvas.SetLeft(e, x);
            Canvas.SetTop(e, y);
            e.Width = Math.Max(0, w);
            e.Height = Math.Max(0, h);
        }
    }
}
