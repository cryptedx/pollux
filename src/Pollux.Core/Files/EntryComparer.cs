using Pollux.Core.Settings;

namespace Pollux.Core.Files;

/// <summary>Sorts like Explorer: folders first (optional), natural number order ("file2" before "file10").</summary>
public sealed class EntryComparer(SortColumn column, bool descending, bool foldersFirst) : IComparer<FileEntry>
{
    public int Compare(FileEntry? x, FileEntry? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;

        if (foldersFirst && x.IsDirectory != y.IsDirectory)
        {
            return x.IsDirectory ? -1 : 1;
        }

        var result = column switch
        {
            SortColumn.Modified => x.Modified.CompareTo(y.Modified),
            SortColumn.Size => x.Size.CompareTo(y.Size),
            SortColumn.Type => string.Compare(x.Extension, y.Extension, StringComparison.OrdinalIgnoreCase),
            _ => 0,
        };

        if (result == 0)
        {
            result = NaturalCompare(x.Name, y.Name);
        }

        return descending ? -result : result;
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
