using Pollux.Core.Files;
using Pollux.Core.Settings;

namespace Pollux.Core.Tests.Files;

[TestClass]
public sealed class ColumnSettingsTests
{
    private const string Folder = @"C:\media";

    private static FileEntry F(string name) => new(name, Folder + "\\" + name, false, 0, new DateTime(2026, 1, 1), FileAttributes.Normal);

    [TestMethod]
    public void EverySortColumnHasItsColumn()
    {
        foreach (var sort in Enum.GetValues<SortColumn>())
        {
            Assert.AreEqual(sort, KnownColumns.For(sort).Sort);
        }

        Assert.AreEqual(KnownColumns.Duration, KnownColumns.For(SortColumn.Duration));
    }

    [TestMethod]
    public void ProjectionSortsByAPropertyColumn()
    {
        var store = new PropertyValues(10);
        var long1 = F("a-long.mp4");
        var short1 = F("b-short.mp4");
        store.Set(long1, [new(ShellProperty.Duration, PropertyValue.Duration(600_000_000))]);
        store.Set(short1, [new(ShellProperty.Duration, PropertyValue.Duration(10_000_000))]);
        var settings = new PolluxSettings();
        settings.Files.SortBy = SortColumn.Duration;

        var listing = FolderProjection.Project([F("c-unread.txt"), long1, short1], Folder, settings, null, null, [], null, properties: store);
        CollectionAssert.AreEqual(new[] { "b-short.mp4", "a-long.mp4", "c-unread.txt" }, listing.Rows.Select(r => r.Entry.Name).ToArray());

        var unsorted = FolderProjection.Project([F("c-unread.txt"), long1, short1], Folder, settings, null, null, [], null);
        CollectionAssert.AreEqual(new[] { "a-long.mp4", "b-short.mp4", "c-unread.txt" }, unsorted.Rows.Select(r => r.Entry.Name).ToArray());
    }

    [TestMethod]
    public void SettingsKeepOrderWidthsAndPropertySort()
    {
        var json = """
            { "files": { "sortBy": "dimensions", "columns": ["duration"], "columnOrder": ["name", "duration", "size"], "columnWidths": { "name": 320, "duration": 70.5 } } }
            """;
        var s = SettingsStore.Parse(json).Settings;

        Assert.AreEqual(SortColumn.Dimensions, s.Files.SortBy);
        var layout = ColumnLayout.Parse(s.Files.Columns, s.Files.ColumnOrder, s.Files.ColumnWidths);
        CollectionAssert.AreEqual(new[] { "name", "duration", "size" }, layout.Visible.Select(v => v.Column.Id).ToArray());
        Assert.AreEqual(70.5, layout.WidthOf(KnownColumns.Duration));

        var again = SettingsStore.Parse(SettingsStore.Serialize(s)).Settings;
        Assert.IsTrue(layout.SameAs(ColumnLayout.Parse(again.Files.Columns, again.Files.ColumnOrder, again.Files.ColumnWidths)));
        StringAssert.Contains(SettingsStore.Serialize(s), "\"columnWidths\"");
    }

    [TestMethod]
    public void NullColumnSettingsBecomeEmpty()
    {
        var s = SettingsStore.Parse("""{ "files": { "columns": null, "columnOrder": null, "columnWidths": null } }""").Settings;

        Assert.AreEqual(0, s.Files.Columns.Count);
        Assert.AreEqual(0, s.Files.ColumnOrder.Count);
        Assert.AreEqual(0, s.Files.ColumnWidths.Count);
    }

    [TestMethod]
    public void WithoutColumnsIgnoresOnlyTheColumnLayout()
    {
        var a = new PolluxSettings();
        var b = new PolluxSettings();
        b.Files.Columns = ["dimensions"];
        b.Files.ColumnOrder = ["name", "dimensions"];
        b.Files.ColumnWidths = new() { ["name"] = 200 };

        Assert.AreEqual(SettingsStore.WithoutColumns(a), SettingsStore.WithoutColumns(b));
        b.Files.SortBy = SortColumn.Dimensions;
        Assert.AreNotEqual(SettingsStore.WithoutColumns(a), SettingsStore.WithoutColumns(b));
    }

    [TestMethod]
    public void SameAsComparesColumnsOrderAndWidths()
    {
        var a = ColumnLayout.Parse(["title"], null, null);

        Assert.IsTrue(a.SameAs(ColumnLayout.Parse(["title"], [], new Dictionary<string, double>())));
        Assert.IsFalse(a.SameAs(a.Resize("title", 300)));
        Assert.IsFalse(a.SameAs(a.Move("title", 1)));
        Assert.IsFalse(a.SameAs(a.Toggle("company")));
    }

    [TestMethod]
    public void WidthLinesRoundTrip()
    {
        var parsed = ColumnLayout.ParseWidths(["name=320", " Size: 90 ", "dateTaken 140.5", "bogus=10", "type=wide", "", "modified=1"]);

        Assert.AreEqual(4, parsed.Count);
        Assert.AreEqual(320, parsed["name"]);
        Assert.AreEqual(90, parsed["size"]);
        Assert.AreEqual(140.5, parsed["dateTaken"]);
        Assert.AreEqual(KnownColumns.Modified.MinWidth, parsed["modified"]);
        CollectionAssert.AreEqual(
            new[] { "name=320", "modified=60", "size=90", "dateTaken=140.5" },
            ColumnLayout.FormatWidths(parsed));
        CollectionAssert.AreEqual(new[] { "name=320" }, ColumnLayout.FormatWidths(new Dictionary<string, double> { ["NAME"] = 320, ["x"] = 3 }));
    }
}
