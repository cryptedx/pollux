using Pollux.Core.Compare;
using Pollux.Core.Files;

namespace Pollux.Core.Tests.Compare;

[TestClass]
public sealed class FolderComparerTests
{
    private static readonly DateTime Noon = new(2026, 1, 1, 12, 0, 0);

    private static FileEntry F(string name, long size = 10, int minutes = 0) =>
        new(name, Path.Combine("root", name), false, size, Noon.AddMinutes(minutes), FileAttributes.Normal);

    private static FileEntry D(string name) =>
        new(name, Path.Combine("root", name), true, 0, Noon, FileAttributes.Directory);

    private static FolderComparison Run(FileEntry[] left, FileEntry[] right, CompareOptions? options = null, Func<FileEntry, FileEntry, bool>? sameContent = null) =>
        FolderComparer.Compare("L", "R", CompareItem.Flat(left), CompareItem.Flat(right), options ?? new CompareOptions(), sameContent);

    private static CompareStatus? StatusOf(FolderComparison c, string name) => c.Find(name)?.Status;

    [TestMethod]
    public void NamesInOnlyOneFolderAreOnlyThere()
    {
        var c = Run([F("a"), F("b")], [F("b"), F("c")]);

        Assert.AreEqual(CompareStatus.OnlyLeft, StatusOf(c, "a"));
        Assert.AreEqual(CompareStatus.OnlyRight, StatusOf(c, "c"));
        Assert.AreEqual(CompareStatus.Same, StatusOf(c, "b"));
    }

    [TestMethod]
    public void NamesMatchIgnoringCase()
    {
        var c = Run([F("Readme.MD")], [F("readme.md")]);

        Assert.AreEqual(1, c.Entries.Count);
        Assert.AreEqual(CompareStatus.Same, c.Entries[0].Status);
    }

    [TestMethod]
    public void LaterModifiedTimeIsNewer()
    {
        var c = Run([F("a", minutes: 5), F("b", minutes: 0)], [F("a", minutes: 0), F("b", minutes: 5)]);

        Assert.AreEqual(CompareStatus.LeftNewer, StatusOf(c, "a"));
        Assert.AreEqual(CompareStatus.RightNewer, StatusOf(c, "b"));
    }

    [TestMethod]
    public void ADifferentSizeWithALaterTimeIsNewer()
    {
        var c = Run([F("a", size: 99, minutes: 5)], [F("a", size: 10)]);

        Assert.AreEqual(CompareStatus.LeftNewer, StatusOf(c, "a"));
    }

    [TestMethod]
    public void TimesWithinTheToleranceAreEqual()
    {
        var left = new FileEntry("a", "a", false, 10, Noon.AddSeconds(2), FileAttributes.Normal);
        var right = new FileEntry("a", "a", false, 10, Noon, FileAttributes.Normal);

        Assert.AreEqual(CompareStatus.Same, StatusOf(Run([left], [right]), "a"));
        Assert.AreEqual(CompareStatus.LeftNewer, StatusOf(Run([left], [right], new CompareOptions(TimeTolerance: TimeSpan.FromMilliseconds(500))), "a"));
    }

    [TestMethod]
    public void TheSameTimeWithADifferentSizeDiffers()
    {
        var c = Run([F("a", size: 11)], [F("a", size: 10)]);

        Assert.AreEqual(CompareStatus.Different, StatusOf(c, "a"));
    }

    [TestMethod]
    public void AFileAgainstAFolderDiffers()
    {
        var c = Run([F("x")], [D("x")]);

        Assert.AreEqual(CompareStatus.Different, StatusOf(c, "x"));
        Assert.IsTrue(c.Entries[0].IsTypeMismatch);
    }

    [TestMethod]
    public void FoldersInBothAreTheSameWithoutLookingInside()
    {
        Assert.AreEqual(CompareStatus.Same, StatusOf(Run([D("d")], [D("d")]), "d"));
    }

