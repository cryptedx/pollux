using Pollux.Core.Git;
using Pollux.Core.Navigation;
using Pollux.Core.Settings;

namespace Pollux.Core.Files;

/// <summary>One visible row of a folder listing.</summary>
public readonly record struct FolderRow(FileEntry Entry, IReadOnlyList<MatchRange> Highlights, GitMark Git);

/// <summary>The rows a folder shows and which of them (by index) to select.</summary>
public sealed record FolderListing(IReadOnlyList<FolderRow> Rows, IReadOnlyList<int> Selected, IReadOnlyList<RowGroup>? Groups = null);

/// <summary>The working-tree status of the repository <paramref name="Root"/>, for git marks.</summary>
public sealed record FolderGit(GitStatus Status, string Root);

/// <summary>The folder listing rules: which entries are visible, in what order, with which marks, and what gets selected.</summary>
public static class FolderProjection
{
    /// <summary>Suffix of a file a running Transfer is still writing; never listed.</summary>
    public const string PartSuffix = ".pollux-part";

    /// <summary>
    /// Hides in-flight Transfer files and hidden/system entries (per settings), sorts, applies the Filter and adds git marks.
    /// Selects the entry named <paramref name="focusName"/> if given, else the entries named in <paramref name="selectedNames"/>
    /// (case-insensitive), else the first row. <paramref name="folderSizes"/> (full path to bytes) gives folders their computed size;
    /// <paramref name="properties"/> holds the values a shell property sort column sorts by; <paramref name="view"/> (a folder's
    /// remembered view) replaces the sort and grouping settings.
    /// </summary>
    public static FolderListing Project(
        IEnumerable<FileEntry> entries,
        string folder,
        PolluxSettings settings,
        string? filter,
        FolderGit? git,
        IReadOnlyList<string> selectedNames,
        string? focusName,
        DateTime? now = null,
        IReadOnlyDictionary<string, long>? folderSizes = null,
        PropertyValues? properties = null,
        ResolvedFolderView? view = null)
    {
        var s = settings.Files;
        var comparer = new EntryComparer(KnownColumns.For(view?.SortBy ?? s.SortBy), view?.SortDescending ?? s.SortDescending, s.FoldersFirst, properties);
        var query = filter ?? "";
        var gitHere = git is not null && WinPath.IsSameOrUnder(folder, git.Root) ? git : null;
        var relative = gitHere is null ? "" : RepoRelative(folder, gitHere.Root);
        var rows = new List<FolderRow>();
        if (folderSizes is { Count: > 0 })
        {
            entries = entries.Select(e => e.IsDirectory && folderSizes.TryGetValue(e.FullPath, out var size) ? e with { Size = size } : e);
        }

        foreach (var e in entries.Order(comparer))
        {
            if (e.Name.EndsWith(PartSuffix, StringComparison.OrdinalIgnoreCase)) continue;
            if (!s.ShowHidden && e.IsHidden) continue;
            if (!s.ShowSystem && e.IsSystem && e.IsHidden) continue;
            var m = FilterMatcher.Match(e.Name, query, settings.Filter.MatchInitials);
            if (!m.IsMatch) continue;
            var mark = gitHere is null ? GitMark.None : gitHere.Status.MarkFor(relative, e.Name, e.IsDirectory);
            rows.Add(new FolderRow(e, m.Ranges, mark));
        }

        var (grouped, groups) = EntryGrouping.Apply(rows, view?.GroupBy ?? s.GroupBy, now ?? DateTime.Now);
        rows = grouped;

        IReadOnlyList<string> names = focusName is { } f ? [f] : selectedNames;
        var selected = new List<int>();
        if (names.Count > 0)
        {
            var set = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < rows.Count; i++)
            {
                if (set.Contains(rows[i].Entry.Name)) selected.Add(i);
            }
        }
        else if (rows.Count > 0)
        {
            selected.Add(0);
        }

        return new FolderListing(rows, selected, groups);
    }

    /// <summary><paramref name="folder"/> relative to the repository root, with "/" separators ("" for the root itself).</summary>
    public static string RepoRelative(string folder, string root) =>
        folder.Length <= root.Length ? "" : folder[(root.TrimEnd('\\').Length + 1)..].Replace('\\', '/');
}
