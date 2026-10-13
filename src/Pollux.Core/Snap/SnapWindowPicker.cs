namespace Pollux.Core.Snap;

/// <summary>A top-level window as the Snap overlay sees it: handle, frame in physical pixels and whether it shows at all.</summary>
public sealed record SnapWindow(nint Handle, PixelRect Bounds, bool Visible, bool Minimized, bool Cloaked);

/// <summary>Finds the window a click in the Snap overlay takes.</summary>
public static class SnapWindowPicker
{
    /// <summary>
    /// The topmost window under <paramref name="point"/> among <paramref name="windows"/> (topmost first), skipping windows
    /// that do not show (hidden, minimized, cloaked, empty) and Pollux's own <paramref name="ignore"/>d overlays.
    /// </summary>
    public static SnapWindow? At(PixelPoint point, IEnumerable<SnapWindow> windows, IReadOnlyCollection<nint> ignore) =>
        windows.FirstOrDefault(w =>
            w.Visible && !w.Minimized && !w.Cloaked
            && w.Bounds.Width > 0 && w.Bounds.Height > 0
            && !ignore.Contains(w.Handle)
            && point.X >= w.Bounds.X && point.X < w.Bounds.Right && point.Y >= w.Bounds.Y && point.Y < w.Bounds.Bottom);
}
