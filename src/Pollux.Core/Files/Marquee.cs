namespace Pollux.Core.Files;

/// <summary>An axis-aligned rectangle in list content coordinates.</summary>
public readonly record struct LayoutBox(double Left, double Top, double Right, double Bottom)
{
    /// <summary>The box spanned by two corners in any order.</summary>
    public static LayoutBox Span(double x1, double y1, double x2, double y2) =>
        new(Math.Min(x1, x2), Math.Min(y1, y2), Math.Max(x1, x2), Math.Max(y1, y2));

    public double Width => Right - Left;
    public double Height => Bottom - Top;

    /// <summary>
    /// Whether this marquee touches <paramref name="slot"/>. The marquee counts as closed and the slot as half-open, so a
    /// zero-width marquee still hits the slot it crosses and a marquee edge on the line between two slots hits only one.
    /// </summary>
    public bool Hits(LayoutBox slot) => Left < slot.Right && slot.Left <= Right && Top < slot.Bottom && slot.Top <= Bottom;
}

/// <summary>Where a list puts its items, so a marquee can hit items that are not realized.</summary>
public abstract record ItemLayout
{
    /// <summary>The slot of the item at <paramref name="index"/>.</summary>
    public abstract LayoutBox Slot(int index);

    /// <summary>The indexes, ascending, of the first <paramref name="count"/> items whose slots <paramref name="marquee"/> hits.</summary>
    public abstract IEnumerable<int> Hits(LayoutBox marquee, int count);
}

/// <summary>
/// Rows of one height stacked from <see cref="Top"/>; with <see cref="GroupStarts"/> (ascending, the first is 0) a header
/// of <see cref="HeaderHeight"/> precedes each group.
/// </summary>
public sealed record StackItemLayout(double Left, double Top, double Right, double RowHeight, double HeaderHeight = 0, IReadOnlyList<int>? GroupStarts = null) : ItemLayout
{
    private IReadOnlyList<int> Starts => GroupStarts ?? [];

    public override LayoutBox Slot(int index)
    {
        var headers = 0;
        while (headers < Starts.Count && Starts[headers] <= index) headers++;
        var y = Top + headers * HeaderHeight + index * RowHeight;
        return new LayoutBox(Left, y, Right, y + RowHeight);
    }

    public override IEnumerable<int> Hits(LayoutBox marquee, int count)
    {
        if (RowHeight <= 0 || count <= 0 || !(marquee.Left < Right && Left <= marquee.Right)) yield break;
        if (Starts.Count == 0)
        {
            foreach (var i in Segment(marquee, 0, count, Top)) yield return i;
            yield break;
        }

        for (var g = 0; g < Starts.Count; g++)
        {
            var start = Starts[g];
            var end = g + 1 < Starts.Count ? Math.Min(Starts[g + 1], count) : count;
            if (start >= end) continue;
            foreach (var i in Segment(marquee, start, end, Top + (g + 1) * HeaderHeight + start * RowHeight)) yield return i;
        }
    }

    /// <summary>Items <paramref name="start"/>..<paramref name="end"/> stacked from <paramref name="y"/>.</summary>
    private IEnumerable<int> Segment(LayoutBox marquee, int start, int end, double y)
    {
        var first = start + (int)Math.Max(0, Math.Floor((marquee.Top - y) / RowHeight));
        var lastOffset = Math.Floor((marquee.Bottom - y) / RowHeight);
        if (lastOffset < 0) yield break;
        var last = (int)Math.Min(end - 1, start + lastOffset);
        for (var i = first; i <= last; i++) yield return i;
    }
}

/// <summary>Cells of one size filled row by row, <see cref="Columns"/> per row, from (<see cref="Left"/>, <see cref="Top"/>).</summary>
public sealed record WrapItemLayout(double Left, double Top, double ItemWidth, double ItemHeight, int Columns) : ItemLayout
{
    public override LayoutBox Slot(int index)
    {
        var x = Left + index % Columns * ItemWidth;
        var y = Top + index / Columns * ItemHeight;
        return new LayoutBox(x, y, x + ItemWidth, y + ItemHeight);
    }

