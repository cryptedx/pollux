using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Pollux.App.Services;
using Pollux.Core.Files;

namespace Pollux.App.Views;

/// <summary>
/// The Details column header. A click on a cell sorts by it; dragging the gap after a cell resizes that column
/// (double-click the gap: default width again); dragging a cell moves the column. Resizing shows live in the rows,
/// which share <see cref="Columns"/>; <see cref="LayoutCommitted"/> fires once the mouse is released.
/// </summary>
public sealed partial class DetailsHeader : Grid
{
    private const double DragThreshold = 6;
    private const long DoubleClickMs = 500;
    private static readonly SolidColorBrush Clear = new(Microsoft.UI.Colors.Transparent);

    private readonly DetailsColumnsPanel _panel = new();
    private readonly Border _marker = new() { Width = 2, HorizontalAlignment = HorizontalAlignment.Left, Visibility = Visibility.Collapsed, IsHitTestVisible = false };
    private readonly Dictionary<string, (Border Cell, TextBlock Text)> _cells = new(StringComparer.OrdinalIgnoreCase);
    private DetailsColumns _columns = new();
    private Resizing? _resizing;
    private Pressing? _pressing;
    private Border? _hovered;
    private (string Id, long At)? _lastGripPress;

    private sealed record Resizing(ListColumn Column, double StartX, double StartWidth, ColumnLayout Before);

    private sealed class Pressing(ListColumn column, double startX)
    {
        public ListColumn Column { get; } = column;
        public double StartX { get; } = startX;
        public bool Dragging { get; set; }
        public int Index { get; set; } = -1;
    }

    public DetailsHeader()
    {
        foreach (var column in KnownColumns.All)
        {
            var text = new TextBlock
            {
                Text = column.Title,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                HorizontalAlignment = column.Alignment == ColumnAlignment.Right ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            };
            var cell = new Border { Child = text, Background = Clear, CornerRadius = new CornerRadius(3) };
            DetailsColumnsPanel.SetColumnId(cell, column.Id);
            _panel.Children.Add(cell);
            _cells[column.Id] = (cell, text);
        }

        _panel.Columns = _columns;
        Children.Add(_panel);
        Children.Add(_marker);
        PointerPressed += OnPressed;
        PointerMoved += OnMoved;
        PointerReleased += OnReleased;
        PointerCaptureLost += (_, _) => EndGesture(commit: true);
        PointerExited += (_, _) => Hover(null);
    }

    /// <summary>The layout this header shows and changes (shared with the rows).</summary>
    public DetailsColumns Columns
    {
        get => _columns;
        set
        {
            _columns = value;
            _panel.Columns = value;
        }
    }

    /// <summary>A plain click on a column's cell.</summary>
    public event Action<ListColumn>? SortClicked;

    /// <summary>A resize, reset or move ended with a new layout (to save).</summary>
    public event Action<ColumnLayout>? LayoutCommitted;

    /// <summary>Sets every cell's text; called when the sort or the listing kind changes, and on theme changes.</summary>
    public void SetTitles(Func<ListColumn, string> title)
    {
        var muted = Theme.Brush("PolluxMutedBrush");
        _marker.Background = Theme.Brush("PolluxAccentBrush");
        foreach (var column in KnownColumns.All)
        {
            var text = _cells[column.Id].Text;
            text.Text = title(column);
            text.Foreground = muted;
        }
    }

    private IReadOnlyList<ColumnPlacement> Placements() => _columns.Layout.Arrange(_panel.ActualWidth, _columns.Spacing);

    private double X(PointerRoutedEventArgs e) => e.GetCurrentPoint(_panel).Position.X;

    /// <summary>The column whose resize gap (its right edge plus the spacing after it) is under <paramref name="x"/>.</summary>
    private ListColumn? GripAt(double x) => ColumnLayout.GripAt(Placements(), x - _columns.Spacing / 2, _columns.Spacing / 2 + 1);

