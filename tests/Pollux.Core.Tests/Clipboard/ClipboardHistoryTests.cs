using Pollux.Core.Clipboard;

namespace Pollux.Core.Tests.Clipboard;

[TestClass]
public sealed class ClipboardHistoryTests
{
    private static readonly DateTime T0 = new(2026, 10, 1, 12, 0, 0);

    [TestMethod]
    public void NewestClipIsOnTop()
    {
        var history = new ClipboardHistory();

        history.Add(Clip.FromText("first", T0, "notepad", @"C:\Windows\notepad.exe"), T0);
        history.Add(Clip.FromText("second", T0.AddMinutes(1), "Code", null), T0.AddMinutes(1));

        CollectionAssert.AreEqual(new[] { "second", "first" }, history.Recent.Select(c => c.Text).ToArray());
        Assert.AreEqual("Code", history.Recent[0].SourceApp);
        Assert.AreEqual(@"C:\Windows\notepad.exe", history.Recent[1].SourcePath);
        Assert.AreEqual(T0, history.Recent[1].CapturedAt);
    }

    [TestMethod]
    public void CopyingIdenticalContentMovesThatClipToTheTop()
    {
        var history = new ClipboardHistory();
        var first = history.Add(Clip.FromText("hello", T0, "notepad", null), T0);
        history.Add(Clip.FromText("other", T0.AddMinutes(1), "notepad", null), T0.AddMinutes(1));

        var again = history.Add(Clip.FromText("hello", T0.AddMinutes(2), "Code", null), T0.AddMinutes(2));

        Assert.AreSame(first, again);
        CollectionAssert.AreEqual(new[] { "hello", "other" }, history.Recent.Select(c => c.Text).ToArray());
        Assert.AreEqual(T0.AddMinutes(2), again.CapturedAt);
        Assert.AreEqual("Code", again.SourceApp);
    }

