using Pollux.Core.Compare;

namespace Pollux.Core.Tests.Compare;

[TestClass]
public sealed class FolderComparerDiskTests
{
    private string _root = null!;
    private string _left = null!;
    private string _right = null!;

    [TestInitialize]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "pollux-compare-" + Guid.NewGuid().ToString("N"));
        _left = Directory.CreateDirectory(Path.Combine(_root, "left")).FullName;
        _right = Directory.CreateDirectory(Path.Combine(_root, "right")).FullName;
    }

    [TestCleanup]
    public void Cleanup() => Directory.Delete(_root, recursive: true);

    private static string Write(string folder, string relative, string text, DateTime? modified = null)
    {
        var path = Path.Combine(folder, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        File.SetLastWriteTime(path, modified ?? new DateTime(2026, 1, 1, 12, 0, 0));
        return path;
    }

    [TestMethod]
    public void ComparesTheFilesOfTwoFolders()
    {
        Write(_left, "only.txt", "x");
        Write(_left, "both.txt", "same");
        Write(_right, "both.txt", "same");
        Write(_right, "away.txt", "x");
        Write(_left, "newer.txt", "x", new DateTime(2026, 1, 2));
        Write(_right, "newer.txt", "x");

        var c = FolderComparer.Compare(_left, _right, new CompareOptions());

        Assert.AreEqual(CompareStatus.OnlyLeft, c.Find("only.txt")!.Status);
        Assert.AreEqual(CompareStatus.OnlyRight, c.Find("away.txt")!.Status);
        Assert.AreEqual(CompareStatus.Same, c.Find("both.txt")!.Status);
        Assert.AreEqual(CompareStatus.LeftNewer, c.Find("newer.txt")!.Status);
    }

    [TestMethod]
    public void SubfoldersAreOnlyLookedIntoWhenRecursive()
    {
        Write(_left, Path.Combine("sub", "deep.txt"), "x");
        Directory.CreateDirectory(Path.Combine(_right, "sub"));

        var flat = FolderComparer.Compare(_left, _right, new CompareOptions());
        var deep = FolderComparer.Compare(_left, _right, new CompareOptions(Recursive: true));

        Assert.IsNull(flat.Find(Path.Combine("sub", "deep.txt")));
        Assert.AreEqual(CompareStatus.Same, flat.Find("sub")!.Status);
        Assert.AreEqual(CompareStatus.OnlyLeft, deep.Find(Path.Combine("sub", "deep.txt"))!.Status);
        Assert.AreEqual(CompareStatus.Different, deep.Find("sub")!.Status);
    }

    [TestMethod]
    public void SameSizeFilesAreComparedByContentWhenAsked()
    {
        Write(_left, "a.txt", "abcd");
        Write(_right, "a.txt", "abce");
        Write(_left, "b.txt", "same", new DateTime(2026, 1, 5));
        Write(_right, "b.txt", "same");

        var byTime = FolderComparer.Compare(_left, _right, new CompareOptions());
        var byContent = FolderComparer.Compare(_left, _right, new CompareOptions(CompareContent: true));

        Assert.AreEqual(CompareStatus.Same, byTime.Find("a.txt")!.Status);
        Assert.AreEqual(CompareStatus.LeftNewer, byTime.Find("b.txt")!.Status);
        Assert.AreEqual(CompareStatus.Different, byContent.Find("a.txt")!.Status);
        Assert.AreEqual(CompareStatus.Same, byContent.Find("b.txt")!.Status);
    }

    [TestMethod]
    public void IgnoresHiddenEntriesUnlessIncluded()
    {
        var hidden = Write(_left, ".secret", "x");
        File.SetAttributes(hidden, FileAttributes.Hidden);

        Assert.IsNull(FolderComparer.Compare(_left, _right, new CompareOptions()).Find(".secret"));
        Assert.AreEqual(CompareStatus.OnlyLeft, FolderComparer.Compare(_left, _right, new CompareOptions(IncludeHidden: true)).Find(".secret")!.Status);
    }

    [TestMethod]
    public void IgnoresFilesATransferIsStillWriting()
    {
        Write(_left, "big.iso.pollux-part", "x");

        Assert.AreEqual(0, FolderComparer.Compare(_left, _right, new CompareOptions()).Entries.Count);
    }

    [TestMethod]
    public void AFolderThatDoesNotExistCountsAsEmpty()
    {
        Write(_left, "a", "x");

        var c = FolderComparer.Compare(_left, Path.Combine(_root, "missing"), new CompareOptions());

        Assert.AreEqual(CompareStatus.OnlyLeft, c.Find("a")!.Status);
    }

    [TestMethod]
    public void StopsWhenCancelled()
    {
        Write(_left, "a", "x");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsExactly<OperationCanceledException>(() => FolderComparer.Compare(_left, _right, new CompareOptions(), cts.Token));
    }

    [TestMethod]
    public void ReadsFilesThatAreOpenElsewhere()
    {
        var path = Write(_left, "a", "data");
        Write(_right, "a", "data");
        using var open = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

        Assert.IsTrue(FileContent.AreEqual(path, Path.Combine(_right, "a")));
    }

    [TestMethod]
    public void FilesLongerThanTheBufferAreComparedToTheEnd()
    {
        var big = new string('x', 200_000);
        var a = Write(_left, "a", big);
        var b = Write(_right, "a", big[..^1] + "y");

        Assert.IsFalse(FileContent.AreEqual(a, b));
        Assert.IsTrue(FileContent.AreEqual(a, a));
    }
}
