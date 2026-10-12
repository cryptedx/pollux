using Pollux.Core.Files;
using Pollux.Core.Settings;

namespace Pollux.Core.Tests.Files;

[TestClass]
public sealed class ColumnLayoutTests
{
    private static string[] Ids(ColumnLayout layout) => [.. layout.Visible.Select(s => s.Column.Id)];

    [TestMethod]
    public void DefaultsWithoutSettings()
    {
        var layout = ColumnLayout.Parse(null, null, null);

        CollectionAssert.AreEqual(new[] { "name", "modified", "type", "size" }, Ids(layout));
        Assert.IsTrue(layout.Visible[0].Fill);
        Assert.AreEqual(OptionalColumns.None, layout.Optional);
    }

    [TestMethod]
    public void OldColumnsSettingAppendsInCatalogOrder()
    {
        var layout = ColumnLayout.Parse(["Extension", "bogus", " created ", "duration"], [], null);

        CollectionAssert.AreEqual(new[] { "name", "modified", "type", "size", "created", "extension", "duration" }, Ids(layout));
        Assert.AreEqual(OptionalColumns.Created | OptionalColumns.Extension, layout.Optional);
        CollectionAssert.AreEqual(new[] { ShellProperty.Duration }, layout.Properties.ToArray());
    }

    [TestMethod]
    public void OrderDecidesAndKeepsNameAndColumnsAddedByOlderVersions()
    {
        var layout = ColumnLayout.Parse(["attributes"], ["size", "SIZE", "bogus", "dimensions", "modified"], null);

        CollectionAssert.AreEqual(new[] { "name", "size", "dimensions", "modified", "attributes" }, Ids(layout));
    }

    [TestMethod]
    public void WidthsAreClampedAndUnknownOnesIgnored()
    {
        var layout = ColumnLayout.Parse(null, null, new Dictionary<string, double>
        {
            ["modified"] = 5,
            ["size"] = 99999,
            ["type"] = double.NaN,
            ["nope"] = 100,
            ["title"] = 222,
        });

        Assert.AreEqual(KnownColumns.Modified.MinWidth, layout.WidthOf(KnownColumns.Modified));
        Assert.AreEqual(KnownColumns.MaxWidth, layout.WidthOf(KnownColumns.Size));
        Assert.AreEqual(KnownColumns.Type.DefaultWidth, layout.WidthOf(KnownColumns.Type));
        Assert.AreEqual(222, layout.WidthOf(KnownColumns.Title));
        CollectionAssert.AreEquivalent(new[] { "modified", "size", "title" }, layout.ToSettings().Widths.Keys.ToArray());
    }

    [TestMethod]
    public void DefaultOrderIsStoredAsEmptyOrder()
    {
        var settings = ColumnLayout.Parse(["created"], null, null).ToSettings();

        CollectionAssert.AreEqual(new[] { "created" }, settings.Columns);
        Assert.AreEqual(0, settings.Order.Count);
        Assert.AreEqual(0, settings.Widths.Count);
    }

    [TestMethod]
    public void RoundTripsThroughSettings()
    {
        var layout = ColumnLayout.Default
            .Toggle("dateTaken")
            .Toggle("extension")
            .Move("size", 1)
            .Resize("name", 300)
            .Resize("dateTaken", 140);

        var s = layout.ToSettings();
        CollectionAssert.AreEqual(new[] { "extension", "dateTaken" }, s.Columns);
        CollectionAssert.AreEqual(new[] { "name", "size", "modified", "type", "dateTaken", "extension" }, s.Order);

        var back = ColumnLayout.Parse(s.Columns, s.Order, s.Widths);
        CollectionAssert.AreEqual(Ids(layout), Ids(back));
        Assert.AreEqual(300, back.WidthOf(KnownColumns.Name));
        Assert.IsFalse(back.Visible[0].Fill);
        Assert.AreEqual(140, back.WidthOf(KnownColumns.DateTaken));
    }

    [TestMethod]
    public void ToggleNeverHidesName()
    {
        var layout = ColumnLayout.Default.Toggle("name").Toggle("modified").Toggle("bogus");

        CollectionAssert.AreEqual(new[] { "name", "type", "size" }, Ids(layout));
    }

    [TestMethod]
    public void MoveClampsTheIndex()
    {
        CollectionAssert.AreEqual(new[] { "modified", "type", "size", "name" }, Ids(ColumnLayout.Default.Move("name", 99)));
        CollectionAssert.AreEqual(new[] { "size", "name", "modified", "type" }, Ids(ColumnLayout.Default.Move("size", -3)));
        Assert.AreSame(ColumnLayout.Default, ColumnLayout.Default.Move("title", 0));
    }

    [TestMethod]
    public void ResetWidthMakesNameFillAgain()
    {
        var layout = ColumnLayout.Default.Resize("name", 400).Resize("size", 90).ResetWidth("name");

        Assert.IsTrue(layout.Visible[0].Fill);
        Assert.AreEqual(90, layout.WidthOf(KnownColumns.Size));
    }

