using Pollux.Core.Navigation;
using Pollux.Core.Session;

namespace Pollux.Core.Tests.Session;

[TestClass]
public sealed class PaneTabsTests
{
    private static PaneTabs Pane(int active, params string[] locations) =>
        new(() => WinPath.Home, new PaneSession { Tabs = [.. locations.Select(l => new TabSession { Location = l })], ActiveTab = active });

    private static string[] Locations(PaneTabs p) => [.. p.Tabs.Select(t => t.Location)];

    [TestMethod]
    public void EmptySessionOpensOneNewTab()
    {
        var p = new PaneTabs(() => @"C:\new", new PaneSession { Tabs = [], ActiveTab = 3 });

        CollectionAssert.AreEqual(new[] { @"C:\new" }, Locations(p));
        Assert.AreEqual(0, p.ActiveIndex);
    }

    [TestMethod]
    public void SessionRoundTrip()
    {
        var p = Pane(1, @"C:\a", @"C:\b", @"C:\c");

        var q = Pane(0);
        q.Load(p.ToSession());

        CollectionAssert.AreEqual(Locations(p), Locations(q));
        Assert.AreEqual(1, q.ActiveIndex);
    }

    [TestMethod]
    public void LoadClampsTheActiveTab()
    {
        var p = Pane(9, @"C:\a", @"C:\b");

        Assert.AreEqual(1, p.ActiveIndex);
    }

    [TestMethod]
    public void DetachingTheLastTabReplacesItWithANewTab()
    {
        var p = new PaneTabs(() => @"C:\new", new PaneSession { Tabs = [new() { Location = @"C:\a" }] });

        Assert.AreEqual(@"C:\a", p.Detach(0));
        CollectionAssert.AreEqual(new[] { @"C:\new" }, Locations(p));
        Assert.AreEqual(0, p.ActiveIndex);
    }

    [TestMethod]
    public void DetachingATabBeforeTheActiveOneKeepsItActive()
    {
        var p = Pane(2, @"C:\a", @"C:\b", @"C:\c");

        p.Detach(0);

        Assert.AreEqual(@"C:\c", p.Active.Location);
    }

    [TestMethod]
    public void DetachingATabAfterTheActiveOneKeepsItActive()
    {
        var p = Pane(0, @"C:\a", @"C:\b", @"C:\c");

        p.Detach(2);

        Assert.AreEqual(@"C:\a", p.Active.Location);
    }

    [TestMethod]
    public void DetachingTheActiveTabActivatesTheNextOneOrThePreviousAtTheEnd()
    {
        var p = Pane(1, @"C:\a", @"C:\b", @"C:\c");
        p.Detach(1);
        Assert.AreEqual(@"C:\c", p.Active.Location);

        p.Detach(1);
        Assert.AreEqual(@"C:\a", p.Active.Location);
    }

    [TestMethod]
    public void ReorderKeepsTheShownTabActive()
    {
        var p = Pane(1, @"C:\a", @"C:\b", @"C:\c");
        var shown = p.Active;

        p.Reorder(0, 2);

        CollectionAssert.AreEqual(new[] { @"C:\b", @"C:\c", @"C:\a" }, Locations(p));
        Assert.AreSame(shown, p.Active);
        Assert.AreEqual(0, p.ActiveIndex);
    }

    [TestMethod]
    public void ReorderIgnoresAnIndexOutOfRange()
    {
        var p = Pane(0, @"C:\a", @"C:\b");

        p.Reorder(5, 0);

        CollectionAssert.AreEqual(new[] { @"C:\a", @"C:\b" }, Locations(p));
    }

    [TestMethod]
    public void InsertActivatesTheInsertedTabAtAClampedIndex()
    {
        var p = Pane(0, @"C:\a", @"C:\b");

        p.Insert(10, @"C:\x");

        CollectionAssert.AreEqual(new[] { @"C:\a", @"C:\b", @"C:\x" }, Locations(p));
        Assert.AreEqual(@"C:\x", p.Active.Location);
    }

    [TestMethod]
    public void NewOpensAfterTheActiveTab()
    {
        var p = Pane(0, @"C:\a", @"C:\b");

        p.New(@"C:\x", activate: false);
        Assert.AreEqual(@"C:\a", p.Active.Location);

        p.New();
        CollectionAssert.AreEqual(new[] { @"C:\a", WinPath.Home, @"C:\x", @"C:\b" }, Locations(p));
        Assert.AreEqual(1, p.ActiveIndex);
    }

    [TestMethod]
    public void ClosedTabsReopenNewestFirst()
    {
        var p = Pane(0, @"C:\a", @"C:\b", @"C:\c");

        p.Close(1);
        p.Close(1);
        p.Close(7); // out of range: ignored
        p.ReopenClosed();
        Assert.AreEqual(@"C:\c", p.Active.Location);
        p.ReopenClosed();
        Assert.AreEqual(@"C:\b", p.Active.Location);
        p.ReopenClosed(); // nothing left

        CollectionAssert.AreEqual(new[] { @"C:\a", @"C:\c", @"C:\b" }, Locations(p));
    }

    [TestMethod]
    public void ClosingTheLastTabCanBeReopened()
    {
        var p = Pane(0, @"C:\a");

        p.Close(0);
        p.ReopenClosed();

        CollectionAssert.AreEqual(new[] { WinPath.Home, @"C:\a" }, Locations(p));
        Assert.AreEqual(@"C:\a", p.Active.Location);
    }

    [TestMethod]
    public void NextWrapsAround()
    {
        var p = Pane(0, @"C:\a", @"C:\b", @"C:\c");

        p.Next(-1);
        Assert.AreEqual(2, p.ActiveIndex);
        p.Next(1);
        Assert.AreEqual(0, p.ActiveIndex);
        p.Next(4);
        Assert.AreEqual(1, p.ActiveIndex);
    }

    [TestMethod]
    public void MoveToAnotherPaneActivatesTheTabThere()
    {
        var left = Pane(1, @"C:\a", @"C:\b", @"C:\c");
        var right = Pane(0, @"D:\x");

        PaneTabs.Move(left, 0, right, 1);

        CollectionAssert.AreEqual(new[] { @"C:\b", @"C:\c" }, Locations(left));
        Assert.AreEqual(@"C:\b", left.Active.Location);
        CollectionAssert.AreEqual(new[] { @"D:\x", @"C:\a" }, Locations(right));
        Assert.AreEqual(@"C:\a", right.Active.Location);
    }

    [TestMethod]
    public void MovingTheOnlyTabLeavesANewTabBehind()
    {
        var left = Pane(0, @"C:\a");
        var right = Pane(0, @"D:\x");

        PaneTabs.Move(left, 0, right, 0);

        CollectionAssert.AreEqual(new[] { WinPath.Home }, Locations(left));
        CollectionAssert.AreEqual(new[] { @"C:\a", @"D:\x" }, Locations(right));
    }

    [TestMethod]
    public void MoveWithinAPaneReorders()
    {
        var p = Pane(0, @"C:\a", @"C:\b", @"C:\c");

        PaneTabs.Move(p, 0, p, 2);

        CollectionAssert.AreEqual(new[] { @"C:\b", @"C:\c", @"C:\a" }, Locations(p));
        Assert.AreEqual(@"C:\a", p.Active.Location);
    }
}
