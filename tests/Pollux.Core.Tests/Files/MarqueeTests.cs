using Pollux.Core.Files;

namespace Pollux.Core.Tests.Files;

[TestClass]
public sealed class MarqueeTests
{
    private static readonly StackItemLayout Rows = new(0, 0, 300, 26);

    private static int[] Hits(ItemLayout layout, LayoutBox box, int count) => layout.Hits(box, count).ToArray();

    private static HashSet<int> Set(params int[] items) => [.. items];

    [TestMethod]
    public void SpanNormalizesCorners()
    {
        Assert.AreEqual(new LayoutBox(1, 2, 5, 9), LayoutBox.Span(5, 9, 1, 2));
    }

    [TestMethod]
    public void ZeroWidthMarqueeHitsTheSlotItCrosses()
    {
        var slot = new LayoutBox(0, 0, 10, 10);

        Assert.IsTrue(new LayoutBox(5, 2, 5, 3).Hits(slot));
        Assert.IsTrue(new LayoutBox(0, 0, 0, 0).Hits(slot));
        Assert.IsFalse(new LayoutBox(10, 0, 12, 5).Hits(slot));
    }

    [TestMethod]
    public void StackHitsTheRowsTheMarqueeSpans()
    {
        CollectionAssert.AreEqual(new[] { 1, 2, 3 }, Hits(Rows, LayoutBox.Span(10, 30, 50, 100), 10));
    }

    [TestMethod]
    public void StackEdgeOnARowBoundaryHitsOnlyTheRowBelow()
    {
        CollectionAssert.AreEqual(new[] { 1 }, Hits(Rows, LayoutBox.Span(10, 26, 50, 26), 10));
    }

    [TestMethod]
    public void StackClampsToTheItemCount()
    {
        CollectionAssert.AreEqual(new[] { 8, 9 }, Hits(Rows, LayoutBox.Span(0, 210, 50, 5000), 10));
        CollectionAssert.AreEqual(new[] { 0, 1 }, Hits(Rows, LayoutBox.Span(0, -500, 50, 30), 10));
    }

    [TestMethod]
    public void StackMissesBelowTheLastRowAndBesideTheRows()
    {
        Assert.AreEqual(0, Hits(Rows, LayoutBox.Span(0, 270, 50, 400), 10).Length);
        Assert.AreEqual(0, Hits(Rows, LayoutBox.Span(310, 0, 400, 100), 10).Length);
        Assert.AreEqual(0, Hits(Rows, LayoutBox.Span(0, -50, 50, -10), 10).Length);
    }

    [TestMethod]
    public void StackMatchesItsSlots()
    {
        var layout = new StackItemLayout(4, 10, 296, 26, 30, [0, 3, 7]);
        var box = LayoutBox.Span(20, 60, 40, 250);
        var bySlot = Enumerable.Range(0, 12).Where(i => box.Hits(layout.Slot(i))).ToArray();

        CollectionAssert.AreEqual(bySlot, Hits(layout, box, 12));
    }

    [TestMethod]
    public void GroupedStackSkipsHeaders()
    {
        // Header 0..30, rows 0-2 at 30..108, header 108..138, rows 3-4 at 138..190.
        var layout = new StackItemLayout(0, 0, 300, 26, 30, [0, 3]);

        Assert.AreEqual(new LayoutBox(0, 138, 300, 164), layout.Slot(3));
        CollectionAssert.AreEqual(new[] { 2, 3 }, Hits(layout, LayoutBox.Span(0, 100, 10, 140), 5));
        Assert.AreEqual(0, Hits(layout, LayoutBox.Span(0, 110, 10, 130), 5).Length);
        Assert.AreEqual(0, Hits(layout, LayoutBox.Span(0, 0, 10, 20), 5).Length);
    }

    [TestMethod]
    public void WrapHitsTheCellsInTheMarquee()
    {
        var layout = new WrapItemLayout(0, 0, 100, 50, 4);

        // Columns 1-2 of rows 0-1.
        CollectionAssert.AreEqual(new[] { 1, 2, 5, 6 }, Hits(layout, LayoutBox.Span(150, 10, 250, 60), 20));
    }

    [TestMethod]
    public void WrapStopsAtTheLastItemOfAPartialRow()
    {
        var layout = new WrapItemLayout(0, 0, 100, 50, 4);

        CollectionAssert.AreEqual(new[] { 4, 5 }, Hits(layout, LayoutBox.Span(0, 60, 1000, 1000), 6));
    }

