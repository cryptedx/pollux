using Pollux.Core.Files;
using Pollux.Core.Settings;

namespace Pollux.Core.Tests.Files;

[TestClass]
public sealed class ColumnsAndGroupingTests
{
    // Thursday.
    private static readonly DateTime Now = new(2026, 10, 15, 12, 0, 0);

    private static FileEntry F(string name, DateTime? modified = null, DateTime? created = null, FileAttributes attrs = FileAttributes.Normal) =>
        new(name, name, false, 0, modified ?? Now, attrs, created ?? default);

    private static FileEntry D(string name, DateTime? modified = null) =>
        new(name, name, true, 0, modified ?? Now, FileAttributes.Directory);

    private static string[] Sorted(SortColumn column, bool descending, params FileEntry[] entries) =>
        [.. entries.Order(new EntryComparer(column, descending, false)).Select(e => e.Name)];

    [TestMethod]
    public void SortsByCreated()
    {
        var a = F("a", created: new DateTime(2026, 3, 1));
        var b = F("b", created: new DateTime(2025, 3, 1));

        CollectionAssert.AreEqual(new[] { "b", "a" }, Sorted(SortColumn.Created, false, a, b));
        CollectionAssert.AreEqual(new[] { "a", "b" }, Sorted(SortColumn.Created, true, a, b));
    }

    [TestMethod]
    public void SortsByExtensionThenName()
    {
        var sorted = Sorted(SortColumn.Extension, false, F("b.md"), F("z.cs"), F("noext"), F("a.md"));

        CollectionAssert.AreEqual(new[] { "noext", "z.cs", "a.md", "b.md" }, sorted);
    }

    [TestMethod]
    public void SortsByAttributes()
    {
        var plain = F("plain", attrs: FileAttributes.Archive);
        var hidden = F("hidden", attrs: FileAttributes.Hidden | FileAttributes.Archive);
        var ro = F("ro", attrs: FileAttributes.ReadOnly);

        // "-H-A" < "---A" is false since '-' sorts before 'H': "---A" < "-H-A" < "R---".
        CollectionAssert.AreEqual(new[] { "plain", "hidden", "ro" }, Sorted(SortColumn.Attributes, false, ro, hidden, plain));
    }

    [TestMethod]
    public void FormatsAttributeLetters()
    {
        Assert.AreEqual("RHSA", Format.AttributeLetters(FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System | FileAttributes.Archive));
        Assert.AreEqual("---A", Format.AttributeLetters(FileAttributes.Archive));
        Assert.AreEqual("----", Format.AttributeLetters(FileAttributes.Directory));
    }

    [TestMethod]
    public void ParsesColumnIds()
    {
        var parsed = ListColumns.Parse(["Created", "bogus", " extension "]);

        Assert.AreEqual(OptionalColumns.Created | OptionalColumns.Extension, parsed);
        Assert.AreEqual(OptionalColumns.None, ListColumns.Parse(null));
    }

    [TestMethod]
    public void TogglesColumns()
    {
        var on = ListColumns.Toggle(["extension"], OptionalColumns.Created);
        CollectionAssert.AreEqual(new[] { "created", "extension" }, on);

        var off = ListColumns.Toggle(on, OptionalColumns.Extension);
        CollectionAssert.AreEqual(new[] { "created" }, off);
    }

    [TestMethod]
    [DataRow("2026-10-15 00:00", 0)] // today
    [DataRow("2026-10-14 23:59", 1)] // yesterday
    [DataRow("2026-10-12 08:00", 2)] // Monday of this week
    [DataRow("2026-10-11 23:00", 3)] // Sunday: previous week, same month
    [DataRow("2026-10-01 00:00", 3)]
    [DataRow("2026-09-30 23:00", 4)]
    [DataRow("2026-10-20 10:00", 0)] // future counts as today
    public void BucketsDates(string modified, int expected) =>
        Assert.AreEqual(expected, EntryGrouping.DateBucket(DateTime.Parse(modified, System.Globalization.CultureInfo.InvariantCulture), Now));

    [TestMethod]
    public void WeekBucketReachesIntoPreviousMonth()
    {
        // Thursday 2026-10-01: its week started on Monday 2026-09-28.
        var now = new DateTime(2026, 10, 1, 9, 0, 0);

        Assert.AreEqual(2, EntryGrouping.DateBucket(new DateTime(2026, 9, 29), now));
        Assert.AreEqual(4, EntryGrouping.DateBucket(new DateTime(2026, 9, 27), now));
    }

    [TestMethod]
    public void CyclesGroupModes()
    {
        Assert.AreEqual(GroupBy.Type, EntryGrouping.Next(GroupBy.None));
        Assert.AreEqual(GroupBy.Modified, EntryGrouping.Next(GroupBy.Type));
        Assert.AreEqual(GroupBy.None, EntryGrouping.Next(GroupBy.Modified));
    }

    private static FolderListing Project(PolluxSettings settings, params FileEntry[] entries) =>
        FolderProjection.Project(entries, @"C:\x", settings, null, null, [], null, Now);

    [TestMethod]
    public void NoGroupingLeavesRowsAndHasNoGroups()
    {
        var l = Project(new PolluxSettings(), F("b.md"), F("a.md"));

        Assert.AreEqual(0, l.Groups!.Count);
        CollectionAssert.AreEqual(new[] { "a.md", "b.md" }, l.Rows.Select(r => r.Entry.Name).ToArray());
    }

    [TestMethod]
    public void GroupsByTypeFoldersFirst()
    {
        var settings = new PolluxSettings { Files = { GroupBy = GroupBy.Type } };

        var l = Project(settings, F("b.md"), F("a.cs"), D("src"), F("a.md"), F("readme"));

        CollectionAssert.AreEqual(new[] { "src", "a.cs", "a.md", "b.md", "readme" }, l.Rows.Select(r => r.Entry.Name).ToArray());
        CollectionAssert.AreEqual(new[] { "Folders", "CS", "MD", "Files" }, l.Groups!.Select(g => g.Title).ToArray());
        CollectionAssert.AreEqual(new[] { 0, 1, 2, 4 }, l.Groups!.Select(g => g.Start).ToArray());
        CollectionAssert.AreEqual(new[] { 1, 1, 2, 1 }, l.Groups!.Select(g => g.Count).ToArray());
    }

    [TestMethod]
    public void GroupsByModifiedDateKeepingSortWithinGroup()
    {
        var settings = new PolluxSettings { Files = { GroupBy = GroupBy.Modified } };

        var l = Project(settings,
            F("old", Now.AddYears(-1)),
            F("b", Now),
            F("a", Now),
            F("y", Now.AddDays(-1)));

        CollectionAssert.AreEqual(new[] { "a", "b", "y", "old" }, l.Rows.Select(r => r.Entry.Name).ToArray());
        CollectionAssert.AreEqual(new[] { "Today", "Yesterday", "Earlier" }, l.Groups!.Select(g => g.Title).ToArray());
    }

    [TestMethod]
    public void SelectionIndicesFollowGroupedOrder()
    {
        var settings = new PolluxSettings { Files = { GroupBy = GroupBy.Type } };

        var l = FolderProjection.Project([F("a.md"), D("src")], @"C:\x", settings, null, null, [], "a.md", Now);

        Assert.AreEqual(1, l.Selected.Single());
    }
}
