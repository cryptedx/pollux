using Pollux.Core.Files;
using Pollux.Core.Git;
using Pollux.Core.Settings;

namespace Pollux.Core.Tests.Files;

[TestClass]
public sealed class FolderProjectionTests
{
    private const string Folder = @"C:\repo\src";

    private static FileEntry F(string name, FileAttributes attributes = FileAttributes.Normal) =>
        new(name, Folder + "\\" + name, false, 0, new DateTime(2026, 1, 1), attributes);

    private static FileEntry D(string name) =>
        new(name, Folder + "\\" + name, true, 0, new DateTime(2026, 1, 1), FileAttributes.Directory);

    private static FolderListing Project(
        IEnumerable<FileEntry> entries,
        PolluxSettings? settings = null,
        string? filter = null,
        FolderGit? git = null,
        IReadOnlyList<string>? selected = null,
        string? focus = null) =>
        FolderProjection.Project(entries, Folder, settings ?? new PolluxSettings(), filter, git, selected ?? [], focus);

    private static string[] Names(FolderListing l) => [.. l.Rows.Select(r => r.Entry.Name)];

    private static string[] SelectedNames(FolderListing l) => [.. l.Selected.Select(i => l.Rows[i].Entry.Name)];

    [TestMethod]
    public void HidesInFlightTransferFiles()
    {
        var l = Project([F("a.txt"), F(".a.txt.0f3c.POLLUX-PART")]);

        CollectionAssert.AreEqual(new[] { "a.txt" }, Names(l));
    }

    [TestMethod]
    public void HidesHiddenEntriesUnlessShown()
    {
        var entries = new[] { F("a.txt"), F("h.txt", FileAttributes.Hidden) };
        var settings = new PolluxSettings();

        CollectionAssert.AreEqual(new[] { "a.txt" }, Names(Project(entries, settings)));
        settings.Files.ShowHidden = true;
        CollectionAssert.AreEqual(new[] { "a.txt", "h.txt" }, Names(Project(entries, settings)));
    }

    [TestMethod]
    public void HidesHiddenSystemEntriesUnlessSystemIsShown()
    {
        var entries = new[] { F("a.txt"), F("s.sys", FileAttributes.System), F("hs.sys", FileAttributes.Hidden | FileAttributes.System) };
        var settings = new PolluxSettings();
        settings.Files.ShowHidden = true;

        CollectionAssert.AreEqual(new[] { "a.txt", "s.sys" }, Names(Project(entries, settings)));
        settings.Files.ShowSystem = true;
        CollectionAssert.AreEqual(new[] { "a.txt", "hs.sys", "s.sys" }, Names(Project(entries, settings)));
    }

    [TestMethod]
    public void SortsFoldersFirstPerSettings()
    {
        var entries = new[] { F("b.txt"), D("z"), F("a.txt") };
        var settings = new PolluxSettings();

        CollectionAssert.AreEqual(new[] { "z", "a.txt", "b.txt" }, Names(Project(entries, settings)));
        settings.Files.FoldersFirst = false;
        CollectionAssert.AreEqual(new[] { "a.txt", "b.txt", "z" }, Names(Project(entries, settings)));
    }

    [TestMethod]
    public void FilterKeepsMatchesWithRanges()
    {
        var l = Project([F("readme.md"), F("Directory.Build.props"), F("main.cs")], filter: "dbp");

        CollectionAssert.AreEqual(new[] { "Directory.Build.props" }, Names(l));
        Assert.AreEqual(3, l.Rows[0].Highlights.Count);
    }

    [TestMethod]
    public void InitialsMatchingFollowsTheFilterSetting()
    {
        var settings = new PolluxSettings();
        settings.Filter.MatchInitials = false;

        Assert.AreEqual(0, Project([F("Directory.Build.props")], settings, filter: "dbp").Rows.Count);
    }

    [TestMethod]
    public void SelectsTheFirstRowWhenNothingIsRemembered()
    {
        var l = Project([F("b"), F("a")]);

        CollectionAssert.AreEqual(new[] { "a" }, SelectedNames(l));
    }

    [TestMethod]
    public void SelectsNothingInAnEmptyFolder() => Assert.AreEqual(0, Project([]).Selected.Count);

    [TestMethod]
    public void RestoresSelectionByNameIgnoringCase()
    {
        var l = Project([F("a"), F("b"), F("c")], selected: ["C", "A", "gone"]);

        CollectionAssert.AreEqual(new[] { "a", "c" }, SelectedNames(l));
    }

    [TestMethod]
    public void RememberedSelectionThatIsGoneSelectsNothing()
    {
        var l = Project([F("a")], selected: ["gone"]);

        Assert.AreEqual(0, l.Selected.Count);
    }

    [TestMethod]
    public void FocusNameWinsOverRememberedSelection()
    {
        var l = Project([F("a"), F("b"), F("c")], selected: ["a", "b"], focus: "C");

        CollectionAssert.AreEqual(new[] { "c" }, SelectedNames(l));
    }

    [TestMethod]
    public void MarksEntriesInsideTheRepository()
    {
        var git = new FolderGit(GitStatus.Parse(" M src/a.txt\0?? src/new.txt\0"), @"C:\repo");

        var l = Project([F("a.txt"), F("new.txt"), F("same.txt")], git: git);

        CollectionAssert.AreEqual(new[] { GitMark.Modified, GitMark.Untracked, GitMark.None }, l.Rows.Select(r => r.Git).ToArray());
    }

    [TestMethod]
    public void NoMarksOutsideTheRepository()
    {
        var git = new FolderGit(GitStatus.Parse(" M src/a.txt\0"), @"D:\other");

        Assert.AreEqual(GitMark.None, Project([F("a.txt")], git: git).Rows[0].Git);
    }

    [TestMethod]
    [DataRow(@"C:\repo", @"C:\repo", "")]
    [DataRow(@"C:\repo\src\ui", @"C:\repo", "src/ui")]
    [DataRow(@"C:\src", @"C:\", "src")]
    public void RepoRelative(string folder, string root, string expected) =>
        Assert.AreEqual(expected, FolderProjection.RepoRelative(folder, root));
}
