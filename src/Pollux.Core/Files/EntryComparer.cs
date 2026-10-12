using Pollux.Core.Settings;

namespace Pollux.Core.Files;

/// <summary>
/// Sorts like Explorer: folders first (optional), natural number order ("file2" before "file10"). A shell property
/// column sorts by the values in <see cref="PropertyValues"/>; entries without one (or not read yet) come last in
/// either direction.
/// </summary>
public sealed class EntryComparer : IComparer<FileEntry>
{
    private readonly SortColumn _column;
    private readonly ShellProperty? _property;
    private readonly PropertyValues? _properties;
    private readonly bool _descending;
    private readonly bool _foldersFirst;

    public EntryComparer(SortColumn column, bool descending, bool foldersFirst)
    {
        _column = column;
        _descending = descending;
        _foldersFirst = foldersFirst;
    }

    /// <param name="column">Any known column; one that cannot sort sorts by name.</param>
    /// <param name="descending">Reverse order (entries without a property value stay last).</param>
    /// <param name="foldersFirst">Folders before files.</param>
    /// <param name="properties">The values a shell property column sorts by.</param>
    public EntryComparer(ListColumn column, bool descending, bool foldersFirst, PropertyValues? properties = null)
        : this(column.Sortable ? column.Sort ?? SortColumn.Name : SortColumn.Name, descending, foldersFirst)
    {
        _property = column.Sortable ? column.Property : null;
        _properties = properties;
    }

    public int Compare(FileEntry? x, FileEntry? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;

        if (_foldersFirst && x.IsDirectory != y.IsDirectory)
        {
            return x.IsDirectory ? -1 : 1;
        }

        if (_property is { } property)
        {
            var a = _properties?.Get(x, property);
            var b = _properties?.Get(y, property);
            if (a is null != b is null) return a is null ? 1 : -1;
            var byValue = a is { } va && b is { } vb ? PropertyValue.Compare(va, vb) : 0;
            if (byValue == 0) byValue = NaturalCompare(x.Name, y.Name);
            return _descending ? -byValue : byValue;
        }

        var result = _column switch
        {
            SortColumn.Modified => x.Modified.CompareTo(y.Modified),
            SortColumn.Size => x.Size.CompareTo(y.Size),
            SortColumn.Created => x.Created.CompareTo(y.Created),
            SortColumn.Attributes => string.CompareOrdinal(Format.AttributeLetters(x.Attributes), Format.AttributeLetters(y.Attributes)),
            SortColumn.Extension => string.Compare(x.Extension, y.Extension, StringComparison.OrdinalIgnoreCase),
            SortColumn.Type => string.Compare(x.Extension, y.Extension, StringComparison.OrdinalIgnoreCase),
            _ => 0,
        };

        if (result == 0)
        {
            result = NaturalCompare(x.Name, y.Name);
        }

        return _descending ? -result : result;
    }

    public static int NaturalCompare(string a, string b)
    {
        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            if (char.IsDigit(a[i]) && char.IsDigit(b[j]))
            {
                var si = i;
                var sj = j;
                while (i < a.Length && char.IsDigit(a[i])) i++;
                while (j < b.Length && char.IsDigit(b[j])) j++;
                var na = a[si..i].TrimStart('0');
                var nb = b[sj..j].TrimStart('0');
                if (na.Length != nb.Length) return na.Length.CompareTo(nb.Length);
                var c = string.CompareOrdinal(na, nb);
                if (c != 0) return c;
            }
            else
            {
                var c = char.ToLowerInvariant(a[i]).CompareTo(char.ToLowerInvariant(b[j]));
                if (c != 0) return c;
                i++;
                j++;
            }
        }

        return (a.Length - i).CompareTo(b.Length - j);
    }
}
