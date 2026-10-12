using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Pollux.App.Services;
using Pollux.Core.Files;
using Windows.Foundation;
using VirtualKeyModifiers = Windows.System.VirtualKeyModifiers;

namespace Pollux.App.Views;

/// <summary>
/// Rubber-band selection on a pane list, like Explorer: a left-button drag from empty space draws a rectangle and
/// selects the items it touches, live. The rectangle is kept in the items panel's coordinates, so it stays anchored
/// to the content while the list scrolls (by wheel or by auto-scroll at the edges).
/// </summary>
/// <remarks>
/// Hit geometry: realized containers count with their actual bounds; every other item through an <see cref="ItemLayout"/>
/// calibrated on a realized container (fixed row height or wrap cell, plus group headers). Items scrolled out of the
/// realized range therefore keep the state the marquee gives them, without realizing them.
/// </remarks>
internal sealed class MarqueeSelection
{
    private readonly ListView _list;
    private readonly Canvas _layer;
    private readonly Action _pressed;
    private readonly Border _box = new() { BorderThickness = new Thickness(1), IsHitTestVisible = false };
    private readonly DispatcherQueueTimer _scrollTimer;
    private ScrollViewer? _scroller;
    private Panel? _panel;
    private uint? _pointer;
    private bool _dragging;
    private object? _source;
    /// <summary>The press point in panel coordinates.</summary>
    private Point _anchor;
    /// <summary>The last pointer position in list coordinates: it stays put on screen while the content scrolls.</summary>
    private Point _last;
    private MarqueeMode _mode;
    private HashSet<int> _initial = [];
    private HashSet<int> _hits = [];
    /// <summary>A group header's height, kept from the last time one was realized.</summary>
    private double _headerHeight = 24;

