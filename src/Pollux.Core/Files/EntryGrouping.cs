using Pollux.Core.Settings;

namespace Pollux.Core.Files;

/// <summary>A run of rows under one group header: rows <see cref="Start"/> .. <see cref="Start"/> + <see cref="Count"/> - 1.</summary>
public sealed record RowGroup(string Title, int Start, int Count);

/// <summary>Grouping rules of the Details view: by Type or by Modified date bucket.</summary>
public static class EntryGrouping
{
    public static readonly string[] DateBuckets = ["Today", "Yesterday", "This week", "This month", "Earlier"];

    /// <summary>Next mode in the cycle None, Type, Modified date, None.</summary>
    public static GroupBy Next(GroupBy current) => current switch
    {
        GroupBy.None => GroupBy.Type,
        GroupBy.Type => GroupBy.Modified,
        _ => GroupBy.None,
    };

    public static string Describe(GroupBy mode) => mode switch
    {
        GroupBy.Type => "Type",
        GroupBy.Modified => "Modified date",
        _ => "None",
    };

    /// <summary>Index into <see cref="DateBuckets"/> for <paramref name="modified"/> relative to <paramref name="now"/>; weeks start on Monday.</summary>
    public static int DateBucket(DateTime modified, DateTime now)
    {
        var today = now.Date;
        var day = modified.Date;
        if (day >= today) return 0;
        if (day >= today.AddDays(-1)) return 1;
        var weekStart = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        if (day >= weekStart) return 2;
        if (day >= new DateTime(today.Year, today.Month, 1)) return 3;
        return 4;
    }

    /// <summary>Group title of an entry: "Folders" or its type label; or its date bucket.</summary>
    public static string TitleOf(FileEntry entry, GroupBy mode, DateTime now) => mode switch
    {
        GroupBy.Type => entry.IsDirectory ? "Folders" : entry.Extension.Length == 0 ? "Files" : entry.Extension.ToUpperInvariant(),
        GroupBy.Modified => DateBuckets[DateBucket(entry.Modified, now)],
        _ => "",
    };

    /// <summary>
    /// Reorders <paramref name="rows"/> so each group is contiguous (the order inside a group is kept) and returns the groups.
    /// Type groups: Folders first, then alphabetical, extensionless files last. Date groups: newest bucket first.
    /// </summary>
    public static (List<FolderRow> Rows, List<RowGroup> Groups) Apply(IReadOnlyList<FolderRow> rows, GroupBy mode, DateTime now)
    {
        if (mode == GroupBy.None) return ([.. rows], []);

        var keyed = rows.Select(r => (Row: r, Title: TitleOf(r.Entry, mode, now))).ToList();
        IEnumerable<(FolderRow Row, string Title)> ordered = mode == GroupBy.Type
            ? keyed.OrderBy(k => k.Row.Entry.IsDirectory ? 0 : k.Row.Entry.Extension.Length == 0 ? 2 : 1).ThenBy(k => k.Title, StringComparer.OrdinalIgnoreCase)
            : keyed.OrderBy(k => DateBucket(k.Row.Entry.Modified, now));

        var result = new List<FolderRow>(rows.Count);
        var groups = new List<RowGroup>();
        foreach (var (row, title) in ordered)
        {
            if (groups.Count > 0 && groups[^1].Title == title)
            {
                groups[^1] = groups[^1] with { Count = groups[^1].Count + 1 };
            }
            else
            {
                groups.Add(new RowGroup(title, result.Count, 1));
            }

            result.Add(row);
        }

        return (result, groups);
    }
}
