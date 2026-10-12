using System.Globalization;

namespace Pollux.Core.Files;

/// <summary>A visible Details column: its width, and whether it takes the space the others leave (Name until resized).</summary>
public readonly record struct ColumnSlot(ListColumn Column, double Width, bool Fill);

/// <summary>Where a visible column is drawn: <see cref="X"/> from the left edge of the row.</summary>
public readonly record struct ColumnPlacement(ListColumn Column, double X, double Width);

/// <summary>
/// The three settings values a <see cref="ColumnLayout"/> is stored in. <see cref="Columns"/> is <c>files.columns</c>
/// (the shown columns beyond the four defaults, read by older versions too), <see cref="Order"/> is
/// <c>files.columnOrder</c> (every shown column left to right; empty while it is the default order) and
/// <see cref="Widths"/> is <c>files.columnWidths</c> (only the widths the user changed).
/// </summary>
public sealed record ColumnSettings(List<string> Columns, List<string> Order, Dictionary<string, double> Widths);

/// <summary>
/// The shown Details columns, their order and widths. Immutable: every change returns a new layout. Name is always
/// shown; it fills the free space until the user gives it a width.
/// </summary>
public sealed class ColumnLayout
{
    private readonly Dictionary<string, double> _widths;

    private ColumnLayout(IReadOnlyList<ListColumn> visible, Dictionary<string, double> widths)
    {
        _widths = widths;
        Visible = [.. visible.Select(c => new ColumnSlot(c, WidthOf(c), c == KnownColumns.Name && !widths.ContainsKey(c.Id)))];
    }

    public static ColumnLayout Default { get; } = new(KnownColumns.Defaults, []);

    /// <summary>Left to right.</summary>
    public IReadOnlyList<ColumnSlot> Visible { get; }

    /// <summary>The shown columns that the older <see cref="OptionalColumns"/> flags describe.</summary>
    public OptionalColumns Optional => Visible.Aggregate(OptionalColumns.None, (a, s) => a | s.Column.Optional);

    /// <summary>The shell properties the shown columns need, left to right.</summary>
    public IReadOnlyList<ShellProperty> Properties => [.. Visible.Where(s => s.Column.Property is not null).Select(s => s.Column.Property!.Value)];

    public bool IsVisible(string id) => IndexOf(id) >= 0;

    public int IndexOf(string id)
    {
        for (var i = 0; i < Visible.Count; i++)
        {
            if (string.Equals(Visible[i].Column.Id, id, StringComparison.OrdinalIgnoreCase)) return i;
        }

        return -1;
    }

    /// <summary>
    /// Reads the settings. Without an order the defaults come first, then the extra columns in catalog order; ids in
    /// <paramref name="columns"/> missing from the order (added by an older version) are appended. Unknown ids and
    /// duplicates are ignored, widths are clamped, Name is always shown.
    /// </summary>
    public static ColumnLayout Parse(IEnumerable<string>? columns, IEnumerable<string>? order, IReadOnlyDictionary<string, double>? widths)
    {
        var extra = (columns ?? []).Select(KnownColumns.Find).OfType<ListColumn>().ToHashSet();
        var visible = new List<ListColumn>();
        var ordered = (order ?? []).Select(KnownColumns.Find).OfType<ListColumn>().ToList();
        if (ordered.Count == 0)
        {
            visible.AddRange(KnownColumns.Defaults);
            visible.AddRange(KnownColumns.All.Where(c => extra.Contains(c) && !visible.Contains(c)));
        }
        else
        {
            foreach (var c in ordered.Concat(KnownColumns.All.Where(extra.Contains)))
            {
                if (!visible.Contains(c)) visible.Add(c);
            }
        }

        if (!visible.Contains(KnownColumns.Name)) visible.Insert(0, KnownColumns.Name);

        var kept = new Dictionary<string, double>();
        foreach (var (id, width) in widths ?? new Dictionary<string, double>())
        {
            if (KnownColumns.Find(id) is { } c && double.IsFinite(width)) kept[c.Id] = Clamp(c, width);
        }

        return new ColumnLayout(visible, kept);
    }

    /// <summary>The values to store; the order is left empty while it is the one <see cref="Parse"/> derives anyway.</summary>
    public ColumnSettings ToSettings()
    {
        var shown = Visible.Select(s => s.Column).ToList();
        var columns = KnownColumns.All.Where(c => shown.Contains(c) && !KnownColumns.Defaults.Contains(c)).Select(c => c.Id).ToList();
        var derived = KnownColumns.Defaults.Concat(KnownColumns.All.Where(c => shown.Contains(c) && !KnownColumns.Defaults.Contains(c)));
        List<string> order = derived.SequenceEqual(shown) ? [] : shown.Select(c => c.Id).ToList();
        return new ColumnSettings(columns, order, new Dictionary<string, double>(_widths));
    }

    /// <summary>Shows a hidden column at the right end, or hides a shown one. Name stays.</summary>
    public ColumnLayout Toggle(string id)
    {
        if (KnownColumns.Find(id) is not { } column || column == KnownColumns.Name) return this;
        var shown = Visible.Select(s => s.Column).ToList();
        if (!shown.Remove(column)) shown.Add(column);
        return new ColumnLayout(shown, _widths);
    }

    /// <summary>Moves a shown column to <paramref name="index"/> (counted after taking it out; clamped).</summary>
    public ColumnLayout Move(string id, int index)
    {
        var from = IndexOf(id);
        if (from < 0) return this;
        var shown = Visible.Select(s => s.Column).ToList();
        var column = shown[from];
        shown.RemoveAt(from);
        shown.Insert(Math.Clamp(index, 0, shown.Count), column);
        return new ColumnLayout(shown, _widths);
    }