    /// <param name="pressed">Called when a press on empty space starts a possible marquee (activates the Pane).</param>
    public MarqueeSelection(ListView list, Canvas layer, Action pressed)
    {
        _list = list;
        _layer = layer;
        _pressed = pressed;
        _scrollTimer = list.DispatcherQueue.CreateTimer();
        _scrollTimer.Interval = TimeSpan.FromMilliseconds(16);
        _scrollTimer.Tick += (_, _) => AutoScroll();
        // handledEventsToo: the list's ScrollViewer may mark presses on empty space as handled.
        list.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnPressed), handledEventsToo: true);
        list.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(OnMoved), handledEventsToo: true);
        list.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(OnReleased), handledEventsToo: true);
        list.PointerCaptureLost += (_, _) => End();
        list.PointerCanceled += (_, _) => End();
    }

    /// <summary>Whether a press on empty space is held (a click or a marquee in progress).</summary>
    public bool IsActive => _pointer is not null;

    private void OnPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_pointer is not null || e.Pointer.PointerDeviceType != PointerDeviceType.Mouse) return;
        var point = e.GetCurrentPoint(_list);
        if (!point.Properties.IsLeftButtonPressed || !IsEmptySpace(e.OriginalSource as DependencyObject)) return;
        if (_list.ItemsPanelRoot is not { } panel || Scroller() is not { } scroller) return;

        _panel = panel;
        _scroller = scroller;
        _mode = Marquee.ModeFor(e.KeyModifiers.HasFlag(VirtualKeyModifiers.Control), e.KeyModifiers.HasFlag(VirtualKeyModifiers.Shift));
        _pressed();
        _list.Focus(FocusState.Pointer);
        // Like Explorer, a plain press on empty space clears the selection, so a plain click there does too.
        if (_mode == MarqueeMode.Replace) _list.SelectedItems.Clear();
        _initial = _mode == MarqueeMode.Replace ? [] : SelectedIndexes();
        _hits = [];
        _source = _list.ItemsSource;
        _anchor = e.GetCurrentPoint(panel).Position;
        _last = point.Position;
        _pointer = e.Pointer.PointerId;
        _dragging = false;
        _list.CapturePointer(e.Pointer);
    }

    private void OnMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_pointer != e.Pointer.PointerId || _panel is null) return;
        _last = e.GetCurrentPoint(_list).Position;
        if (!_dragging)
        {
            var p = ContentPoint();
            if (Math.Abs(p.X - _anchor.X) < Marquee.DragThreshold && Math.Abs(p.Y - _anchor.Y) < Marquee.DragThreshold) return;
            _dragging = true;
            var accent = Theme.Accent;
            _box.BorderBrush = new SolidColorBrush(accent);
            _box.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x40, accent.R, accent.G, accent.B));
            _layer.Children.Add(_box);
            _scroller!.ViewChanged += Scroller_ViewChanged;
            _scrollTimer.Start();
        }

        Update();
        e.Handled = true;
    }

    private void OnReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_pointer != e.Pointer.PointerId) return;
        if (_dragging) e.Handled = true;
        End();
    }

    private void Scroller_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e) => Update();

    private void End()
    {
        if (_pointer is null) return;
        _pointer = null;
        if (_dragging)
        {
            _dragging = false;
            _scrollTimer.Stop();
            _scroller!.ViewChanged -= Scroller_ViewChanged;
            _layer.Children.Remove(_box);
        }

        _list.ReleasePointerCaptures();
        _panel = null;
        _scroller = null;
        _source = null;
        _initial = [];
        _hits = [];
    }

    /// <summary>Empty list space: not an item, a group header, a scroll bar or a control inside the list.</summary>
    private bool IsEmptySpace(DependencyObject? source)
    {
        for (var d = source; d is not null; d = VisualTreeHelper.GetParent(d))
        {
            if (d == _list) return true;
            if (d is ListViewItem or ListViewHeaderItem or ScrollBar or ButtonBase or TextBox) return false;
        }

        return false;
    }

    private ScrollViewer? Scroller()
    {
        for (var d = _list.ItemsPanelRoot as DependencyObject; d is not null && d != _list; d = VisualTreeHelper.GetParent(d))
        {
            if (d is ScrollViewer sv) return sv;
        }

        return null;
    }

    private HashSet<int> SelectedIndexes()
    {
        var set = new HashSet<int>();
        foreach (var range in _list.SelectedRanges)
        {
            for (var i = range.FirstIndex; i <= range.LastIndex; i++) set.Add(i);
        }

        return set;
    }

    private Point ContentPoint() => _list.TransformToVisual(_panel).TransformPoint(_last);

    private void AutoScroll()
    {
        if (!_dragging || _scroller is not { } sv) return;
        var y = _list.TransformToVisual(sv).TransformPoint(_last).Y;
        var step = Marquee.AutoScrollStep(y, 0, sv.ViewportHeight);
        if (step == 0) return;
        var offset = Math.Clamp(sv.VerticalOffset + step, 0, sv.ScrollableHeight);
        if (offset != sv.VerticalOffset) sv.ChangeView(null, offset, null, disableAnimation: true);
    }

    private void Update()
    {
        if (!_dragging || _panel is null) return;
        // A refresh or navigation replaced the items: the indexes no longer mean what they meant at the press.
        if (!ReferenceEquals(_list.ItemsSource, _source))
        {
            End();
            return;
        }

        var p = ContentPoint();
        var box = LayoutBox.Span(_anchor.X, _anchor.Y, p.X, p.Y);
        Draw(box);
        var hits = HitsOf(box);
        var (select, deselect) = Marquee.Changes(_mode, _initial, _hits, hits);
        _hits = hits;
        foreach (var (first, length) in Marquee.Runs(deselect)) _list.DeselectRange(new ItemIndexRange(first, (uint)length));
        foreach (var (first, length) in Marquee.Runs(select)) _list.SelectRange(new ItemIndexRange(first, (uint)length));
    }

    /// <summary>Shows the marquee in the overlay, clipped to the list's viewport.</summary>
    private void Draw(LayoutBox box)
    {
        var r = _panel!.TransformToVisual(_layer).TransformBounds(new Rect(box.Left, box.Top, box.Width, box.Height));
        var view = _scroller!.TransformToVisual(_layer).TransformBounds(new Rect(0, 0, _scroller.ActualWidth, _scroller.ActualHeight));
        var left = Math.Max(r.Left, view.Left);
        var top = Math.Max(r.Top, view.Top);
        var right = Math.Min(r.Right, view.Right);
        var bottom = Math.Min(r.Bottom, view.Bottom);
        _box.Visibility = right > left && bottom > top ? Visibility.Visible : Visibility.Collapsed;
        Canvas.SetLeft(_box, left);
        Canvas.SetTop(_box, top);
        _box.Width = Math.Max(0, right - left);
        _box.Height = Math.Max(0, bottom - top);
    }

    private HashSet<int> HitsOf(LayoutBox box)
    {
        var count = _list.Items.Count;
        var hits = new HashSet<int>();
        var realized = Realized(count);
        if (Layout(realized) is { } layout) hits.UnionWith(layout.Hits(box, count));
        foreach (var (index, _, bounds) in realized)
        {
            if (box.Hits(bounds)) hits.Add(index);
            else hits.Remove(index);
        }

        return hits;
    }

    /// <summary>The realized containers with their bounds in panel coordinates.</summary>
    private List<(int Index, ListViewItem Container, LayoutBox Bounds)> Realized(int count)
    {
        var list = new List<(int, ListViewItem, LayoutBox)>();
        var (first, last) = _panel switch
        {
            ItemsStackPanel s => (s.FirstCacheIndex, s.LastCacheIndex),
            ItemsWrapGrid g => (g.FirstCacheIndex, g.LastCacheIndex),
            _ => (0, count - 1),
        };
        if (first < 0) return list;
        for (var i = first; i <= Math.Min(last, count - 1); i++)
        {
            if (_list.ContainerFromIndex(i) is not ListViewItem c || c.Visibility != Visibility.Visible || c.ActualHeight <= 0) continue;
            var r = c.TransformToVisual(_panel).TransformBounds(new Rect(0, 0, c.ActualWidth, c.ActualHeight));
            list.Add((i, c, new LayoutBox(r.Left, r.Top, r.Right, r.Bottom)));
        }

        return list;
    }

    /// <summary>The layout of all items, calibrated on the first realized container; null when none is realized.</summary>
    private ItemLayout? Layout(List<(int Index, ListViewItem Container, LayoutBox Bounds)> realized)
    {
        if (realized.Count == 0) return null;
        var (index, c, bounds) = realized[0];
        var x = bounds.Left - c.Margin.Left;
        var y = bounds.Top - c.Margin.Top;
        switch (_panel)
        {
            case ItemsWrapGrid g:
            {
                var width = double.IsNaN(g.ItemWidth) ? c.ActualWidth + c.Margin.Left + c.Margin.Right : g.ItemWidth;
                var height = double.IsNaN(g.ItemHeight) ? c.ActualHeight + c.Margin.Top + c.Margin.Bottom : g.ItemHeight;
                var columns = g.MaximumRowsOrColumns > 0 ? g.MaximumRowsOrColumns : Math.Max(1, (int)Math.Floor((g.ActualWidth + 0.5) / width));
                return new WrapItemLayout(x - index % columns * width, y - index / columns * height, width, height, columns);
            }

            case ItemsStackPanel:
            {
                var rowHeight = c.ActualHeight + c.Margin.Top + c.Margin.Bottom;
                var starts = GroupStarts();
                if (starts is null) return new StackItemLayout(x, y - index * rowHeight, bounds.Right + c.Margin.Right, rowHeight);
                foreach (var (_, item, _) in realized)
                {
                    if (_list.GroupHeaderContainerFromItemContainer(item) is FrameworkElement { ActualHeight: > 0 } header)
                    {
                        _headerHeight = header.ActualHeight + header.Margin.Top + header.Margin.Bottom;
                        break;
                    }
                }

                var headers = starts.Count(s => s <= index);
                return new StackItemLayout(x, y - headers * _headerHeight - index * rowHeight, bounds.Right + c.Margin.Right, rowHeight, _headerHeight, starts);
            }

            default:
                return null;
        }
    }

    /// <summary>The flat index of each group's first item when the list is grouped, otherwise null.</summary>
    private List<int>? GroupStarts()
    {
        if (_list.ItemsSource is not ICollectionView { CollectionGroups: { Count: > 0 } groups }) return null;
        var starts = new List<int>(groups.Count);
        var next = 0;
        foreach (var g in groups)
        {
            starts.Add(next);
            next += (g as ICollectionViewGroup)?.GroupItems.Count ?? 0;
        }

        return starts;
    }
}
