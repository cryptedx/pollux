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
