using Pollux.Core.Files;
using Pollux.Core.Operations;

namespace Pollux.Core.Compare;

/// <summary>One thing a sync brings over: <paramref name="Replaces"/> is true when it overwrites an older file.</summary>
public sealed record SyncItem(string Source, string TargetDirectory, string RelativePath, bool IsDirectory, bool Replaces, long Size);

/// <summary>
/// What a one-way sync from one folder to the other copies: what is only there and what is newer there. It never
/// deletes, never touches what is newer on the target and leaves a file-against-folder clash for the user.
/// </summary>
public sealed class SyncPlan(string sourceFolder, string targetFolder, IReadOnlyList<SyncItem> items, int clashes, bool foldersSized)
{
    private const int MaxListed = 12;

    public string SourceFolder { get; } = sourceFolder;
    public string TargetFolder { get; } = targetFolder;
    public IReadOnlyList<SyncItem> Items { get; } = items;

    /// <summary>Names that are a file on one side and a folder on the other; the sync leaves them alone.</summary>
    public int Clashes { get; } = clashes;

    public bool IsEmpty => Items.Count == 0;
    public int NewCount => Items.Count(i => !i.Replaces);
    public int ReplaceCount => Items.Count(i => i.Replaces);

    /// <summary>Size of everything to copy. A folder's contents are only counted when the comparison looked into subfolders.</summary>
    public long TotalBytes => Items.Sum(i => i.Size);

    /// <summary>False when a folder is copied whose contents were not scanned, so <see cref="TotalBytes"/> is a minimum.</summary>
    public bool SizeIsExact { get; } = foldersSized || items.All(i => !i.IsDirectory);

    /// <summary>
    /// The copies to queue, one per target folder. A file that appeared on the target in the meantime is only
    /// replaced if the source is newer, so the user's confirmation never costs a newer file.
    /// </summary>
    public IReadOnlyList<TransferRequest> ToRequests() =>
    [
        .. Items
            .GroupBy(i => i.TargetDirectory, StringComparer.OrdinalIgnoreCase)
            .Select(g => new TransferRequest(TransferKind.Copy, [.. g.Select(i => i.Source)], g.First().TargetDirectory, ConflictPolicy.OverwriteIfNewer)),
    ];

    /// <summary>The confirmation dialog body: the counts, then the changes (the first few), then what stays as it is.</summary>
    public string ConfirmText
    {
        get
        {
            var lines = new List<string>
            {
                $"Copy {Describe()} ({(SizeIsExact ? "" : "at least ")}{Format.Size(TotalBytes)}) from",
                SourceFolder,
                "to",
                TargetFolder + "?",
                "Nothing is deleted.",
                "",
            };
            foreach (var i in Items.Take(MaxListed))
            {
                lines.Add($"{(i.Replaces ? "newer" : "new  ")}  {i.RelativePath}{(i.IsDirectory ? Path.DirectorySeparatorChar.ToString() : "")}");
            }

            if (Items.Count > MaxListed) lines.Add($"… and {Items.Count - MaxListed} more");
            if (Clashes > 0) lines.Add($"\n{Clashes} name(s) are a file on one side and a folder on the other and are skipped.");
            return string.Join("\n", lines);
        }
    }

    private string Describe()
    {
        var parts = new List<string>();
        if (NewCount > 0) parts.Add($"{NewCount} new");
        if (ReplaceCount > 0) parts.Add($"{ReplaceCount} newer");
        return string.Join(" and ", parts) + (Items.Count == 1 ? " item" : " items");
    }
}

public static class FolderSync
{
    /// <summary>The plan for copying from the <paramref name="from"/> side of <paramref name="comparison"/> to the other.</summary>
    public static SyncPlan Plan(FolderComparison comparison, CompareSide from)
    {
        var to = from == CompareSide.Left ? CompareSide.Right : CompareSide.Left;
        var onlyHere = from == CompareSide.Left ? CompareStatus.OnlyLeft : CompareStatus.OnlyRight;
        var newerHere = from == CompareSide.Left ? CompareStatus.LeftNewer : CompareStatus.RightNewer;
        var wholeFolders = comparison.Entries
            .Where(e => e.Status == onlyHere && e.IsDirectory)
            .Select(e => e.RelativePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var items = new List<SyncItem>();
        foreach (var e in comparison.Entries)
        {
            if (e.Status != onlyHere && e.Status != newerHere) continue;
            // Below a folder that is copied as a whole, the folder's copy brings it along.
            if (IsBelowAny(e.RelativePath, wholeFolders)) continue;
            var source = e.On(from)!;
            var parent = Path.GetDirectoryName(e.RelativePath) ?? "";
            var targetDirectory = parent.Length == 0 ? comparison.FolderOf(to) : Path.Combine(comparison.FolderOf(to), parent);
            var size = e.IsDirectory
                ? comparison.Entries.Where(c => !c.IsDirectory && IsBelow(c.RelativePath, e.RelativePath)).Sum(c => c.On(from)?.Size ?? 0)
                : source.Size;
            items.Add(new SyncItem(source.FullPath, targetDirectory, e.RelativePath, e.IsDirectory, e.Status == newerHere, size));
        }

        return new SyncPlan(comparison.FolderOf(from), comparison.FolderOf(to), items, comparison.Entries.Count(e => e.IsTypeMismatch), comparison.Options.Recursive);
    }

    private static bool IsBelow(string relativePath, string folder) =>
        relativePath.Length > folder.Length
        && relativePath.StartsWith(folder, StringComparison.OrdinalIgnoreCase)
        && relativePath[folder.Length] is '\\' or '/';

    private static bool IsBelowAny(string relativePath, HashSet<string> folders)
    {
        for (var p = Path.GetDirectoryName(relativePath); !string.IsNullOrEmpty(p); p = Path.GetDirectoryName(p))
        {
            if (folders.Contains(p)) return true;
        }

        return false;
    }
}