    [TestMethod]
    public void ContentCompareOnlyAsksForFilesOfTheSameSize()
    {
        var asked = new List<string>();
        bool Content(FileEntry l, FileEntry r) { asked.Add(l.Name); return false; }
        var options = new CompareOptions(CompareContent: true);

        var c = Run([F("same", size: 10), F("grown", size: 20)], [F("same", size: 10), F("grown", size: 10)], options, Content);

        CollectionAssert.AreEqual(new[] { "same" }, asked);
        Assert.AreEqual(CompareStatus.Different, StatusOf(c, "same"));
        Assert.AreEqual(CompareStatus.Different, StatusOf(c, "grown"));
    }

    [TestMethod]
    public void EqualContentIsTheSameDespiteDifferentTimes()
    {
        var c = Run([F("a", minutes: 30)], [F("a", minutes: 0)], new CompareOptions(CompareContent: true), (_, _) => true);

        Assert.AreEqual(CompareStatus.Same, StatusOf(c, "a"));
    }

    [TestMethod]
    public void DifferentContentWithDifferentTimesStillSaysWhichIsNewer()
    {
        var c = Run([F("a", minutes: 30)], [F("a", minutes: 0)], new CompareOptions(CompareContent: true), (_, _) => false);

        Assert.AreEqual(CompareStatus.LeftNewer, StatusOf(c, "a"));
    }

    [TestMethod]
    public void CloudOnlyFilesAreNeverReadForTheContentCompare()
    {
        var placeholder = F("a", minutes: 0) with { Attributes = FileAttributes.Offline };
        Func<FileEntry, FileEntry, bool> content = (_, _) => throw new AssertFailedException("read a cloud-only file");
        var options = new CompareOptions(CompareContent: true);

        Assert.AreEqual(CompareStatus.Same, StatusOf(Run([placeholder], [F("a", minutes: 0)], options, content), "a"), "same size and time");
        Assert.AreEqual(CompareStatus.Same, StatusOf(Run([F("a", minutes: 0)], [placeholder], options, content), "a"), "either side");
        Assert.AreEqual(CompareStatus.LeftNewer, StatusOf(Run([F("a", minutes: 30)], [placeholder], options, content), "a"));
    }

    [TestMethod]
    public void ContentIsNotReadUnlessAskedFor()
    {
        var c = Run([F("a", minutes: 30)], [F("a", minutes: 0)], sameContent: (_, _) => throw new AssertFailedException("read content"));

        Assert.AreEqual(CompareStatus.LeftNewer, StatusOf(c, "a"));
    }

    [TestMethod]
    public void MarksAreSeenFromThePanesSide()
    {
        var c = Run([F("only"), F("new", minutes: 5), F("old", minutes: 0), F("same")], [F("new", minutes: 0), F("old", minutes: 5), F("same"), F("away")]);

        Assert.AreEqual(CompareMark.OnlyHere, c.MarkFor(CompareSide.Left, "only"));
        Assert.AreEqual(CompareMark.None, c.MarkFor(CompareSide.Right, "only"));
        Assert.AreEqual(CompareMark.Newer, c.MarkFor(CompareSide.Left, "new"));
        Assert.AreEqual(CompareMark.Older, c.MarkFor(CompareSide.Right, "new"));
        Assert.AreEqual(CompareMark.Older, c.MarkFor(CompareSide.Left, "old"));
        Assert.AreEqual(CompareMark.Same, c.MarkFor(CompareSide.Right, "same"));
        Assert.AreEqual(CompareMark.OnlyHere, c.MarkFor(CompareSide.Right, "away"));
        Assert.AreEqual(CompareMark.None, c.MarkFor(CompareSide.Left, "unknown"));
    }

    [TestMethod]
    public void SummaryCountsEachStatus()
    {
        var c = Run(
            [F("a"), F("b"), F("n", minutes: 5), F("s"), F("x", size: 1)],
            [F("c"), F("n", minutes: 0), F("s"), F("x", size: 2)]);

        Assert.AreEqual(new CompareSummary(OnlyLeft: 2, OnlyRight: 1, LeftNewer: 1, RightNewer: 0, Same: 1, Different: 1), c.Summary);
        Assert.AreEqual("2 only left · 1 only right · 1 left newer · 1 differ · 1 same", c.Summary.Text);
    }

