namespace Pollux.Core.Snap;

/// <summary>
/// The area being chosen in the Snap overlay: the corner where the drag started and the corner being dragged,
/// always inside the monitor the drag started on.
/// </summary>
public sealed record SnapSelection
{
    private SnapSelection(SnapMonitor monitor, PixelPoint anchor, PixelPoint end)
    {
        Monitor = monitor;
        Anchor = Clamp(monitor, anchor);
        End = Clamp(monitor, end);
    }

    public SnapMonitor Monitor { get; }
    public PixelPoint Anchor { get; }
    public PixelPoint End { get; }

    /// <summary>The chosen area in physical screen pixels, whatever direction it was dragged in.</summary>
    public PixelRect Area => new(
        Math.Min(Anchor.X, End.X), Math.Min(Anchor.Y, End.Y), Math.Abs(End.X - Anchor.X), Math.Abs(End.Y - Anchor.Y));

    /// <summary>No area yet: a click without a drag, or a drag along one axis only.</summary>
    public bool IsEmpty => Area.Width == 0 || Area.Height == 0;

    public static SnapSelection Start(SnapMonitor monitor, PixelPoint at) => new(monitor, at, at);

    public static SnapSelection WholeMonitor(SnapMonitor monitor) =>
        new(monitor, new PixelPoint(monitor.Bounds.X, monitor.Bounds.Y), new PixelPoint(monitor.Bounds.Right, monitor.Bounds.Bottom));

    /// <summary>A given rectangle (a window's frame), cut to <paramref name="monitor"/>.</summary>
    public static SnapSelection Of(SnapMonitor monitor, PixelRect area) =>
        new(monitor, new PixelPoint(area.X, area.Y), new PixelPoint(area.Right, area.Bottom));

    /// <summary>
    /// The last area again: on the monitor holding its centre, else the one it overlaps most, cut to that monitor;
    /// null when it overlaps no monitor any more.
    /// </summary>
    public static SnapSelection? Repeat(PixelRect last, ScreenLayout layout)
    {
        var cx = last.X + (last.Width / 2);
        var cy = last.Y + (last.Height / 2);
        var monitor = layout.Monitors.FirstOrDefault(m => cx >= m.Bounds.X && cx < m.Bounds.Right && cy >= m.Bounds.Y && cy < m.Bounds.Bottom)
            ?? layout.Monitors.MaxBy(m => Overlap(m.Bounds, last));
        if (monitor is null || Overlap(monitor.Bounds, last) == 0) return null;
        var selection = Of(monitor, last);
        return selection.IsEmpty ? null : selection;
    }

    private static long Overlap(PixelRect a, PixelRect b) =>
        (long)Math.Max(0, Math.Min(a.Right, b.Right) - Math.Max(a.X, b.X)) * Math.Max(0, Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Y, b.Y));

    public SnapSelection WithEnd(PixelPoint end) => new(Monitor, Anchor, end);

    /// <summary>Moves the dragged corner (arrow keys: 1 px, with Shift 10 px).</summary>
    public SnapSelection MoveEndBy(int dx, int dy) => WithEnd(new PixelPoint(End.X + dx, End.Y + dy));

    /// <summary>Moves the whole area without changing its size; it stops at the monitor's edges.</summary>
    public SnapSelection MoveBy(int dx, int dy)
    {
        var area = Area;
        var b = Monitor.Bounds;
        dx = Math.Clamp(dx, b.X - area.X, b.Right - area.Right);
        dy = Math.Clamp(dy, b.Y - area.Y, b.Bottom - area.Bottom);
        return new SnapSelection(Monitor, new PixelPoint(Anchor.X + dx, Anchor.Y + dy), new PixelPoint(End.X + dx, End.Y + dy));
    }

    private static PixelPoint Clamp(SnapMonitor monitor, PixelPoint p) =>
        new(Math.Clamp(p.X, monitor.Bounds.X, monitor.Bounds.Right), Math.Clamp(p.Y, monitor.Bounds.Y, monitor.Bounds.Bottom));
}