    private ColumnPlacement? CellAt(double x)
    {
        foreach (var p in Placements())
        {
            if (x >= p.X && x < p.X + p.Width) return p;
        }

        return null;
    }

    private void OnPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var x = X(e);
        if (GripAt(x) is { } grip)
        {
            // Double-click on a gap: default width. Detected here, as the press is captured for resizing.
            var now = Environment.TickCount64;
            if (_lastGripPress is { } last && last.Id == grip.Id && now - last.At <= DoubleClickMs)
            {
                _lastGripPress = null;
                e.Handled = true;
                ResetWidth(grip);
                return;
            }

            _lastGripPress = (grip.Id, now);
            var width = Placements().First(p => p.Column == grip).Width;
            _resizing = new Resizing(grip, x, width, _columns.Layout);
        }
        else if (CellAt(x) is { } cell)
        {
            _pressing = new Pressing(cell.Column, x);
        }
        else
        {
            return;
        }

        CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnMoved(object sender, PointerRoutedEventArgs e)
    {
        var x = X(e);
        if (_resizing is { } r)
        {
            // A width is only stored once the mouse moved: a click on a gap keeps a filling Name filling.
            if (x != r.StartX) _columns.Set(r.Before.Resize(r.Column.Id, r.StartWidth + x - r.StartX));
            return;
        }

        if (_pressing is { } p)
        {
            if (!p.Dragging && Math.Abs(x - p.StartX) < DragThreshold) return;
            p.Dragging = true;
            _cells[p.Column.Id].Cell.Opacity = 0.5;
            ShowMarker(p, x);
            return;
        }

        var overGrip = GripAt(x) is not null;
        ProtectedCursor = overGrip ? InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast) : null;
        Hover(overGrip || CellAt(x) is not { } c ? null : _cells[c.Column.Id].Cell);
    }

    private void ShowMarker(Pressing p, double x)
    {
        var placements = Placements();
        p.Index = ColumnLayout.DropIndex(placements, p.Column.Id, x);
        var others = placements.Where(q => q.Column != p.Column).ToList();
        var at = others.Count == 0 ? 0
            : p.Index < others.Count ? others[p.Index].X - _columns.Spacing / 2
            : others[^1].X + others[^1].Width + _columns.Spacing / 2;
        _marker.Margin = new Thickness(Padding.Left + at - 1, 0, 0, 0);
        _marker.Visibility = Visibility.Visible;
    }

    private void OnReleased(object sender, PointerRoutedEventArgs e)
    {
        var click = _pressing is { Dragging: false } p ? p.Column : null;
        EndGesture(commit: true);
        ReleasePointerCapture(e.Pointer);
        e.Handled = true;
        if (click is not null) SortClicked?.Invoke(click);
    }

    private void EndGesture(bool commit)
    {
        if (_resizing is { } r)
        {
            _resizing = null;
            if (commit && !_columns.Layout.SameAs(r.Before)) LayoutCommitted?.Invoke(_columns.Layout);
        }

        if (_pressing is { } p)
        {
            _pressing = null;
            _cells[p.Column.Id].Cell.Opacity = 1;
            _marker.Visibility = Visibility.Collapsed;
            if (commit && p.Dragging && p.Index >= 0)
            {
                var moved = _columns.Layout.Move(p.Column.Id, p.Index);
                if (!moved.SameAs(_columns.Layout))
                {
                    _columns.Set(moved);
                    LayoutCommitted?.Invoke(moved);
                }
            }
        }
    }

    private void ResetWidth(ListColumn column)
    {
        var reset = _columns.Layout.ResetWidth(column.Id);
        if (reset.SameAs(_columns.Layout)) return;
        _columns.Set(reset);
        LayoutCommitted?.Invoke(reset);
    }

    private void Hover(Border? cell)
    {
        if (ReferenceEquals(cell, _hovered)) return;
        if (_hovered is not null) _hovered.Background = Clear;
        _hovered = cell;
        if (cell is not null) cell.Background = Theme.Brush("PolluxHoverBrush");
    }
}
