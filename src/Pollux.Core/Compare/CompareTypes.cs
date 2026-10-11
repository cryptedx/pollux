using Pollux.Core.Files;

namespace Pollux.Core.Compare;

/// <summary>How one entry stands between the left and the right folder.</summary>
public enum CompareStatus
{
    /// <summary>Exists only in the left folder.</summary>
    OnlyLeft,
    /// <summary>Exists only in the right folder.</summary>
    OnlyRight,
    /// <summary>In both; the left one was modified later.</summary>
    LeftNewer,
    /// <summary>In both; the right one was modified later.</summary>
    RightNewer,
    /// <summary>In both and equal (for folders: nothing below differs, or the folders were not looked into).</summary>
    Same,
    /// <summary>In both, modified at the same time but different (size or content), or a file against a folder.</summary>
    Different,
}

public enum CompareSide { Left, Right }

/// <summary>What a row of one Pane shows; <see cref="CompareStatus"/> seen from that Pane's side.</summary>
public enum CompareMark { None, OnlyHere, Newer, Older, Same, Different }

/// <param name="Recursive">Compare subfolders too (a folder in both then counts as the same only if nothing below it differs).</param>
/// <param name="CompareContent">Same-size files are read and compared byte by byte before they count as the same.</param>
/// <param name="IncludeHidden">Look at hidden entries too.</param>
/// <param name="TimeTolerance">Modified times closer than this count as equal (FAT and network shares round to 2 seconds); default 2 seconds.</param>
public sealed record CompareOptions(
    bool Recursive = false,
    bool CompareContent = false,
    bool IncludeHidden = false,
    TimeSpan TimeTolerance = default)
{
    public static readonly TimeSpan DefaultTolerance = TimeSpan.FromSeconds(2);

    public TimeSpan Tolerance => TimeTolerance == default ? DefaultTolerance : TimeTolerance;
}

/// <summary>An entry of one side, named by its path below the compared folder.</summary>
public readonly record struct CompareItem(string RelativePath, FileEntry Entry)
{
    /// <summary>The entries of a single folder, as the items of a non-recursive comparison.</summary>
    public static IEnumerable<CompareItem> Flat(IEnumerable<FileEntry> entries) => entries.Select(e => new CompareItem(e.Name, e));
}

/// <summary>One name found in at least one of the two folders.</summary>
public sealed record CompareEntry(string RelativePath, FileEntry? Left, FileEntry? Right, CompareStatus Status)
{
    public bool IsDirectory => (Left ?? Right)!.IsDirectory;

    /// <summary>A file on one side, a folder on the other.</summary>
    public bool IsTypeMismatch => Left is not null && Right is not null && Left.IsDirectory != Right.IsDirectory;

    /// <summary>The entry on <paramref name="side"/>, if there is one.</summary>
    public FileEntry? On(CompareSide side) => side == CompareSide.Left ? Left : Right;
}

public static class CompareMarks
{
    public static CompareMark For(CompareStatus status, CompareSide side) => (status, side) switch
    {
        (CompareStatus.OnlyLeft, CompareSide.Left) or (CompareStatus.OnlyRight, CompareSide.Right) => CompareMark.OnlyHere,
        (CompareStatus.OnlyLeft, _) or (CompareStatus.OnlyRight, _) => CompareMark.None,
        (CompareStatus.LeftNewer, CompareSide.Left) or (CompareStatus.RightNewer, CompareSide.Right) => CompareMark.Newer,
        (CompareStatus.LeftNewer, _) or (CompareStatus.RightNewer, _) => CompareMark.Older,
        (CompareStatus.Same, _) => CompareMark.Same,
        _ => CompareMark.Different,
    };

    /// <summary>The marker text shown in a row.</summary>
    public static string Label(CompareMark mark, CompareSide side) => mark switch
    {
        CompareMark.OnlyHere => side == CompareSide.Left ? "only left" : "only right",
        CompareMark.Newer => "newer",
        CompareMark.Older => "older",
        CompareMark.Same => "=",
        CompareMark.Different => "differs",
        _ => "",
    };
}