    [TestMethod]
    public void DuplicatesAreDetectedForImagesAndFileLists()
    {
        var history = new ClipboardHistory();
        history.Add(Clip.FromImage([1, 2, 3], T0, "mspaint", null), T0);
        history.Add(Clip.FromFiles([@"C:\a.txt", @"C:\b.txt"], T0, "explorer", null), T0);
        history.Add(Clip.FromImage([1, 2, 4], T0, "mspaint", null), T0);

        history.Add(Clip.FromImage([1, 2, 3], T0.AddMinutes(1), "mspaint", null), T0.AddMinutes(1));
        history.Add(Clip.FromFiles([@"c:\A.txt", @"C:\b.txt"], T0.AddMinutes(2), "explorer", null), T0.AddMinutes(2));

        Assert.AreEqual(3, history.Recent.Count);
        Assert.AreEqual(ClipKind.Files, history.Recent[0].Kind);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, history.Recent[1].Image);
    }

    [TestMethod]
    public void TextThatLooksLikeAPathIsNotAFileListDuplicate()
    {
        var history = new ClipboardHistory();
        history.Add(Clip.FromFiles([@"C:\a.txt"], T0, "explorer", null), T0);
        history.Add(Clip.FromText(@"C:\a.txt", T0, "notepad", null), T0);

        Assert.AreEqual(2, history.Recent.Count);
    }

    [TestMethod]
    public void KeptClipsHaveTheirOwnSectionAndReleaseBackIntoTheHistory()
    {
        var history = new ClipboardHistory();
        var a = history.Add(Clip.FromText("a", T0, null, null), T0);
        var b = history.Add(Clip.FromText("b", T0.AddMinutes(1), null, null), T0.AddMinutes(1));
        history.Add(Clip.FromText("c", T0.AddMinutes(2), null, null), T0.AddMinutes(2));

        history.Keep(b.Id);
        history.Keep(a.Id, "Address");

        CollectionAssert.AreEqual(new[] { "b", "a" }, history.Kept.Select(c => c.Text).ToArray());
        CollectionAssert.AreEqual(new[] { "c" }, history.Recent.Select(c => c.Text).ToArray());
        Assert.IsTrue(a.Kept);
        Assert.AreEqual("Address", a.Title);
        CollectionAssert.AreEqual(new[] { "b", "a", "c" }, history.All.Select(c => c.Text).ToArray());

        history.Release(b.Id, T0.AddMinutes(3));

        Assert.IsFalse(b.Kept);
        CollectionAssert.AreEqual(new[] { "a" }, history.Kept.Select(c => c.Text).ToArray());
        CollectionAssert.AreEqual(new[] { "c", "b" }, history.Recent.Select(c => c.Text).ToArray());
    }

    [TestMethod]
    public void ReleasingAKeptClipAppliesTheRetention()
    {
        var history = new ClipboardHistory { Retention = new ClipRetention(MaxClips: 2, MaxAgeDays: 30) };
        var old = history.Add(Clip.FromText("old", T0, null, null), T0);
        history.Keep(old.Id);
        history.Add(Clip.FromText("a", T0.AddDays(40), null, null), T0.AddDays(40));
        history.Add(Clip.FromText("b", T0.AddDays(40).AddMinutes(1), null, null), T0.AddDays(40).AddMinutes(1));
        var newer = history.Add(Clip.FromText("kept", T0.AddDays(40).AddMinutes(2), null, null), T0.AddDays(40).AddMinutes(2));
        history.Keep(newer.Id);

        history.Release(old.Id, T0.AddDays(40).AddMinutes(3));
        history.Release(newer.Id, T0.AddDays(40).AddMinutes(3));

        Assert.AreEqual(0, history.Kept.Count);
        CollectionAssert.AreEqual(new[] { "kept", "b" }, history.Recent.Select(c => c.Text).ToArray());
    }

    [TestMethod]
    public void KeptClipCanBeRetitled()
    {
        var history = new ClipboardHistory();
        var a = history.Add(Clip.FromText("a", T0, null, null), T0);
        history.Keep(a.Id, "Old");

        history.SetTitle(a.Id, "  ");
        Assert.IsNull(a.Title);

        history.SetTitle(a.Id, " New ");
        Assert.AreEqual("New", a.Title);
    }

    [TestMethod]
    public void CopyingAKeptClipAgainKeepsItInPlace()
    {
        var history = new ClipboardHistory();
        var a = history.Add(Clip.FromText("a", T0, null, null), T0);
        var b = history.Add(Clip.FromText("b", T0, null, null), T0);
        history.Keep(a.Id);
        history.Keep(b.Id);

        var again = history.Add(Clip.FromText("b", T0.AddMinutes(5), "Code", null), T0.AddMinutes(5));

        Assert.AreSame(b, again);
        CollectionAssert.AreEqual(new[] { "a", "b" }, history.Kept.Select(c => c.Text).ToArray());
        Assert.AreEqual(0, history.Recent.Count);
        Assert.AreEqual(T0.AddMinutes(5), b.CapturedAt);
    }

    [TestMethod]
    public void KeptClipsCanBeReordered()
    {
        var history = new ClipboardHistory();
        var ids = new[] { "a", "b", "c" }.Select(t => history.Add(Clip.FromText(t, T0, null, null), T0).Id).ToArray();
        foreach (var id in ids)
        {
            history.Keep(id);
        }

        history.MoveKept(ids[2], 0);
        CollectionAssert.AreEqual(new[] { "c", "a", "b" }, history.Kept.Select(c => c.Text).ToArray());

        history.MoveKept(ids[2], 99);
        CollectionAssert.AreEqual(new[] { "a", "b", "c" }, history.Kept.Select(c => c.Text).ToArray());

        // Alt+Up on the first Kept Clip stays put.
        history.MoveKept(ids[0], -1);
        CollectionAssert.AreEqual(new[] { "a", "b", "c" }, history.Kept.Select(c => c.Text).ToArray());
    }

    [TestMethod]
    public void DeleteRemovesKeptAndRecentClips()
    {
        var history = new ClipboardHistory();
        var a = history.Add(Clip.FromText("a", T0, null, null), T0);
        var b = history.Add(Clip.FromText("b", T0, null, null), T0);
        history.Keep(a.Id);

        history.Delete(a.Id);
        history.Delete(b.Id);
        history.Delete("missing");

        Assert.AreEqual(0, history.All.Count());
    }

    [TestMethod]
    public void ClearHistoryKeepsKeptClips()
    {
        var history = new ClipboardHistory();
        var a = history.Add(Clip.FromText("a", T0, null, null), T0);
        history.Add(Clip.FromText("b", T0, null, null), T0);
        history.Add(Clip.FromText("c", T0, null, null), T0);
        history.Keep(a.Id);

        history.ClearHistory();

        CollectionAssert.AreEqual(new[] { "a" }, history.All.Select(c => c.Text).ToArray());
    }

    [TestMethod]
    public void AtMost500ClipsByDefaultAndKeptClipsDoNotCount()
    {
        var history = new ClipboardHistory();
        var kept = history.Add(Clip.FromText("kept", T0, null, null), T0);
        history.Keep(kept.Id);

        for (var i = 0; i < 501; i++)
        {
            history.Add(Clip.FromText($"clip {i}", T0, null, null), T0);
        }

        Assert.AreEqual(500, history.Recent.Count);
        Assert.AreEqual("clip 500", history.Recent[0].Text);
        Assert.AreEqual("clip 1", history.Recent[^1].Text);
        Assert.AreSame(kept, history.Kept.Single());
    }

    [TestMethod]
    public void ClipsOlderThan30DaysRotateOutButKeptClipsStay()
    {
        var history = new ClipboardHistory();
        var kept = history.Add(Clip.FromText("kept", T0, null, null), T0);
        history.Keep(kept.Id);
        history.Add(Clip.FromText("old", T0, null, null), T0);
        history.Add(Clip.FromText("recent", T0.AddDays(20), null, null), T0.AddDays(20));

        history.Add(Clip.FromText("new", T0.AddDays(31), null, null), T0.AddDays(31));

        CollectionAssert.AreEqual(new[] { "kept", "new", "recent" }, history.All.Select(c => c.Text).ToArray());
    }

    [TestMethod]
    public void RetentionIsConfigurableAndZeroMeansNoLimit()
    {
        var history = new ClipboardHistory { Retention = new ClipRetention(MaxClips: 2, MaxAgeDays: 0) };
        history.Add(Clip.FromText("a", T0, null, null), T0);
        history.Add(Clip.FromText("b", T0, null, null), T0);
        history.Add(Clip.FromText("c", T0.AddYears(1), null, null), T0.AddYears(1));

        CollectionAssert.AreEqual(new[] { "c", "b" }, history.Recent.Select(c => c.Text).ToArray());

        history.Retention = new ClipRetention(MaxClips: 1, MaxAgeDays: 30);
        history.Prune(T0.AddYears(1));

        CollectionAssert.AreEqual(new[] { "c" }, history.Recent.Select(c => c.Text).ToArray());
    }

    [TestMethod]
    public void FindLooksUpAClipById()
    {
        var history = new ClipboardHistory();
        var a = history.Add(Clip.FromText("a", T0, null, null), T0);
        history.Keep(a.Id);

        Assert.AreSame(a, history.Find(a.Id));
        Assert.IsNull(history.Find("missing"));
    }

    [TestMethod]
    public void PastingAClipMovesItToTheTopWithoutANewClip()
    {
        var history = new ClipboardHistory();
        var pasted = history.Add(Clip.FromText("pasted", T0, "notepad", null), T0);
        history.Add(Clip.FromText("newer", T0.AddMinutes(1), "Code", null), T0.AddMinutes(1));
        var kept = history.Add(Clip.FromText("kept", T0, "Code", null), T0);
        history.Keep(kept.Id);

        history.Pasted(pasted.Id, T0.AddMinutes(5));
        history.Pasted(kept.Id, T0.AddMinutes(6));

        CollectionAssert.AreEqual(new[] { "pasted", "newer" }, history.Recent.Select(c => c.Text).ToArray());
        Assert.AreEqual(T0.AddMinutes(5), pasted.CapturedAt);
        Assert.AreEqual("notepad", pasted.SourceApp);
        CollectionAssert.AreEqual(new[] { "kept" }, history.Kept.Select(c => c.Text).ToArray());
    }

    [TestMethod]
    public void MergingTheStoredHistoryKeepsClipsFromBothSides()
    {
        var stored = new ClipboardHistory();
        var keptOnDisk = stored.Add(Clip.FromText("kept on disk", T0, null, null), T0);
        stored.Keep(keptOnDisk.Id, "title");
        var recopied = stored.Add(Clip.FromText("kept, copied again", T0, null, null), T0);
        stored.Keep(recopied.Id);
        stored.Add(Clip.FromText("old", T0.AddMinutes(1), null, null), T0.AddMinutes(1));
        stored.Add(Clip.FromText("both", T0.AddMinutes(2), null, null), T0.AddMinutes(2));

        // This session's history, started empty because the stored one could not be read.
        var session = new ClipboardHistory();
        session.Add(Clip.FromText("kept, copied again", T0.AddMinutes(3), "Code", null), T0.AddMinutes(3));
        session.Add(Clip.FromText("both", T0.AddMinutes(4), "Code", null), T0.AddMinutes(4));
        session.Add(Clip.FromText("new", T0.AddMinutes(5), null, null), T0.AddMinutes(5));

        session.MergeStored(stored, T0.AddMinutes(5));

        CollectionAssert.AreEqual(new[] { "kept on disk", "kept, copied again" }, session.Kept.Select(c => c.Text).ToArray());
        Assert.IsTrue(session.Kept.All(c => c.Kept));
        Assert.AreEqual("title", session.Kept[0].Title);
        Assert.AreEqual("Code", session.Kept[1].SourceApp, "the newer copy's details win");
        CollectionAssert.AreEqual(new[] { "new", "both", "old" }, session.Recent.Select(c => c.Text).ToArray());
    }

    [TestMethod]
    public void MergingTheStoredHistoryAppliesTheRetention()
    {
        var stored = new ClipboardHistory();
        stored.Add(Clip.FromText("old", T0, null, null), T0);
        var session = new ClipboardHistory { Retention = new ClipRetention(MaxClips: 1) };
        session.Add(Clip.FromText("new", T0.AddMinutes(1), null, null), T0.AddMinutes(1));

        session.MergeStored(stored, T0.AddMinutes(1));

        CollectionAssert.AreEqual(new[] { "new" }, session.Recent.Select(c => c.Text).ToArray());
    }
}