    public override IEnumerable<int> Hits(LayoutBox marquee, int count)
    {
        if (ItemWidth <= 0 || ItemHeight <= 0 || Columns <= 0 || count <= 0) yield break;
        var c2 = Math.Floor((marquee.Right - Left) / ItemWidth);
        var r2 = Math.Floor((marquee.Bottom - Top) / ItemHeight);
        if (c2 < 0 || r2 < 0) yield break;
        var c1 = (int)Math.Max(0, Math.Floor((marquee.Left - Left) / ItemWidth));
        var r1 = (int)Math.Max(0, Math.Floor((marquee.Top - Top) / ItemHeight));
        if (c1 >= Columns) yield break;
        var lastColumn = (int)Math.Min(Columns - 1, c2);
        var lastRow = (int)Math.Min((count - 1) / Columns, r2);
        for (var r = r1; r <= lastRow; r++)
        {
            for (var c = c1; c <= lastColumn; c++)
            {
                var i = r * Columns + c;
                if (i >= count) yield break;
                yield return i;
            }
        }
    }
}

/// <summary>How a marquee combines with the selection it started from.</summary>
public enum MarqueeMode
{
    /// <summary>Plain drag: exactly the items hit.</summary>
    Replace,

    /// <summary>Shift+drag: the selection plus the items hit.</summary>
    Add,

    /// <summary>Ctrl+drag: the items hit flip relative to the selection.</summary>
    Toggle,
}

/// <summary>Rubber-band selection like Explorer's, independent of the UI.</summary>
public static class Marquee
{
    /// <summary>The pointer distance before a press on empty space becomes a marquee rather than a click.</summary>
    public const double DragThreshold = 4;

    /// <summary>Ctrl toggles (also with Shift), Shift adds, otherwise the marquee replaces the selection.</summary>
    public static MarqueeMode ModeFor(bool control, bool shift) =>
        control ? MarqueeMode.Toggle : shift ? MarqueeMode.Add : MarqueeMode.Replace;

    /// <summary>Whether an item ends up selected.</summary>
    public static bool IsSelected(MarqueeMode mode, bool wasSelected, bool hit) => mode switch
    {
        MarqueeMode.Add => wasSelected || hit,
        MarqueeMode.Toggle => wasSelected != hit,
        _ => hit,
    };

    /// <summary>
    /// The changes that take the selection from the marquee's previous hits to its new ones. Only items hit before or
    /// now can change, so the cost follows the marquee, not the selection. For <see cref="MarqueeMode.Replace"/> the
    /// selection is assumed cleared when the marquee started.
    /// </summary>
    public static (List<int> Select, List<int> Deselect) Changes(
        MarqueeMode mode, IReadOnlySet<int> initial, IReadOnlySet<int> before, IReadOnlySet<int> now)
    {
        var select = new List<int>();
        var deselect = new List<int>();
        foreach (var i in before.Union(now))
        {
            var was = mode != MarqueeMode.Replace && initial.Contains(i);
            var prior = IsSelected(mode, was, before.Contains(i));
            var next = IsSelected(mode, was, now.Contains(i));
            if (prior == next) continue;
            (next ? select : deselect).Add(i);
        }

        select.Sort();
        deselect.Sort();
        return (select, deselect);
    }

    /// <summary>Consecutive runs (first index, length) of ascending, distinct <paramref name="sorted"/> indexes.</summary>
    public static List<(int First, int Length)> Runs(IReadOnlyList<int> sorted)
    {
        var runs = new List<(int, int)>();
        var k = 0;
        while (k < sorted.Count)
        {
            var first = sorted[k];
            var length = 1;
            while (k + length < sorted.Count && sorted[k + length] == first + length) length++;
            runs.Add((first, length));
            k += length;
        }

        return runs;
    }

    /// <summary>
    /// How far to scroll per tick while the pointer at <paramref name="y"/> is within <paramref name="edge"/> of the
    /// viewport's <paramref name="top"/> or <paramref name="bottom"/>, or beyond them: negative is up, 0 is none. The
    /// step grows with the distance into the edge zone, up to <paramref name="maxStep"/>.
    /// </summary>
    public static double AutoScrollStep(double y, double top, double bottom, double edge = 24, double maxStep = 48)
    {
        if (bottom - top < 2 * edge) edge = Math.Max(0, (bottom - top) / 4);
        var depth = y < top + edge ? y - (top + edge) : y > bottom - edge ? y - (bottom - edge) : 0;
        if (depth == 0) return 0;
        var step = Math.Min(maxStep, 2 + Math.Abs(depth) / 2);
        return Math.Sign(depth) * step;
    }
}
