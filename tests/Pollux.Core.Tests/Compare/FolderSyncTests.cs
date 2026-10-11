using Pollux.Core.Compare;
using Pollux.Core.Files;
using Pollux.Core.Operations;

namespace Pollux.Core.Tests.Compare;

[TestClass]
public sealed class FolderSyncTests
{
    private static readonly DateTime Noon = new(2026, 1, 1, 12, 0, 0);
    private static readonly string Left = Path.Combine("c", "left");
    private static readonly string Right = Path.Combine("c", "right");

    private static CompareItem I(string root, string path, long size = 10, int minutes = 0, bool dir = false) =>
        new(path, new FileEntry(Path.GetFileName(path), Path.Combine(root, path), dir, dir ? 0 : size, Noon.AddMinutes(minutes), dir ? FileAttributes.Directory : FileAttributes.Normal));

    private static FolderComparison Compare(CompareItem[] left, CompareItem[] right, bool recursive = false) =>
        FolderComparer.Compare(Left, Right, left, right, new CompareOptions(Recursive: recursive));

    [TestMethod]
    public void CopiesWhatIsNewOrNewerAndNothingElse()
    {
        var c = Compare(
            [I(Left, "new.txt"), I(Left, "newer.txt", minutes: 5), I(Left, "older.txt"), I(Left, "same.txt")],
            [I(Right, "newer.txt"), I(Right, "older.txt", minutes: 5), I(Right, "same.txt"), I(Right, "extra.txt")]);

        var plan = FolderSync.Plan(c, CompareSide.Left);

        CollectionAssert.AreEquivalent(new[] { "new.txt", "newer.txt" }, plan.Items.Select(i => i.RelativePath).ToArray());
        Assert.AreEqual(1, plan.NewCount);
        Assert.AreEqual(1, plan.ReplaceCount);
        Assert.IsTrue(plan.Items.All(i => i.TargetDirectory == Right));
    }

    [TestMethod]
    public void SyncsFromEitherSide()
    {
        var c = Compare([I(Left, "a")], [I(Right, "b")]);

        var plan = FolderSync.Plan(c, CompareSide.Right);

        Assert.AreEqual(Path.Combine(Right, "b"), plan.Items.Single().Source);
        Assert.AreEqual(Left, plan.Items.Single().TargetDirectory);
        Assert.AreEqual(Right, plan.SourceFolder);
        Assert.AreEqual(Left, plan.TargetFolder);
    }

    [TestMethod]
    public void NothingToDoWhenNothingIsNewHere()
    {
        var plan = FolderSync.Plan(Compare([I(Left, "a")], [I(Right, "a"), I(Right, "b")]), CompareSide.Left);

        Assert.IsTrue(plan.IsEmpty);
        Assert.AreEqual(0, plan.ToRequests().Count);
    }

    [TestMethod]
    public void SkipsClashesOfFileAndFolder()
    {
        var plan = FolderSync.Plan(Compare([I(Left, "x")], [I(Right, "x", dir: true)]), CompareSide.Left);

        Assert.IsTrue(plan.IsEmpty);
        Assert.AreEqual(1, plan.Clashes);
        StringAssert.Contains(plan.ConfirmText, "1 name(s) are a file on one side and a folder");
    }

    [TestMethod]
    public void AFolderOnlyHereIsCopiedAsAWholeAndNotItsContentsAgain()
    {
        var inner = Path.Combine("docs", "a.md");
        var c = Compare([I(Left, "docs", dir: true), I(Left, inner, size: 100)], [], recursive: true);

        var plan = FolderSync.Plan(c, CompareSide.Left);

        var item = plan.Items.Single();
        Assert.AreEqual("docs", item.RelativePath);
        Assert.IsTrue(item.IsDirectory);
        Assert.AreEqual(100, item.Size);
        Assert.IsTrue(plan.SizeIsExact);
    }

    [TestMethod]
    public void AFolderWithoutScannedContentsHasAnUnknownSize()
    {
        var plan = FolderSync.Plan(Compare([I(Left, "docs", dir: true)], []), CompareSide.Left);

        Assert.IsFalse(plan.SizeIsExact);
        StringAssert.Contains(plan.ConfirmText, "at least");
    }

    [TestMethod]
    public void NestedItemsGoToTheMatchingSubfolderOfTheTarget()
    {
        var src = "src";
        var c = Compare(
            [I(Left, src, dir: true), I(Left, Path.Combine(src, "a.cs"), minutes: 5), I(Left, Path.Combine(src, "b.cs")), I(Left, "top.cs")],
            [I(Right, src, dir: true), I(Right, Path.Combine(src, "a.cs"))],
            recursive: true);

        var requests = FolderSync.Plan(c, CompareSide.Left).ToRequests();

        Assert.AreEqual(2, requests.Count);
        var nested = requests.Single(r => r.TargetDirectory == Path.Combine(Right, src));
        CollectionAssert.AreEquivalent(new[] { Path.Combine(Left, src, "a.cs"), Path.Combine(Left, src, "b.cs") }, nested.Sources.ToArray());
        var top = requests.Single(r => r.TargetDirectory == Right);
        CollectionAssert.AreEqual(new[] { Path.Combine(Left, "top.cs") }, top.Sources.ToArray());
    }

    [TestMethod]
    public void RequestsAreCopiesThatNeverReplaceNewerFiles()
    {
        var requests = FolderSync.Plan(Compare([I(Left, "a")], []), CompareSide.Left).ToRequests();

        Assert.AreEqual(TransferKind.Copy, requests.Single().Kind);
        Assert.AreEqual(ConflictPolicy.OverwriteIfNewer, requests.Single().Policy);
    }

    [TestMethod]
    public void ConfirmationListsTheChangesAndSaysNothingIsDeleted()
    {
        var plan = FolderSync.Plan(Compare([I(Left, "new.txt", size: 2048), I(Left, "newer.txt", minutes: 5)], [I(Right, "newer.txt")]), CompareSide.Left);

        var text = plan.ConfirmText;

        StringAssert.Contains(text, "Copy 1 new and 1 newer items (2.0 K)");
        StringAssert.Contains(text, Left);
        StringAssert.Contains(text, Right + "?");
        StringAssert.Contains(text, "Nothing is deleted.");
        StringAssert.Contains(text, "new    new.txt");
        StringAssert.Contains(text, "newer  newer.txt");
    }

    [TestMethod]
    public void ConfirmationShortensALongList()
    {
        var left = Enumerable.Range(0, 20).Select(n => I(Left, $"f{n:00}.txt")).ToArray();

        var text = FolderSync.Plan(Compare(left, []), CompareSide.Left).ConfirmText;

        StringAssert.Contains(text, "f11.txt");
        Assert.IsFalse(text.Contains("f12.txt", StringComparison.Ordinal));
        StringAssert.Contains(text, "… and 8 more");
    }
}
