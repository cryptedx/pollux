using Pollux.Core.Settings;

namespace Pollux.Core.Files;

/// <summary>The optional Details columns beyond Name, Modified, Type and Size.</summary>
[Flags]
public enum OptionalColumns
{
    None = 0,
    Created = 1,
    Attributes = 2,
    Extension = 4,
}

/// <summary>Maps the <c>files.columns</c> setting (a list of ids) to <see cref="OptionalColumns"/> and back.</summary>
public static class ListColumns
{
    public static readonly OptionalColumns[] All = [OptionalColumns.Created, OptionalColumns.Attributes, OptionalColumns.Extension];

    public static string Id(OptionalColumns column) => column switch
    {
        OptionalColumns.Created => "created",
        OptionalColumns.Attributes => "attributes",
        OptionalColumns.Extension => "extension",
        _ => "",
    };

    public static string Title(OptionalColumns column) => column switch
    {
        OptionalColumns.Created => "Created",
        OptionalColumns.Attributes => "Attributes",
        OptionalColumns.Extension => "Extension",
        _ => "",
    };

    /// <summary>Unknown ids are ignored; ids are case-insensitive.</summary>
    public static OptionalColumns Parse(IEnumerable<string>? ids)
    {
        var result = OptionalColumns.None;
        foreach (var id in ids ?? [])
        {
            foreach (var c in All)
            {
                if (string.Equals(Id(c), id?.Trim(), StringComparison.OrdinalIgnoreCase)) result |= c;
            }
        }

        return result;
    }

    /// <summary>The ids for the setting, in a fixed order.</summary>
    public static List<string> ToIds(OptionalColumns columns) => [.. All.Where(c => columns.HasFlag(c)).Select(Id)];

    public static List<string> Toggle(IEnumerable<string>? ids, OptionalColumns column) => ToIds(Parse(ids) ^ column);
}

/// <summary>A shell property shown in a Details column (read by the App from the file's property store).</summary>
public enum ShellProperty { Dimensions, Duration, Authors, Title, FileVersion, Company, DateTaken }

public enum ColumnAlignment { Left, Right }

/// <summary>
/// One known Details column. <see cref="Property"/> is set for the columns whose values come from the shell's property
/// store; <see cref="Sort"/> is what the <c>files.sortBy</c> setting calls it.
/// </summary>
public sealed record ListColumn(
    string Id,
    string Title,
    double DefaultWidth,
    double MinWidth,
    ColumnAlignment Alignment = ColumnAlignment.Left,
    bool Sortable = true,
    SortColumn? Sort = null,
    ShellProperty? Property = null,
    OptionalColumns Optional = OptionalColumns.None);

/// <summary>Every Details column Pollux knows, in their default order.</summary>
public static class KnownColumns
{
    public const double MaxWidth = 1200;

    public static readonly ListColumn Name = new("name", "Name", 280, 120, Sort: SortColumn.Name);
    public static readonly ListColumn Modified = new("modified", "Modified", 128, 60, Sort: SortColumn.Modified);
    public static readonly ListColumn Type = new("type", "Type", 70, 40, Sort: SortColumn.Type);
    public static readonly ListColumn Size = new("size", "Size", 74, 40, ColumnAlignment.Right, Sort: SortColumn.Size);
    public static readonly ListColumn Created = new("created", "Created", 128, 60, Sort: SortColumn.Created, Optional: OptionalColumns.Created);
    public static readonly ListColumn Attributes = new("attributes", "Attr", 44, 36, Sort: SortColumn.Attributes, Optional: OptionalColumns.Attributes);
    public static readonly ListColumn Extension = new("extension", "Ext", 70, 36, Sort: SortColumn.Extension, Optional: OptionalColumns.Extension);
    public static readonly ListColumn Dimensions = new("dimensions", "Dimensions", 96, 50, ColumnAlignment.Right, Sort: SortColumn.Dimensions, Property: ShellProperty.Dimensions);
    public static readonly ListColumn Duration = new("duration", "Duration", 64, 40, ColumnAlignment.Right, Sort: SortColumn.Duration, Property: ShellProperty.Duration);
    public static readonly ListColumn Authors = new("authors", "Authors", 140, 50, Sort: SortColumn.Authors, Property: ShellProperty.Authors);
    public static readonly ListColumn Title = new("title", "Title", 160, 50, Sort: SortColumn.Title, Property: ShellProperty.Title);
    public static readonly ListColumn FileVersion = new("fileVersion", "File version", 100, 50, Sort: SortColumn.FileVersion, Property: ShellProperty.FileVersion);
    public static readonly ListColumn Company = new("company", "Company", 140, 50, Sort: SortColumn.Company, Property: ShellProperty.Company);
    public static readonly ListColumn DateTaken = new("dateTaken", "Date taken", 128, 60, Sort: SortColumn.DateTaken, Property: ShellProperty.DateTaken);

    public static readonly IReadOnlyList<ListColumn> All =
        [Name, Modified, Type, Size, Created, Attributes, Extension, Dimensions, Duration, Authors, Title, FileVersion, Company, DateTaken];

    /// <summary>The columns shown when the settings add none: Name, Modified, Type, Size.</summary>
    public static readonly IReadOnlyList<ListColumn> Defaults = [Name, Modified, Type, Size];

    /// <summary>The column with this id (case-insensitive, trimmed); null when unknown.</summary>
    public static ListColumn? Find(string? id)
    {
        var trimmed = id?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return null;
        foreach (var c in All)
        {
            if (string.Equals(c.Id, trimmed, StringComparison.OrdinalIgnoreCase)) return c;
        }

        return null;
    }

    public static ListColumn For(SortColumn sort) => All.First(c => c.Sort == sort);

    public static ListColumn For(ShellProperty property) => All.First(c => c.Property == property);
}
