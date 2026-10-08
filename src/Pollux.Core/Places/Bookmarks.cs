namespace Pollux.Core.Places;

public sealed class Bookmark
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
}

public sealed class BookmarkGroup
{
    /// <summary>Empty name = ungrouped Bookmarks, shown first.</summary>
    public string Name { get; set; } = "";
    public bool Collapsed { get; set; }
    public List<Bookmark> Items { get; set; } = [];
}

/// <summary>The user's Bookmarks: ordered groups of ordered locations. Global (Workspace-scoped Bookmarks come with Workspaces).</summary>
public sealed class BookmarkList
{
    public List<BookmarkGroup> Groups { get; set; } = [];

    /// <summary>Bookmarks in display order; the first nine get Ctrl+Shift+1…9.</summary>
    public IEnumerable<Bookmark> Flat => Groups.SelectMany(g => g.Items);

    public bool Contains(string path) => Flat.Any(b => string.Equals(b.Path, path, StringComparison.OrdinalIgnoreCase));

    public Bookmark Add(string path, string name, string group = "")
    {
        var existing = Flat.FirstOrDefault(b => string.Equals(b.Path, path, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            return existing;
        }

        var g = Groups.FirstOrDefault(x => x.Name == group);
        if (g is null)
        {
            g = new BookmarkGroup { Name = group };
            if (group.Length == 0) Groups.Insert(0, g); else Groups.Add(g);
        }

        var bookmark = new Bookmark { Name = name, Path = path };
        g.Items.Add(bookmark);
        return bookmark;
    }

    public bool Remove(string path)
    {
        foreach (var g in Groups)
        {
            if (g.Items.RemoveAll(b => string.Equals(b.Path, path, StringComparison.OrdinalIgnoreCase)) > 0)
            {
                Groups.RemoveAll(x => x.Items.Count == 0 && x.Name.Length == 0);
                return true;
            }
        }

        return false;
    }

    /// <summary>Moves a Bookmark to a position inside a (possibly different) group.</summary>
    public void Move(string path, string group, int index)
    {
        var bookmark = Flat.FirstOrDefault(b => string.Equals(b.Path, path, StringComparison.OrdinalIgnoreCase));
        if (bookmark is null) return;
        foreach (var g in Groups) g.Items.Remove(bookmark);
        var target = Groups.FirstOrDefault(x => x.Name == group);
        if (target is null)
        {
            target = new BookmarkGroup { Name = group };
            Groups.Add(target);
        }

        target.Items.Insert(Math.Clamp(index, 0, target.Items.Count), bookmark);
    }
}