    /// <summary>Gives a column a width, clamped to its minimum and <see cref="KnownColumns.MaxWidth"/>; Name stops filling.</summary>
    public ColumnLayout Resize(string id, double width)
    {
        if (KnownColumns.Find(id) is not { } column || !double.IsFinite(width)) return this;
        return new ColumnLayout(Visible.Select(s => s.Column).ToList(), new Dictionary<string, double>(_widths) { [column.Id] = Clamp(column, width) });
    }

    /// <summary>Back to the column's default width (Name: fills again).</summary>
    public ColumnLayout ResetWidth(string id)
    {
        if (KnownColumns.Find(id) is not { } column) return this;
        var widths = new Dictionary<string, double>(_widths);
        widths.Remove(column.Id);
        return new ColumnLayout(Visible.Select(s => s.Column).ToList(), widths);
    }

    /// <summary>Same columns, order and widths (a re-read of unchanged settings).</summary>
    public bool SameAs(ColumnLayout other) => Visible.SequenceEqual(other.Visible);

    /// <summary>One line for a folder's remembered layout: the shown ids left to right, then the changed widths ("name,size|name=320").</summary>
    public string Pack()
    {
        var ids = string.Join(",", Visible.Select(s => s.Column.Id));
        var widths = string.Join(",", KnownColumns.All.Where(c => _widths.ContainsKey(c.Id)).Select(c => string.Create(CultureInfo.InvariantCulture, $"{c.Id}={_widths[c.Id]:0.#}")));
        return ids + "|" + widths;
    }

    /// <summary>Reads <see cref="Pack"/>; null when the text names no known column.</summary>
    public static ColumnLayout? Unpack(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var split = text.IndexOf('|', StringComparison.Ordinal);
        var ids = (split < 0 ? text : text[..split]).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (!ids.Any(id => KnownColumns.Find(id) is not null)) return null;
        var widths = split < 0 ? [] : ParseWidths(text[(split + 1)..].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
        return Parse(ids, ids, widths);
    }

    /// <summary>The widths as settings-window lines, "name=320", in catalog order.</summary>
    public static List<string> FormatWidths(IReadOnlyDictionary<string, double>? widths)
    {
        var known = ParseWidths(widths?.Select(w => string.Create(CultureInfo.InvariantCulture, $"{w.Key}={w.Value}")));
        return [.. KnownColumns.All.Where(c => known.ContainsKey(c.Id)).Select(c => string.Create(CultureInfo.InvariantCulture, $"{c.Id}={known[c.Id]:0.#}"))];
    }

    /// <summary>Reads "id=width" lines ("id: width" and "id width" too); unknown ids and bad numbers are dropped, widths clamped.</summary>
    public static Dictionary<string, double> ParseWidths(IEnumerable<string>? lines)
    {
        var result = new Dictionary<string, double>();
        foreach (var raw in lines ?? [])
        {
            var line = raw.Trim();
            var split = line.IndexOfAny(['=', ':', ' ', '\t']);
            if (split <= 0 || KnownColumns.Find(line[..split]) is not { } column) continue;
            var number = line[(split + 1)..].Trim().TrimStart('=', ':').Trim();
            if (double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var width) && double.IsFinite(width))
            {
                result[column.Id] = Clamp(column, width);
            }
        }

        return result;
    }

    public double WidthOf(ListColumn column) => _widths.TryGetValue(column.Id, out var w) ? w : column.DefaultWidth;

    /// <summary>
    /// Positions the shown columns in a row <paramref name="available"/> wide with <paramref name="spacing"/> between
    /// them. A filling Name gets what the others leave, at least its minimum (the row is then wider than available).
    /// </summary>
    public IReadOnlyList<ColumnPlacement> Arrange(double available, double spacing)
    {
        var fixedTotal = Visible.Where(s => !s.Fill).Sum(s => s.Width) + spacing * Math.Max(0, Visible.Count - 1);
        var free = double.IsFinite(available) ? available - fixedTotal : 0;
        var result = new List<ColumnPlacement>(Visible.Count);
        var x = 0.0;
        foreach (var slot in Visible)
        {
            var width = slot.Fill ? Math.Max(slot.Column.MinWidth, free) : slot.Width;
            if (slot.Fill && !double.IsFinite(available)) width = slot.Width;
            result.Add(new ColumnPlacement(slot.Column, x, width));
            x += width + spacing;
        }

        return result;
    }

    /// <summary>The <see cref="Move"/> index for a column dragged by its header and dropped at <paramref name="x"/>.</summary>
    public static int DropIndex(IReadOnlyList<ColumnPlacement> placements, string draggedId, double x)
    {
        var index = 0;
        foreach (var p in placements)
        {
            if (string.Equals(p.Column.Id, draggedId, StringComparison.OrdinalIgnoreCase)) continue;
            if (x < p.X + p.Width / 2) break;
            index++;
        }

        return index;
    }

    /// <summary>The column whose right edge is within <paramref name="tolerance"/> of <paramref name="x"/> (a resize grip), or null.</summary>
    public static ListColumn? GripAt(IReadOnlyList<ColumnPlacement> placements, double x, double tolerance)
    {
        foreach (var p in placements)
        {
            if (Math.Abs(x - (p.X + p.Width)) <= tolerance) return p.Column;
        }

        return null;
    }

    private static double Clamp(ListColumn column, double width) => Math.Clamp(width, column.MinWidth, KnownColumns.MaxWidth);
}
