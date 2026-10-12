using Pollux.Core.Files;
using Pollux.Core.Settings;

namespace Pollux.Core.Tests.Files;

[TestClass]
public sealed class PropertyValuesTests
{
    private static readonly DateTime Now = new(2026, 10, 15, 12, 0, 0);

    private static FileEntry F(string name, DateTime? modified = null) => new(name, @"C:\x\" + name, false, 0, modified ?? Now, FileAttributes.Normal);

    private static FileEntry D(string name) => new(name, @"C:\x\" + name, true, 0, Now, FileAttributes.Directory);

    [TestMethod]
    [DataRow(0.0, "0:00")]
    [DataRow(7.9, "0:07")]
    [DataRow(205.0, "3:25")]
    [DataRow(3723.0, "1:02:03")]
    [DataRow(90000.0, "25:00:00")]
    public void FormatsDurations(double seconds, string expected) =>
        Assert.AreEqual(expected, PropertyFormat.Duration(TimeSpan.FromSeconds(seconds)));

    [TestMethod]
    public void BuildsValues()
    {
        Assert.AreEqual(new PropertyValue("1920 × 1080", 1920L * 1080), PropertyValue.Dimensions(1920, 1080));
        Assert.AreEqual("3:25", PropertyValue.Duration(2_050_000_000UL).Text);
        Assert.AreEqual("2026-01-02 03:04", PropertyValue.Date(new DateTime(2026, 1, 2, 3, 4, 5), "").Text);
        Assert.AreEqual("Ann; Bob", PropertyValue.Texts(["Ann", " ", null, " Bob "])!.Value.Text);
        Assert.IsNull(PropertyValue.Texts([" "]));
        Assert.IsNull(PropertyValue.FromText("  "));
        Assert.AreEqual("x", PropertyValue.FromText(" x ")!.Value.Text);
    }

    [TestMethod]
    public void ComparesByNumberThenNaturally()
    {
        Assert.IsTrue(PropertyValue.Compare(PropertyValue.Dimensions(100, 100), PropertyValue.Dimensions(20, 1000)) < 0);
        Assert.IsTrue(PropertyValue.Compare(new PropertyValue("9.1"), new PropertyValue("10.0.2")) < 0);
    }

    [TestMethod]
    public void StoresPerFileVersion()
    {
        var store = new PropertyValues(10);
        var a = F("a.jpg");
        store.Set(a, [new(ShellProperty.Dimensions, PropertyValue.Dimensions(2, 2)), new(ShellProperty.Title, null)]);

        Assert.IsTrue(store.TryGet(a, ShellProperty.Title, out var title));
        Assert.IsNull(title);
        Assert.AreEqual("2 × 2", store.Get(a, ShellProperty.Dimensions)!.Value.Text);
        CollectionAssert.AreEqual(new[] { ShellProperty.Duration }, store.Missing(a, [ShellProperty.Title, ShellProperty.Duration, ShellProperty.Duration]));

        var changed = a with { Modified = Now.AddMinutes(1) };
        Assert.IsNull(store.Get(changed, ShellProperty.Dimensions));
        store.Set(changed, [new(ShellProperty.Duration, PropertyValue.Duration(10_000_000))]);
        Assert.IsFalse(store.TryGet(changed, ShellProperty.Dimensions, out _));
        Assert.AreEqual(1, store.Count);
    }

    [TestMethod]
    public void DropsLeastRecentlyStored()
    {
        var store = new PropertyValues(2);
        store.Set(F("a"), [new(ShellProperty.Title, PropertyValue.FromText("a"))]);
        store.Set(F("b"), [new(ShellProperty.Title, PropertyValue.FromText("b"))]);
        store.Set(F("a"), [new(ShellProperty.Company, null)]);
        store.Set(F("c"), [new(ShellProperty.Title, PropertyValue.FromText("c"))]);

        Assert.AreEqual(2, store.Count);
        Assert.IsNotNull(store.Get(F("a"), ShellProperty.Title));
        Assert.IsFalse(store.TryGet(F("b"), ShellProperty.Title, out _));
        store.Clear();
        Assert.AreEqual(0, store.Count);
    }

    private static string[] SortBy(ListColumn column, bool descending, PropertyValues store, bool foldersFirst, params FileEntry[] entries) =>
        [.. entries.Order(new EntryComparer(column, descending, foldersFirst, store)).Select(e => e.Name)];

    [TestMethod]
    public void SortsByPropertyWithMissingValuesLast()
    {
        var store = new PropertyValues(100);
        var big = F("big.jpg");
        var small = F("small.jpg");
        var none = F("none.txt");
        var unread = F("unread.png");
        store.Set(big, [new(ShellProperty.Dimensions, PropertyValue.Dimensions(4000, 3000))]);
        store.Set(small, [new(ShellProperty.Dimensions, PropertyValue.Dimensions(10, 10))]);
        store.Set(none, [new(ShellProperty.Dimensions, null)]);

        CollectionAssert.AreEqual(new[] { "small.jpg", "big.jpg", "none.txt", "unread.png" }, SortBy(KnownColumns.Dimensions, false, store, false, unread, none, big, small));
        CollectionAssert.AreEqual(new[] { "big.jpg", "small.jpg", "unread.png", "none.txt" }, SortBy(KnownColumns.Dimensions, true, store, false, unread, none, big, small));
    }

    [TestMethod]
    public void PropertySortKeepsFoldersFirst()
    {
        var store = new PropertyValues(100);
        var song = F("song.mp3");
        store.Set(song, [new(ShellProperty.Duration, PropertyValue.Duration(1))]);

        CollectionAssert.AreEqual(new[] { "dir", "song.mp3", "z.txt" }, SortBy(KnownColumns.Duration, false, store, true, F("z.txt"), song, D("dir")));
    }

    [TestMethod]
    public void BuiltInColumnsSortAsBefore()
    {
        var a = F("a", Now.AddDays(-1));
        var b = F("b");
        var store = new PropertyValues(1);

        CollectionAssert.AreEqual(new[] { "b", "a" }, SortBy(KnownColumns.Modified, true, store, false, a, b));
        CollectionAssert.AreEqual(new[] { "a", "b" }, SortBy(KnownColumns.Name, false, store, false, b, a));
        CollectionAssert.AreEqual(
            SortBy(KnownColumns.For(SortColumn.Size), false, store, false, b, a),
            EntriesBy(new EntryComparer(SortColumn.Size, false, false), b, a));
    }

    private static string[] EntriesBy(EntryComparer comparer, params FileEntry[] entries) => [.. entries.Order(comparer).Select(e => e.Name)];
}
