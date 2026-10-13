using System.Globalization;

namespace Pollux.Core.Snap;

public static class SnapColor
{
    /// <summary>A pixel of the frozen image (BGRA byte order) as "#RRGGBB".</summary>
    public static string ToHex(byte b, byte g, byte r) =>
        string.Create(CultureInfo.InvariantCulture, $"#{r:X2}{g:X2}{b:X2}");
}
