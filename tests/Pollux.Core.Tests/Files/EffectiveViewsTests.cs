using Pollux.Core.Files;
using Pollux.Core.Navigation;
using Pollux.Core.Settings;

namespace Pollux.Core.Tests.Files;

[TestClass]
public sealed class EffectiveViewsTests
{
    private static FilesSettings Settings(bool remember = true) => new()
    {
        RememberFolderViews = remember,
        ViewMode = ViewMode.Tiles,
        SortBy = SortColumn.Modified,
        SortDescending = true,
        GroupBy = GroupBy.Type,
    };

    [TestMethod]
    public void Remembering_is_on_by_default()
    {
        Assert.IsTrue(new FilesSettings().RememberFolderViews);
        Assert.IsTrue(SettingsStore.Parse("{}").Settings.Files.RememberFolderViews);
        Assert.IsFalse(SettingsStore.Parse("""{ "files": { "rememberFolderViews": false } }""").Settings.Files.RememberFolderViews);
    }

    [TestMethod]
    public void Folder_without_entry_uses_the_global_settings_not_the_tab_mode()
    {
        using var views = new FolderViews();

        var v = EffectiveViews.For(views, @"C:\a", ViewMode.Icons, Settings());

        Assert.AreEqual(new ResolvedFolderView(ViewMode.Tiles, SortColumn.Modified, true, GroupBy.Type, null, null), v);
    }

    [TestMethod]
    public void Remembered_entry_wins_and_another_folder_keeps_the_defaults()
    {
        using var views = new FolderViews();
        views.Update(@"C:\a", mode: ViewMode.Icons, sortBy: SortColumn.Size, sortDescending: false);

        var a = EffectiveViews.For(views, @"C:\a", null, Settings());
        var b = EffectiveViews.For(views, @"C:\b", null, Settings());

        Assert.AreEqual((ViewMode.Icons, SortColumn.Size, false, GroupBy.Type), (a.Mode, a.SortBy, a.SortDescending, a.GroupBy));
        Assert.AreEqual((ViewMode.Tiles, SortColumn.Modified, true), (b.Mode, b.SortBy, b.SortDescending));
    }

    [TestMethod]
    public void Off_means_tab_mode_and_global_sort_even_with_an_entry()
    {
        using var views = new FolderViews();
        views.Update(@"C:\a", mode: ViewMode.Icons, sortBy: SortColumn.Size);

        var v = EffectiveViews.For(views, @"C:\a", ViewMode.Details, Settings(remember: false));

        Assert.AreEqual(new ResolvedFolderView(ViewMode.Details, SortColumn.Modified, true, GroupBy.Type, null, null), v);
        Assert.AreEqual(ViewMode.Tiles, EffectiveViews.For(views, @"C:\a", null, Settings(remember: false)).Mode);
    }

    [TestMethod]
    public void Home_is_never_remembered()
    {
        using var views = new FolderViews();
        views.Update(WinPath.Home, mode: ViewMode.Icons);

        Assert.IsFalse(EffectiveViews.Remembers(Settings(), WinPath.Home));
        Assert.AreEqual(ViewMode.Details, EffectiveViews.For(views, WinPath.Home, ViewMode.Details, Settings()).Mode);
        Assert.IsTrue(EffectiveViews.Remembers(Settings(), WinPath.RecycleBin));
    }

    [TestMethod]
    public void Pseudo_and_shell_locations_are_keyed_as_written()
    {
        using var views = new FolderViews();
        views.Update(WinPath.ThisPc, mode: ViewMode.Icons);
        views.Update(Locations.ThisPcParsingName, sortBy: SortColumn.Size);

        Assert.AreEqual(ViewMode.Icons, EffectiveViews.For(views, WinPath.ThisPc, null, Settings()).Mode);
        Assert.AreEqual(SortColumn.Size, EffectiveViews.For(views, Locations.ThisPcParsingName, null, Settings()).SortBy);
        Assert.AreEqual(ViewMode.Tiles, EffectiveViews.For(views, WinPath.Network, null, Settings()).Mode);
    }

    [TestMethod]
    public void Same_ignores_where_the_view_came_from()
    {
        var a = new ResolvedFolderView(ViewMode.Icons, SortColumn.Size, true, GroupBy.None, null, @"C:\a");
        var b = a with { Source = @"C:\" };

        Assert.IsTrue(EffectiveViews.Same(a, b));
        Assert.IsFalse(EffectiveViews.Same(a, b with { Columns = "name|" }));
        Assert.IsFalse(EffectiveViews.Same(null, b));
    }

    [TestMethod]
    [DataRow(SortColumn.Name, false, SortColumn.Name, SortColumn.Name, true)]
    [DataRow(SortColumn.Size, true, SortColumn.Size, SortColumn.Size, false)]
    [DataRow(SortColumn.Name, false, SortColumn.Size, SortColumn.Size, true)]
    [DataRow(SortColumn.Name, false, SortColumn.Modified, SortColumn.Modified, true)]
    [DataRow(SortColumn.Name, false, SortColumn.DateTaken, SortColumn.DateTaken, true)]
    [DataRow(SortColumn.Size, true, SortColumn.Type, SortColumn.Type, false)]
    [DataRow(SortColumn.Size, true, SortColumn.Duration, SortColumn.Duration, false)]
    public void Next_sort_flips_the_sorted_column_and_starts_dates_and_sizes_descending(
        SortColumn current, bool descending, SortColumn clicked, SortColumn expected, bool expectedDescending)
    {
        Assert.AreEqual((expected, expectedDescending), EffectiveViews.NextSort(current, descending, clicked));
    }

    [TestMethod]
    public void Subfolders_inherit_only_from_locations_that_have_subfolders()
    {
        Assert.IsTrue(EffectiveViews.CanApplyToSubfolders(LocationKind.Folder));
        Assert.IsTrue(EffectiveViews.CanApplyToSubfolders(LocationKind.Zip));
        Assert.IsTrue(EffectiveViews.CanApplyToSubfolders(LocationKind.Server));
        Assert.IsFalse(EffectiveViews.CanApplyToSubfolders(LocationKind.Shell));
        Assert.IsFalse(EffectiveViews.CanApplyToSubfolders(LocationKind.RecycleBin));
        Assert.IsFalse(EffectiveViews.CanApplyToSubfolders(LocationKind.Home));
    }

    [TestMethod]
    public void Projection_sorts_and_groups_by_the_folder_view()
    {
        const string folder = @"C:\x";
        FileEntry F(string name, long size) => new(name, folder + "\\" + name, false, size, new DateTime(2026, 1, 1), FileAttributes.Normal);
        var view = new ResolvedFolderView(ViewMode.Icons, SortColumn.Size, true, GroupBy.None, null, folder);

        var l = FolderProjection.Project([F("a.txt", 1), F("b.txt", 30), F("c.txt", 2)], folder, new PolluxSettings(), null, null, [], null, view: view);

        CollectionAssert.AreEqual(new[] { "b.txt", "c.txt", "a.txt" }, l.Rows.Select(r => r.Entry.Name).ToArray());
        Assert.AreEqual(0, l.Groups?.Count ?? 0);
    }
}
