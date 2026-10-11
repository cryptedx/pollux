namespace Pollux.App.ViewModels;

/// <summary>One group of rows under a header in the grouped file list.</summary>
/// <remarks>Partial so the WinRT interop generator gives it a native-AOT wrapper; without it the list shows no rows.</remarks>
public sealed partial class FileGroup(string title, List<FileItem> items) : List<FileItem>(items)
{
    public string Title { get; } = title;
    public string Header => $"{Title} ({Count})";

    /// <summary>What the group header shows.</summary>
    public override string ToString() => Header;
}
