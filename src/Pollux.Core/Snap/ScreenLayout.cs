namespace Pollux.Core.Snap;

/// <summary>A point in physical screen pixels (virtual-screen coordinates; can be negative left of or above the primary monitor).</summary>
public readonly record struct PixelPoint(int X, int Y);

/// <summary>A rectangle in physical pixels; Right and Bottom are exclusive.</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
}

/// <summary>One monitor: its bounds in physical pixels and its display scale (1.5 = 150 %).</summary>
public sealed record SnapMonitor(PixelRect Bounds, double Scale)
{
    /// <summary>A point in this monitor's overlay (device-independent units from its top-left) in physical screen pixels.</summary>
    public PixelPoint ToPhysical(double x, double y) =>
        new(Bounds.X + (int)Math.Round(x * Scale), Bounds.Y + (int)Math.Round(y * Scale));
}

/// <summary>The monitors at the moment of a Snap, and the mapping from screen pixels to the frozen image.</summary>
public sealed class ScreenLayout
{
    public ScreenLayout(IReadOnlyList<SnapMonitor> monitors)
    {
        Monitors = monitors;
        var left = monitors.Min(m => m.Bounds.X);
        var top = monitors.Min(m => m.Bounds.Y);
        VirtualBounds = new PixelRect(left, top, monitors.Max(m => m.Bounds.Right) - left, monitors.Max(m => m.Bounds.Bottom) - top);
    }

    public IReadOnlyList<SnapMonitor> Monitors { get; }

    /// <summary>The smallest rectangle around all monitors; the frozen image covers exactly this.</summary>
    public PixelRect VirtualBounds { get; }

    /// <summary>A screen rectangle as pixels of the frozen image.</summary>
    public PixelRect ToImage(PixelRect screen) =>
        screen with { X = screen.X - VirtualBounds.X, Y = screen.Y - VirtualBounds.Y };
}
