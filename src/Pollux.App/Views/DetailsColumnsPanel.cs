using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Pollux.Core.Files;
using Windows.Foundation;

namespace Pollux.App.Views;

/// <summary>The Details column layout a Pane's header and rows share. UI thread only.</summary>
public sealed class DetailsColumns
{
    public ColumnLayout Layout { get; private set; } = ColumnLayout.Default;

    /// <summary>Space between two columns.</summary>
    public double Spacing { get; init; } = 8;

    /// <summary>The layout changed: every panel showing it lays out again.</summary>
    public event EventHandler? Changed;

    public void Set(ColumnLayout layout)
    {
        if (ReferenceEquals(layout, Layout)) return;
        Layout = layout;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>
/// Lays out the cells of a Details header or row by a shared <see cref="DetailsColumns"/>: each child names its column
/// with the attached <c>ColumnId</c> and gets that column's place and width; cells of hidden columns are collapsed.
/// Cells beyond the panel's width (a Name too wide to fit) are clipped.
/// The layout comes from <see cref="Columns"/> or, when unset, from the nearest ancestor with the attached <c>Source</c>
/// (set it once on the ListView, so item templates need no binding).
/// </summary>
public sealed partial class DetailsColumnsPanel : Panel
{
    public static readonly DependencyProperty ColumnIdProperty =
        DependencyProperty.RegisterAttached("ColumnId", typeof(string), typeof(DetailsColumnsPanel), new PropertyMetadata(null));

    public static readonly DependencyProperty SourceProperty =
        DependencyProperty.RegisterAttached("Source", typeof(object), typeof(DetailsColumnsPanel), new PropertyMetadata(null));

    private DetailsColumns? _columns;
    private DetailsColumns? _subscribed;

    public DetailsColumnsPanel()
    {
        Loaded += (_, _) => Subscribe();
        Unloaded += (_, _) => Unsubscribe();
    }

    public static string? GetColumnId(DependencyObject element) => (string?)element.GetValue(ColumnIdProperty);

    public static void SetColumnId(DependencyObject element, string? value) => element.SetValue(ColumnIdProperty, value);

    public static object? GetSource(DependencyObject element) => element.GetValue(SourceProperty);

    public static void SetSource(DependencyObject element, object? value) => element.SetValue(SourceProperty, value);

    /// <summary>The layout to follow; null looks for an ancestor's <c>Source</c> once loaded.</summary>
    public DetailsColumns? Columns
    {
        get => _columns;
        set
        {
            _columns = value;
            if (IsLoaded) Subscribe();
        }
    }

    private DetailsColumns? Effective => _columns ?? _subscribed;

    private void Subscribe()
    {
        var columns = _columns ?? FindSource();
        if (ReferenceEquals(columns, _subscribed)) return;
        Unsubscribe();
        _subscribed = columns;
        if (columns is not null) columns.Changed += OnChanged;
        InvalidateMeasure();
    }

    private void Unsubscribe()
    {
        if (_subscribed is not null) _subscribed.Changed -= OnChanged;
        _subscribed = null;
    }

    private DetailsColumns? FindSource()
    {
        for (var e = VisualTreeHelper.GetParent(this); e is not null; e = VisualTreeHelper.GetParent(e))
        {
            if (GetSource(e) is DetailsColumns columns) return columns;
        }

        return null;
    }

    private void OnChanged(object? sender, EventArgs e) => InvalidateMeasure();

    protected override Size MeasureOverride(Size availableSize)
    {
        var placements = Place(availableSize.Width);
        double height = 0, width = 0;
        foreach (var child in Children)
        {
            var place = Find(placements, child);
            // Collapsed, not just sized 0: a 0-wide TextBlock still draws its ellipsis.
            var visibility = place is null ? Visibility.Collapsed : Visibility.Visible;
            if (child.Visibility != visibility) child.Visibility = visibility;
            child.Measure(place is { } p ? new Size(p.Width, availableSize.Height) : default);
            if (place is { } q)
            {
                height = Math.Max(height, child.DesiredSize.Height);
                width = Math.Max(width, q.X + q.Width);
            }
        }

        return new Size(double.IsFinite(availableSize.Width) ? Math.Min(width, availableSize.Width) : width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var placements = Place(finalSize.Width);
        foreach (var child in Children)
        {
            var place = Find(placements, child);
            child.Arrange(place is { } p ? new Rect(p.X, 0, p.Width, finalSize.Height) : default);
        }

        var overflows = placements.Count > 0 && placements[^1].X + placements[^1].Width > finalSize.Width;
        Clip = overflows ? new RectangleGeometry { Rect = new Rect(0, 0, finalSize.Width, finalSize.Height) } : null;
        return finalSize;
    }

    private IReadOnlyList<ColumnPlacement> Place(double width)
    {
        var columns = Effective;
        return (columns?.Layout ?? ColumnLayout.Default).Arrange(width, columns?.Spacing ?? 8);
    }

    private static ColumnPlacement? Find(IReadOnlyList<ColumnPlacement> placements, UIElement child)
    {
        var id = GetColumnId(child);
        if (id is null) return null;
        foreach (var p in placements)
        {
            if (string.Equals(p.Column.Id, id, StringComparison.OrdinalIgnoreCase)) return p;
        }

        return null;
    }
}
