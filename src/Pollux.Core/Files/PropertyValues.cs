using System.Globalization;

namespace Pollux.Core.Files;

/// <summary>A shell property value as shown in its column, with a number to sort by where the text would sort wrongly.</summary>
public readonly record struct PropertyValue(string Text, long? Number = null)
{
    /// <summary>"1920 × 1080", sorted by pixel count.</summary>
    public static PropertyValue Dimensions(uint width, uint height) =>
        new(string.Create(CultureInfo.InvariantCulture, $"{width} × {height}"), (long)width * height);

    /// <summary>"3:25" or "1:02:03" from a duration in 100 ns units (System.Media.Duration).</summary>
    public static PropertyValue Duration(ulong hundredNanoseconds)
    {
        var ticks = (long)Math.Min(hundredNanoseconds, (ulong)TimeSpan.MaxValue.Ticks);
        return new(PropertyFormat.Duration(TimeSpan.FromTicks(ticks)), ticks);
    }

    /// <summary>A local date in the <c>files.dateFormat</c> format.</summary>
    public static PropertyValue Date(DateTime local, string format) =>
        new(local.ToString(string.IsNullOrEmpty(format) ? "yyyy-MM-dd HH:mm" : format, CultureInfo.InvariantCulture), local.Ticks);

    /// <summary>Several strings (System.Author) joined like Explorer: "Ann; Bob". Null when all are blank.</summary>
    public static PropertyValue? Texts(IEnumerable<string?> values)
    {
        var parts = values.Select(v => v?.Trim()).Where(v => !string.IsNullOrEmpty(v)).ToList();
        return parts.Count == 0 ? null : new PropertyValue(string.Join("; ", parts));
    }

    /// <summary>A trimmed string; null when blank.</summary>
    public static PropertyValue? FromText(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : new PropertyValue(value.Trim());

    /// <summary>By number when both have one, otherwise naturally by text ("10.0.2" after "9.1").</summary>
    public static int Compare(PropertyValue a, PropertyValue b) =>
        a.Number is { } x && b.Number is { } y ? x.CompareTo(y) : EntryComparer.NaturalCompare(a.Text, b.Text);
}

public static class PropertyFormat
{
    /// <summary>"0:07", "3:25", "1:02:03": minutes and seconds, hours only when there are any. Truncated to seconds.</summary>
    public static string Duration(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero) duration = TimeSpan.Zero;
        var hours = (long)duration.TotalHours;
        return hours > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{hours}:{duration.Minutes:00}:{duration.Seconds:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{duration.Minutes}:{duration.Seconds:00}");
    }
}

/// <summary>
/// Shell property values of files by path, filled lazily from a background thread and read by sorting and the rows.
/// A value remembers the modified time it was read for, so a changed file is read again. Least-recently-stored
/// files are dropped beyond the capacity. Thread-safe.
/// </summary>
public sealed class PropertyValues(int capacity)
{
    private sealed class Item(string path, DateTime modified)
    {
        public string Path { get; } = path;
        public DateTime Modified { get; } = modified;
        public Dictionary<ShellProperty, PropertyValue?> Values { get; } = [];
    }

    private readonly Dictionary<string, LinkedListNode<Item>> _map = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<Item> _order = new();
    private readonly object _gate = new();

    public int Count
    {
        get { lock (_gate) return _map.Count; }
    }

    /// <summary>True once the property was read for this version of the file; <paramref name="value"/> is null when it has none.</summary>
    public bool TryGet(FileEntry entry, ShellProperty property, out PropertyValue? value)
    {
        lock (_gate)
        {
            if (_map.TryGetValue(entry.FullPath, out var node) && node.Value.Modified == entry.Modified && node.Value.Values.TryGetValue(property, out value)) return true;
        }

        value = null;
        return false;
    }

    /// <summary>The value, or null when the file has none or it was not read yet.</summary>
    public PropertyValue? Get(FileEntry entry, ShellProperty property) => TryGet(entry, property, out var value) ? value : null;

    /// <summary>The properties still to read for this version of the file.</summary>
    public List<ShellProperty> Missing(FileEntry entry, IEnumerable<ShellProperty> properties) =>
        [.. properties.Distinct().Where(p => !TryGet(entry, p, out _))];

    /// <summary>Stores what was read for <paramref name="entry"/>; a null value records that the file has none.</summary>
    public void Set(FileEntry entry, IEnumerable<KeyValuePair<ShellProperty, PropertyValue?>> values)
    {
        lock (_gate)
        {
            if (!_map.TryGetValue(entry.FullPath, out var node) || node.Value.Modified != entry.Modified)
            {
                if (node is not null) _order.Remove(node);
                node = new LinkedListNode<Item>(new Item(entry.FullPath, entry.Modified));
                _map[entry.FullPath] = node;
            }
            else
            {
                _order.Remove(node);
            }

            _order.AddFirst(node);
            foreach (var (property, value) in values) node.Value.Values[property] = value;
            while (_map.Count > Math.Max(1, capacity))
            {
                var last = _order.Last!;
                _order.RemoveLast();
                _map.Remove(last.Value.Path);
            }
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _map.Clear();
            _order.Clear();
        }
    }
}