    [TestMethod]
    public void SummaryTextSaysWhenFoldersAreIdentical()
    {
        Assert.AreEqual("identical (2 items)", Run([F("a"), F("b")], [F("a"), F("b")]).Summary.Text);
        Assert.AreEqual("both folders are empty", Run([], []).Summary.Text);
    }

    [TestMethod]
    public void SelectingDifferencesTakesWhatCopyingToTheOtherSideWouldBringOver()
    {
        var c = Run(
            [F("only"), F("new", minutes: 5), F("old", minutes: 0), F("same"), F("odd", size: 1), F("clash")],
            [F("new", minutes: 0), F("old", minutes: 5), F("same"), F("odd", size: 2), D("clash"), F("away")]);

        CollectionAssert.AreEquivalent(new[] { "only", "new", "odd" }, c.SelectableNames(CompareSide.Left).ToArray());
        CollectionAssert.AreEquivalent(new[] { "old", "away", "odd" }, c.SelectableNames(CompareSide.Right).ToArray());
    }

    // ---------------- recursive ----------------

    private static CompareItem I(string path, long size = 10, int minutes = 0, bool dir = false) =>
        new(path, new FileEntry(Path.GetFileName(path), Path.Combine("root", path), dir, dir ? 0 : size, Noon.AddMinutes(minutes), dir ? FileAttributes.Directory : FileAttributes.Normal));

    private static FolderComparison RunDeep(CompareItem[] left, CompareItem[] right) =>
        FolderComparer.Compare("L", "R", left, right, new CompareOptions(Recursive: true));

    [TestMethod]
    public void AFolderIsDifferentWhenAnythingBelowIt()
    {
        var sub = Path.Combine("src", "deep");
        var c = RunDeep(
            [I("src", dir: true), I(sub, dir: true), I(Path.Combine(sub, "a.cs"), minutes: 5), I(Path.Combine("src", "b.cs"))],
            [I("src", dir: true), I(sub, dir: true), I(Path.Combine(sub, "a.cs")), I(Path.Combine("src", "b.cs"))]);

        Assert.AreEqual(CompareStatus.Different, StatusOf(c, "src"));
        Assert.AreEqual(CompareStatus.Different, StatusOf(c, sub));
        Assert.AreEqual(CompareStatus.LeftNewer, StatusOf(c, Path.Combine(sub, "a.cs")));
        Assert.AreEqual(CompareStatus.Same, StatusOf(c, Path.Combine("src", "b.cs")));
        Assert.AreEqual(CompareMark.Different, c.MarkFor(CompareSide.Left, "src"));
    }

    [TestMethod]
    public void AFolderWithNothingDifferentBelowItIsTheSame()
    {
        var c = RunDeep([I("src", dir: true), I(Path.Combine("src", "a"))], [I("src", dir: true), I(Path.Combine("src", "a"))]);

        Assert.AreEqual(CompareStatus.Same, StatusOf(c, "src"));
    }

    [TestMethod]
    public void RecursiveSummaryCountsFilesNotFolders()
    {
        var c = RunDeep(
            [I("src", dir: true), I(Path.Combine("src", "a")), I(Path.Combine("src", "b"))],
            [I("src", dir: true), I(Path.Combine("src", "a"))]);

        Assert.AreEqual(new CompareSummary(OnlyLeft: 1, OnlyRight: 0, LeftNewer: 0, RightNewer: 0, Same: 1, Different: 0), c.Summary);
    }

    [TestMethod]
    public void SelectingDifferencesTakesOnlyEntriesOfTheShownFolder()
    {
        var c = RunDeep(
            [I("src", dir: true), I(Path.Combine("src", "a"), minutes: 5), I("top")],
            [I("src", dir: true), I(Path.Combine("src", "a")), I("top")]);

        CollectionAssert.AreEqual(new[] { "src" }, c.SelectableNames(CompareSide.Left).ToArray());
    }
}
