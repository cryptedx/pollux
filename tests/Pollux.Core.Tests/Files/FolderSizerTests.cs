using Pollux.Core.Files;

namespace Pollux.Core.Tests.Files;

[TestClass]
public sealed class FolderSizerTests
{
    private string _root = "";

    [TestInitialize]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "pollux-fs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void Cleanup() => Directory.Delete(_root, true);

    private void Write(string relative, int bytes)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
    }

    [TestMethod]
    public void SumsFilesRecursively()
    {
        Write("a.bin", 10);
        Write("sub/b.bin", 20);
        Write("sub/deep/c.bin", 30);

        Assert.AreEqual(60, FolderSizer.Measure(_root));
    }

    [TestMethod]
    public void EmptyFolderIsZero() => Assert.AreEqual(0, FolderSizer.Measure(_root));

    [TestMethod]
    public void DoesNotFollowLinkedFolders()
    {
        Write("real/a.bin", 10);
        Directory.CreateSymbolicLink(Path.Combine(_root, "link"), Path.Combine(_root, "real"));
        Directory.CreateSymbolicLink(Path.Combine(_root, "real", "loop"), _root);

        Assert.AreEqual(10, FolderSizer.Measure(_root));
    }

    [TestMethod]
    public void MissingFolderIsZero() => Assert.AreEqual(0, FolderSizer.Measure(Path.Combine(_root, "nope")));

    [TestMethod]
    public void CancelledMeasureThrows()
    {
        Write("a.bin", 1);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsExactly<OperationCanceledException>(() => FolderSizer.Measure(_root, cts.Token));
    }

    [TestMethod]
    public async Task ReportsEveryFolder()
    {
        Write("x/a.bin", 5);
        Write("y/b.bin", 7);
        var seen = new System.Collections.Concurrent.ConcurrentDictionary<string, long>();

        await FolderSizer.MeasureAllAsync([Path.Combine(_root, "x"), Path.Combine(_root, "y")], (f, s) => seen[Path.GetFileName(f)] = s);

        Assert.AreEqual(5, seen["x"]);
        Assert.AreEqual(7, seen["y"]);
    }

    [TestMethod]
    public async Task CancelledMeasureAllDoesNotThrow()
    {
        Write("x/a.bin", 5);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await FolderSizer.MeasureAllAsync([Path.Combine(_root, "x")], (_, _) => { }, cts.Token);
    }
}
