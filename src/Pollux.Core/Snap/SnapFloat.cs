namespace Pollux.Core.Snap;

/// <summary>Size, place and opacity of a Float (a Snap kept on screen), in physical pixels.</summary>
public static class SnapFloat
{
    private const double Step = 1.1, MinZoom = 0.1, MaxZoom = 4.0;

    /// <summary>
    /// <paramref name="rect"/> zoomed by <paramref name="notches"/> wheel notches (10 % each) around <paramref name="anchor"/>,
    /// which stays in place; between 10 % and 400 % of the Snap's own <paramref name="width"/> × <paramref name="height"/>.
    /// </summary>
    public static PixelRect Zoom(PixelRect rect, int width, int height, int notches, PixelPoint anchor)
    {
        var zoom = Math.Clamp((double)rect.Width / width * Math.Pow(Step, notches), MinZoom, MaxZoom);
        var w = Math.Max(1, (int)Math.Round(width * zoom));
        var h = Math.Max(1, (int)Math.Round(height * zoom));
        var k = (double)w / rect.Width;
        return new PixelRect(
            anchor.X - (int)Math.Round((anchor.X - rect.X) * k),
            anchor.Y - (int)Math.Round((anchor.Y - rect.Y) * k),
            w, h);
    }

    /// <summary>Opacity after <paramref name="notches"/> Ctrl+wheel notches: 10 % each, between 20 % and 100 %.</summary>
    public static double Opacity(double current, int notches) =>
        Math.Round(Math.Clamp(current + (notches * 0.1), 0.2, 1.0), 1);

    /// <summary>A Float of a <paramref name="width"/> × <paramref name="height"/> Snap: own size, centred, at most 80 % of the work area.</summary>
    public static PixelRect Place(int width, int height, PixelRect workArea)
    {
        var scale = Math.Min(1.0, Math.Min(workArea.Width * 0.8 / width, workArea.Height * 0.8 / height));
        var w = Math.Max(1, (int)Math.Round(width * scale));
        var h = Math.Max(1, (int)Math.Round(height * scale));
        return new PixelRect(workArea.X + ((workArea.Width - w) / 2), workArea.Y + ((workArea.Height - h) / 2), w, h);
    }
}
