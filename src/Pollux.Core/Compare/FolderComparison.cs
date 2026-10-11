using Pollux.Core.Files;

namespace Pollux.Core.Compare;

/// <summary>How many entries fall into each <see cref="CompareStatus"/>.</summary>
public sealed record CompareSummary(int OnlyLeft, int OnlyRight, int LeftNewer, int RightNewer, int Same, int Different)
{
    public int Total => OnlyLeft + OnlyRight + LeftNewer + RightNewer + Same + Different;

    public bool IsIdentical => Total == Same;

    /// <summary>The status-bar text, e.g. "3 only left · 1 left newer · 40 same"; says so when nothing differs.</summary>
    public string Text
    {
        get
        {
            if (Total == 0) return "both folders are empty";
            if (IsIdentical) return $"identical ({Same} items)";
            var parts = new List<string>();
            if (OnlyLeft > 0) parts.Add($"{OnlyLeft} only left");
            if (OnlyRight > 0) parts.Add($"{OnlyRight} only right");
            if (LeftNewer > 0) parts.Add($"{LeftNewer} left newer");
            if (RightNewer > 0) parts.Add($"{RightNewer} right newer");
            if (Different > 0) parts.Add($"{Different} differ");
            if (Same > 0) parts.Add($"{Same} same");
            return string.Join(" · ", parts);
        }
    }
}

/// <summary>The result of comparing two folders: one <see cref="CompareEntry"/> per name, ordered by path.</summary>
public sealed class FolderComparison
{
    private readonly Dictionary<string, CompareEntry> _byPath;

    public FolderComparison(string leftFolder, string rightFolder, CompareOptions options, IReadOnlyList<CompareEntry> entries)
    {
        LeftFolder = leftFolder;
        RightFolder = rightFolder;
        Options = options;
        Entries = entries;
        _byPath = new Dictionary<string, CompareEntry>(entries.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var e in entries) _byPath[e.RelativePath] = e;
        Summary = Summarize(entries, options.Recursive);
    }

    public string LeftFolder { get; }
    public string RightFolder { get; }
    public CompareOptions Options { get; }
    public IReadOnlyList<CompareEntry> Entries { get; }
    public CompareSummary Summary { get; }

    public string FolderOf(CompareSide side) => side == CompareSide.Left ? LeftFolder : RightFolder;

    public CompareEntry? Find(string relativePath) => _byPath.GetValueOrDefault(relativePath);

    /// <summary>The marker of the row named <paramref name="name"/> in the folder shown on <paramref name="side"/>.</summary>
    public CompareMark MarkFor(CompareSide side, string name) =>
        _byPath.TryGetValue(name, out var e) && e.On(side) is not null ? CompareMarks.For(e.Status, side) : CompareMark.None;

    /// <summary>
    /// The names in the folder on <paramref name="side"/> that copying to the other side would bring over: entries only
    /// there, newer there, or different at the same time. Entries older there and files against folders are left out.
    /// </summary>
    public IReadOnlyList<string> SelectableNames(CompareSide side) =>
    [
        .. Entries
            .Where(e => e.On(side) is not null && !e.IsTypeMismatch && !IsNested(e.RelativePath)
                && CompareMarks.For(e.Status, side) is CompareMark.OnlyHere or CompareMark.Newer or CompareMark.Different)
            .Select(e => e.On(side)!.Name),
    ];

    internal static bool IsNested(string relativePath) => relativePath.AsSpan().IndexOfAny('\\', '/') >= 0;

    private static CompareSummary Summarize(IReadOnlyList<CompareEntry> entries, bool recursive)
    {
        // Recursive: folders in both stand for what is below them, and what is below is counted file by file.
        var counted = entries.Where(e => !recursive || !e.IsDirectory).ToList();
        int N(CompareStatus s) => counted.Count(e => e.Status == s);
        return new CompareSummary(N(CompareStatus.OnlyLeft), N(CompareStatus.OnlyRight), N(CompareStatus.LeftNewer), N(CompareStatus.RightNewer), N(CompareStatus.Same), N(CompareStatus.Different));
    }
}
