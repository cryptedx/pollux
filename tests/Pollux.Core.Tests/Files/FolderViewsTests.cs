using Pollux.Core.Files;
using Pollux.Core.Navigation;
using Pollux.Core.Settings;

namespace Pollux.Core.Tests.Files;

[TestClass]
public sealed class FolderViewsTests
{
    private static readonly FolderViewDefaults Defaults = new(ViewMode.Details, SortColumn.Name, false, GroupBy.None);

    private static ResolvedFolderView View(ViewMode mode, SortColumn sort = SortColumn.Name, bool desc = false, GroupBy group = GroupBy.None) =>
        new(mode, sort, desc, group, null, null);

    private static string TempFile() => Path.Combine(Path.GetTempPath(), "pollux-fv-" + Guid.NewGuid().ToString("N"), "folder-views.json");

    [TestMethod]
    public void Unknown_folder_gets_the_defaults()
    {
        using var views = new FolderViews();

        Assert.AreEqual(View(ViewMode.Details), views.Resolve(@"C:\a", Defaults));
    }

    [TestMethod]
    public void Own_entry_wins_per_field_and_ignores_case_and_trailing_separator()
    {
        using var views = new FolderViews();
        views.Update(@"C:\Photos", mode: ViewMode.Icons);

        var v = views.Resolve(@"c:/photos\", Defaults with { SortBy = SortColumn.Size });

        Assert.AreEqual(ViewMode.Icons, v.Mode);
        Assert.AreEqual(SortColumn.Size, v.SortBy);
        Assert.AreEqual(@"C:\Photos", v.Source);
    }

    [TestMethod]
    public void Update_keeps_fields_it_is_not_given()
    {
        using var views = new FolderViews();
        views.Update(@"C:\a", mode: ViewMode.Tiles);
        views.Update(@"C:\a", sortBy: SortColumn.Modified, sortDescending: true);

        var v = views.Resolve(@"C:\a", Defaults);

        Assert.AreEqual((ViewMode.Tiles, SortColumn.Modified, true), (v.Mode, v.SortBy, v.SortDescending));
    }

    [TestMethod]
    public void Subfolders_inherit_only_from_entries_that_apply_to_them()
    {
        using var views = new FolderViews();
        views.Update(@"C:\a", mode: ViewMode.Tiles);

        Assert.AreEqual(ViewMode.Details, views.Resolve(@"C:\a\b", Defaults).Mode);

        views.ApplyToSubfolders(@"C:\a", views.Resolve(@"C:\a", Defaults));

        Assert.AreEqual(ViewMode.Tiles, views.Resolve(@"C:\a\b\c", Defaults).Mode);
        Assert.AreEqual(ViewMode.Details, views.Resolve(@"C:\ab", Defaults).Mode);
    }

    [TestMethod]
    public void Nearest_ancestor_and_own_entry_take_precedence()
    {
        using var views = new FolderViews();
        views.ApplyToSubfolders(@"C:\a", View(ViewMode.Tiles));
        views.ApplyToSubfolders(@"C:\a\b", View(ViewMode.Icons));
        views.Update(@"C:\a\b\c", mode: ViewMode.Details);

        Assert.AreEqual(ViewMode.Icons, views.Resolve(@"C:\a\b\x", Defaults).Mode);
        Assert.AreEqual(ViewMode.Details, views.Resolve(@"C:\a\b\c", Defaults).Mode);
        Assert.AreEqual(ViewMode.Icons, views.Resolve(@"C:\a\b\c\d", Defaults).Mode);
    }

    [TestMethod]
    public void Own_entry_without_a_field_falls_back_to_the_ancestor()
    {
        using var views = new FolderViews();
        views.ApplyToSubfolders(@"C:\a", View(ViewMode.Tiles, SortColumn.Size, true, GroupBy.Type));
        views.Update(@"C:\a\b", groupBy: GroupBy.None);

        var v = views.Resolve(@"C:\a\b", Defaults);

        Assert.AreEqual((ViewMode.Tiles, SortColumn.Size, true, GroupBy.None), (v.Mode, v.SortBy, v.SortDescending, v.GroupBy));
    }

    [TestMethod]
    public void Applying_to_subfolders_drops_their_own_entries()
    {
        using var views = new FolderViews();
        views.Update(@"C:\a\b", mode: ViewMode.Icons);
        views.Update(@"C:\other", mode: ViewMode.Icons);

        views.ApplyToSubfolders(@"C:\a", View(ViewMode.Tiles));

        Assert.IsNull(views.Find(@"C:\a\b"));
        Assert.IsNotNull(views.Find(@"C:\other"));
        Assert.AreEqual(ViewMode.Tiles, views.Resolve(@"C:\a\b", Defaults).Mode);
    }

    [TestMethod]
    public void Reset_removes_the_own_entry()
    {
        using var views = new FolderViews();
        views.Update(@"C:\a", mode: ViewMode.Icons);

        Assert.IsTrue(views.Reset(@"C:\A"));
        Assert.IsFalse(views.Reset(@"C:\A"));
        Assert.AreEqual(ViewMode.Details, views.Resolve(@"C:\a", Defaults).Mode);
    }

    [TestMethod]
    public void Pseudo_locations_are_keyed_as_is_and_do_not_inherit()
    {
        using var views = new FolderViews();
        views.ApplyToSubfolders(@"C:\", View(ViewMode.Tiles));
        views.Update(WinPath.RecycleBin, mode: ViewMode.Icons);
        views.ApplyToSubfolders(WinPath.Home, View(ViewMode.Icons));

        Assert.AreEqual(ViewMode.Icons, views.Resolve("POLLUX:Recycle-Bin", Defaults).Mode);
        Assert.AreEqual(ViewMode.Details, views.Resolve("pollux:other", Defaults).Mode);
        Assert.IsNull(views.Find(WinPath.Home));
    }

    [TestMethod]
    public void Least_recently_used_entries_are_evicted()
    {
        using var views = new FolderViews(capacity: 3);
        views.Update(@"C:\1", mode: ViewMode.Tiles);
        views.Update(@"C:\2", mode: ViewMode.Tiles);
        views.Update(@"C:\3", mode: ViewMode.Tiles);
        views.Resolve(@"C:\1", Defaults);

        views.Update(@"C:\4", mode: ViewMode.Tiles);

        Assert.AreEqual(3, views.Count);
        Assert.IsNotNull(views.Find(@"C:\1"));
        Assert.IsNull(views.Find(@"C:\2"));
    }

    [TestMethod]
    public void Flush_round_trips_through_the_file()
    {
        var file = TempFile();
        using (var views = new FolderViews(file))
        {
            views.Update(@"C:\a", ViewMode.Icons, SortColumn.Size, true, GroupBy.Modified, "name:200");
            views.ApplyToSubfolders(@"C:\b", View(ViewMode.Tiles));
            views.Flush();
        }

        using var loaded = new FolderViews(file);
        var a = loaded.Resolve(@"C:\a", Defaults);

        Assert.AreEqual((ViewMode.Icons, SortColumn.Size, true, GroupBy.Modified, "name:200"), (a.Mode, a.SortBy, a.SortDescending, a.GroupBy, a.Columns));
        Assert.AreEqual(ViewMode.Tiles, loaded.Resolve(@"C:\b\x", Defaults).Mode);
        StringAssert.Contains(File.ReadAllText(file), "\"icons\"", StringComparison.OrdinalIgnoreCase);
    }

    [TestMethod]
    public void Saving_is_debounced()
    {
        var file = TempFile();
        using var views = new FolderViews(file, debounce: TimeSpan.FromMilliseconds(100));

        views.Update(@"C:\a", mode: ViewMode.Icons);
        Assert.IsFalse(File.Exists(file));

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!File.Exists(file) && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(20);
        }

        Assert.IsTrue(File.Exists(file));
    }

    [TestMethod]
    public void Dispose_writes_pending_changes()
    {
        var file = TempFile();
        using (var views = new FolderViews(file))
        {
            views.Update(@"C:\a", mode: ViewMode.Icons);
        }

        using var loaded = new FolderViews(file);
        Assert.AreEqual(ViewMode.Icons, loaded.Resolve(@"C:\a", Defaults).Mode);
    }

    [TestMethod]
    [DataRow("not json")]
    [DataRow("{\"entries\":[{\"path\":\"C:\\\\a\",\"mode\":\"bogus\"}]}")]
    [DataRow("")]
    public void Corrupt_file_reads_as_empty(string content)
    {
        var file = TempFile();
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, content);

        using var views = new FolderViews(file);

        Assert.AreEqual(0, views.Count);
        Assert.AreEqual(ViewMode.Details, views.Resolve(@"C:\a", Defaults).Mode);
    }

    [TestMethod]
    public void Loading_a_larger_file_keeps_the_newest_entries()
    {
        var file = TempFile();
        using (var views = new FolderViews(file))
        {
            for (var i = 0; i < 5; i++)
            {
                views.Update($@"C:\{i}", mode: ViewMode.Tiles);
            }
        }

        using var loaded = new FolderViews(file, capacity: 2);

        Assert.AreEqual(2, loaded.Count);
        Assert.IsNotNull(loaded.Find(@"C:\4"));
        Assert.IsNull(loaded.Find(@"C:\0"));
    }
}