    [TestMethod]
    public void ArrangeGivesNameTheFreeSpace()
    {
        var placed = ColumnLayout.Default.Arrange(800, 8);

        // Fixed: 128 + 70 + 74 + 3 * 8 = 296.
        Assert.AreEqual(504, placed[0].Width);
        Assert.AreEqual(512, placed[1].X);
        Assert.AreEqual(512 + 128 + 8, placed[2].X);
        Assert.AreEqual(800, placed[3].X + placed[3].Width);
    }

    [TestMethod]
    public void ArrangeKeepsNameAtItsMinimumWhenNarrow()
    {
        var placed = ColumnLayout.Default.Arrange(200, 8);

        Assert.AreEqual(KnownColumns.Name.MinWidth, placed[0].Width);
    }

    [TestMethod]
    public void ArrangeWithFixedNameLeavesSpaceRight()
    {
        var placed = ColumnLayout.Default.Resize("name", 200).Arrange(800, 8);

        Assert.AreEqual(200, placed[0].Width);
        Assert.AreEqual(208, placed[1].X);
    }

    [TestMethod]
    public void ArrangeUnboundedUsesDefaultNameWidth()
    {
        var placed = ColumnLayout.Default.Arrange(double.PositiveInfinity, 8);

        Assert.AreEqual(KnownColumns.Name.DefaultWidth, placed[0].Width);
    }

    [TestMethod]
    public void DropIndexFollowsColumnMidpoints()
    {
        var placed = ColumnLayout.Default.Resize("name", 200).Arrange(800, 0);
        // name 0-200, modified 200-328, type 328-398, size 398-472.

        Assert.AreEqual(0, ColumnLayout.DropIndex(placed, "size", 50));
        Assert.AreEqual(1, ColumnLayout.DropIndex(placed, "size", 150));
        Assert.AreEqual(3, ColumnLayout.DropIndex(placed, "size", 700));
        Assert.AreEqual(0, ColumnLayout.DropIndex(placed, "name", 250));
        Assert.AreEqual(1, ColumnLayout.DropIndex(placed, "name", 300));
    }

    [TestMethod]
    public void GripAtRightEdges()
    {
        var placed = ColumnLayout.Default.Resize("name", 200).Arrange(800, 8);

        Assert.AreEqual(KnownColumns.Name, ColumnLayout.GripAt(placed, 203, 4));
        Assert.IsNull(ColumnLayout.GripAt(placed, 100, 4));
    }

    [TestMethod]
    public void FindsColumnsCaseInsensitively()
    {
        Assert.AreEqual(KnownColumns.FileVersion, KnownColumns.Find(" FILEVERSION "));
        Assert.IsNull(KnownColumns.Find(""));
        Assert.AreEqual(KnownColumns.Created, KnownColumns.For(SortColumn.Created));
        Assert.AreEqual(KnownColumns.Company, KnownColumns.For(ShellProperty.Company));
        Assert.AreEqual(KnownColumns.All.Count, KnownColumns.All.Select(c => c.Id.ToUpperInvariant()).Distinct().Count());
    }

    [TestMethod]
    public void PackRoundTripsOrderAndWidths()
    {
        var layout = ColumnLayout.Default.Toggle("duration").Move("size", 1).Resize("name", 320).Resize("duration", 77);

        var packed = layout.Pack();
        var back = ColumnLayout.Unpack(packed);

        Assert.AreEqual("name,size,modified,type,duration|name=320,duration=77", packed);
        Assert.IsNotNull(back);
        CollectionAssert.AreEqual(Ids(layout), Ids(back));
        Assert.AreEqual(320, back.WidthOf(KnownColumns.Name));
        Assert.IsFalse(back.Visible[0].Fill);
    }

    [TestMethod]
    public void UnpackDefaultLayoutKeepsNameFilling()
    {
        var back = ColumnLayout.Unpack(ColumnLayout.Default.Pack());

        Assert.IsNotNull(back);
        Assert.IsTrue(back.SameAs(ColumnLayout.Default));
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("|name=300")]
    [DataRow("bogus,other")]
    public void UnpackWithoutKnownColumnsIsNull(string? text) => Assert.IsNull(ColumnLayout.Unpack(text));

    [TestMethod]
    public void UnpackIgnoresUnknownIdsAndBadWidths()
    {
        var back = ColumnLayout.Unpack("size,bogus,name|size=abc,name=250,nope=3");

        Assert.IsNotNull(back);
        CollectionAssert.AreEqual(new[] { "size", "name" }, Ids(back));
        Assert.AreEqual(250, back.WidthOf(KnownColumns.Name));
        Assert.AreEqual(KnownColumns.Size.DefaultWidth, back.WidthOf(KnownColumns.Size));
    }
}
