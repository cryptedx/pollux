using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Pollux.App.Services;
using Windows.Graphics;

namespace Pollux.App.Views;

/// <summary>
/// A short hint after pasting from the Clipboard Stack ("Copied – press Ctrl+V"), shown near the bottom of the screen
/// without taking focus from the window the user pastes into. Hides itself after a few seconds.
/// </summary>
internal sealed partial class ClipHint : Window
{
    private readonly Grid _root = new();
    private readonly Border _frame = new() { BorderThickness = new Thickness(1), Padding = new Thickness(14, 8, 14, 8) };
    private readonly TextBlock _text = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly DispatcherQueueTimer _timer;

    public ClipHint()
    {
        Title = "Pollux Clipboard Hint";
        // Own presenter instead of "AppWindow.Presenter is OverlappedPresenter": that type test fails under Native AOT.
        var p = OverlappedPresenter.Create();
        p.IsAlwaysOnTop = true;
        p.IsResizable = false;
        p.IsMaximizable = false;
        p.IsMinimizable = false;
        p.SetBorderAndTitleBar(false, false);
        AppWindow.SetPresenter(p);

        AppWindow.IsShownInSwitchers = false;
        _frame.Child = _text;
        _root.Children.Add(_frame);
        Content = _root;
        AppWindow.Closing += (_, e) =>
        {
            e.Cancel = true;
            AppWindow.Hide();
        };
        _timer = DispatcherQueue.CreateTimer();
        _timer.IsRepeating = false;
        _timer.Tick += (_, _) => AppWindow.Hide();
    }

    /// <summary>Shows <paramref name="text"/> for <paramref name="seconds"/> without activating the hint.</summary>
    public void ShowHint(string text, int seconds = 3)
    {
        _root.RequestedTheme = Theme.Current;
        _frame.BorderBrush = Theme.Brush("PolluxLineBrush");
        _frame.Background = Theme.Brush("PolluxSurface2Brush");
        _text.FontFamily = Ui.Mono;
        _text.Text = text;
        Place();
        AppWindow.Show(false);
        _timer.Stop();
        _timer.Interval = TimeSpan.FromSeconds(seconds);
        _timer.Start();
    }

    /// <summary>Hides the hint now (before the screen is frozen for a delayed Snap).</summary>
    public void HideHint()
    {
        _timer.Stop();
        AppWindow.Hide();
    }

    private void Place()
    {
        Interop.Native.GetCursorPos(out var cursor);
        var area = DisplayArea.GetFromPoint(new PointInt32(cursor.X, cursor.Y), DisplayAreaFallback.Primary).WorkArea;
        var scale = Interop.Native.GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
        var w = Math.Min((int)(480 * scale), area.Width);
        var h = Math.Min((int)(44 * scale), area.Height);
        AppWindow.MoveAndResize(new RectInt32(area.X + ((area.Width - w) / 2), area.Y + area.Height - h - (int)(48 * scale), w, h));
    }
}
