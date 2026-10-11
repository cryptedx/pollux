using Pollux.Core.Clipboard;

namespace Pollux.Core.Tests.Clipboard;

[TestClass]
public sealed class ClipStackRowsTests
{
    private static readonly DateTime T0 = new(2026, 10, 1, 12, 0, 0);

    /// <summary>Clips "clip 0".. in capture order; the first <paramref name="kept"/> are Kept Clips.</summary>
    private static ClipboardHistory History(int recent, int kept = 0)
    {
        var history = new ClipboardHistory { Retention = new ClipRetention(0, 0) };
        for (var i = 0; i < kept + recent; i++)
        {
            var clip = history.Add(Clip.FromText("clip " + i, T0.AddMinutes(i), null, null), T0.AddMinutes(i));
            if (i < kept) history.Keep(clip.Id);
        }

        return history;
    }

    private static string Describe(ClipStackRows rows) => string.Join("|", rows.Rows.Select(r => r.Header ?? r.Clip!.Text));

    [TestMethod]
    public void WithoutKeptClipsThereAreNoHeaders()
    {
        var rows = ClipStackRows.Build(History(2), "", 10);

        Assert.AreEqual("clip 1|clip 0", Describe(rows));
        Assert.AreEqual(2, rows.HitCount);
    }

    [TestMethod]
    public void KeptClipsComeFirstUnderTheirOwnHeader()
    {
        var rows = ClipStackRows.Build(History(recent: 2, kept: 1), "", 10);

        Assert.AreEqual("Kept Clips|clip 0|Clipboard History|clip 2|clip 1", Describe(rows));
    }

    [TestMethod]
    public void OnlyKeptClipsHaveNoHistoryHeader()
    {
        var rows = ClipStackRows.Build(History(recent: 0, kept: 2), "", 10);

        Assert.AreEqual("Kept Clips|clip 0|clip 1", Describe(rows));
    }

    [TestMethod]
    public void MaxRowsLimitsOnlyTheHistorySection()
    {
        var rows = ClipStackRows.Build(History(recent: 5, kept: 2), "", 2);

        Assert.AreEqual("Kept Clips|clip 0|clip 1|Clipboard History|clip 6|clip 5", Describe(rows));
        Assert.AreEqual(7, rows.HitCount);
    }

    [TestMethod]
    public void TheQueryFiltersBothSectionsAndDropsEmptyOnes()
    {
        var history = History(recent: 3, kept: 1);

        Assert.AreEqual("clip 2", Describe(ClipStackRows.Build(history, "clip 2", 10)));
        Assert.AreEqual("Kept Clips|clip 0", Describe(ClipStackRows.Build(history, "clip 0", 10)));
        Assert.AreEqual("", Describe(ClipStackRows.Build(history, "nothing like it", 10)));
    }

    [TestMethod]
    public void IndexToSelectPrefersTheIdElseTheFirstClip()
    {
        var history = History(recent: 2, kept: 1);
        var rows = ClipStackRows.Build(history, "", 10); // header, clip 0, header, clip 2, clip 1

        Assert.AreEqual(1, rows.IndexToSelect(null));
        Assert.AreEqual(1, rows.IndexToSelect("missing"));
        Assert.AreEqual(4, rows.IndexToSelect(history.Recent[1].Id));
        Assert.AreEqual(-1, ClipStackRows.Build(History(0), "", 10).IndexToSelect(null));
    }

    [TestMethod]
    public void MoveSkipsHeadersAndWraps()
    {
        var rows = ClipStackRows.Build(History(recent: 2, kept: 1), "", 10); // header, clip 0, header, clip 2, clip 1

        Assert.AreEqual(3, rows.Move(1, +1));
        Assert.AreEqual(1, rows.Move(3, -1));
        Assert.AreEqual(1, rows.Move(4, +1));
        Assert.AreEqual(4, rows.Move(1, -1));
        Assert.AreEqual(-1, ClipStackRows.Build(History(0), "", 10).Move(0, 1));
    }

    [TestMethod]
    public void NeighbourIsTheNextClipElseThePreviousOne()
    {
        var history = History(recent: 2, kept: 1);
        var rows = ClipStackRows.Build(history, "", 10);
        var kept = history.Kept[0].Id;
        var (first, last) = (history.Recent[0].Id, history.Recent[1].Id);

        Assert.AreEqual(first, rows.NeighbourId(kept));
        Assert.AreEqual(last, rows.NeighbourId(first));
        Assert.AreEqual(first, rows.NeighbourId(last));

        var single = History(1);
        Assert.IsNull(ClipStackRows.Build(single, "", 10).NeighbourId(single.Recent[0].Id));
    }

    [TestMethod]
    public void IndexOfKeptFollowsTheKeptOrder()
    {
        var history = History(recent: 1, kept: 2);

        Assert.AreEqual(1, ClipStackRows.IndexOfKept(history, history.Kept[1].Id));
        Assert.AreEqual(-1, ClipStackRows.IndexOfKept(history, history.Recent[0].Id));
    }
}
