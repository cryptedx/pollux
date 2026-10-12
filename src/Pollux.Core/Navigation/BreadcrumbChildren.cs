using Pollux.Core.Files;

namespace Pollux.Core.Navigation;

public sealed record BreadcrumbChild(string Name, string FullPath);

/// <summary>The subfolders for a breadcrumb chevron menu, capped; <see cref="More"/> counts the ones left out ("more...").</summary>
public sealed record BreadcrumbChildren(IReadOnlyList<BreadcrumbChild> Items, int More)
{
    public static readonly BreadcrumbChildren None = new([], 0);
}

public static class BreadcrumbMenu
{
    public const int DefaultMax = 100;

    /// <summary>Subfolders of a breadcrumb segment in natural order; empty for virtual locations (home, recycle bin) and unreadable folders.</summary>
    public static BreadcrumbChildren Children(
        string path,
        bool showHidden,
        IDirectoryLister lister,
        int max = DefaultMax,
        CancellationToken cancellationToken = default)
    {
        var p = WinPath.Normalize(path);
        if (!WinPath.IsDriveRooted(p) && !(WinPath.IsUnc(p) && p[2..].Contains('\\')))
        {
            return BreadcrumbChildren.None;
        }

        IReadOnlyList<DirectoryItem> items;
        try
        {
            items = lister.List(p, cancellationToken);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return BreadcrumbChildren.None;
        }

        var folders = items
            .Where(i => i.IsDirectory && (showHidden || !i.IsHidden))
            .OrderBy(i => i.Name, Comparer<string>.Create(EntryComparer.NaturalCompare))
            .ToList();
        var shown = folders.Take(max).Select(i => new BreadcrumbChild(i.Name, WinPath.Combine(p, i.Name))).ToList();
        return new BreadcrumbChildren(shown, folders.Count - shown.Count);
    }
}