    [TestMethod]
    public void WrapMissesRightOfTheColumnsAndAboveTheGrid()
    {
        var layout = new WrapItemLayout(4, 4, 100, 50, 3);

        Assert.AreEqual(0, Hits(layout, LayoutBox.Span(310, 0, 400, 100), 9).Length);
        Assert.AreEqual(0, Hits(layout, LayoutBox.Span(0, -40, 400, 0), 9).Length);
        CollectionAssert.AreEqual(new[] { 2, 5 }, Hits(layout, LayoutBox.Span(250, 0, 400, 60), 9));
    }

    [TestMethod]
    public void WrapMatchesItsSlots()
    {
        var layout = new WrapItemLayout(4, 4, 124, 148, 5);
        var box = LayoutBox.Span(130, 100, 380, 500);
        var bySlot = Enumerable.Range(0, 23).Where(i => box.Hits(layout.Slot(i))).ToArray();

        CollectionAssert.AreEqual(bySlot, Hits(layout, box, 23));
    }

    [TestMethod]
    public void ModifiersPickTheMode()
    {
        Assert.AreEqual(MarqueeMode.Replace, Marquee.ModeFor(control: false, shift: false));
        Assert.AreEqual(MarqueeMode.Add, Marquee.ModeFor(control: false, shift: true));
        Assert.AreEqual(MarqueeMode.Toggle, Marquee.ModeFor(control: true, shift: false));
        Assert.AreEqual(MarqueeMode.Toggle, Marquee.ModeFor(control: true, shift: true));
    }

    [TestMethod]
    public void ReplaceSelectsExactlyTheHits()
    {
        var (select, deselect) = Marquee.Changes(MarqueeMode.Replace, Set(7), Set(1, 2), Set(2, 3));

        CollectionAssert.AreEqual(new[] { 3 }, select);
        CollectionAssert.AreEqual(new[] { 1 }, deselect);
    }

    [TestMethod]
    public void AddKeepsTheInitialSelectionWhenTheMarqueeLeavesIt()
    {
        var (select, deselect) = Marquee.Changes(MarqueeMode.Add, Set(1), Set(1, 2), Set(3));

        CollectionAssert.AreEqual(new[] { 3 }, select);
        CollectionAssert.AreEqual(new[] { 2 }, deselect);
    }

    [TestMethod]
    public void ToggleFlipsRelativeToTheInitialSelection()
    {
        var (select, deselect) = Marquee.Changes(MarqueeMode.Toggle, Set(1, 2), Set(), Set(2, 3));

        CollectionAssert.AreEqual(new[] { 3 }, select);
        CollectionAssert.AreEqual(new[] { 2 }, deselect);

        (select, deselect) = Marquee.Changes(MarqueeMode.Toggle, Set(1, 2), Set(2, 3), Set());
        CollectionAssert.AreEqual(new[] { 2 }, select);
        CollectionAssert.AreEqual(new[] { 3 }, deselect);
    }

    [TestMethod]
    public void UnchangedHitsChangeNothing()
    {
        var (select, deselect) = Marquee.Changes(MarqueeMode.Toggle, Set(1), Set(1, 2), Set(1, 2));

        Assert.AreEqual(0, select.Count);
        Assert.AreEqual(0, deselect.Count);
    }

    [TestMethod]
    public void RunsGroupConsecutiveIndexes()
    {
        CollectionAssert.AreEqual(new[] { (1, 3), (7, 1), (9, 2) }, Marquee.Runs([1, 2, 3, 7, 9, 10]));
        Assert.AreEqual(0, Marquee.Runs([]).Count);
    }

    [TestMethod]
    public void AutoScrollOnlyNearOrBeyondTheEdges()
    {
        Assert.AreEqual(0, Marquee.AutoScrollStep(200, 0, 400));
        Assert.IsTrue(Marquee.AutoScrollStep(10, 0, 400) < 0);
        Assert.IsTrue(Marquee.AutoScrollStep(395, 0, 400) > 0);
    }

    [TestMethod]
    public void AutoScrollSpeedsUpWithDistanceAndIsCapped()
    {
        Assert.IsTrue(Marquee.AutoScrollStep(-50, 0, 400) < Marquee.AutoScrollStep(10, 0, 400));
        Assert.AreEqual(48, Marquee.AutoScrollStep(5000, 0, 400));
        Assert.AreEqual(-48, Marquee.AutoScrollStep(-5000, 0, 400));
    }

    [TestMethod]
    public void AutoScrollShrinksTheEdgeInASmallViewport()
    {
        Assert.AreEqual(0, Marquee.AutoScrollStep(20, 0, 40));
        Assert.IsTrue(Marquee.AutoScrollStep(5, 0, 40) < 0);
    }
}
