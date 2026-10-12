using Pollux.Core.Navigation;

namespace Pollux.Core.Tests.Navigation;

[TestClass]
public sealed class FileSystemDirectoryListerTests
{
    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pollux-lister-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [TestMethod]
    public void ListsFilesAndFolders()
    {
        var dir = NewTempDir();
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "sub"));
            File.WriteAllText(Path.Combine(dir, "a.txt"), "x");
            var items = new FileSystemDirectoryLister().List(dir, CancellationToken.None);
            Assert.AreEqual(2, items.Count);
            Assert.IsTrue(items.Single(i => i.Name == "sub").IsDirectory);
            Assert.IsFalse(items.Single(i => i.Name == "a.txt").IsDirectory);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [TestMethod]
    public void MissingFolderGivesEmptyList()
    {
        var missing = Path.Combine(Path.GetTempPath(), "pollux-missing-" + Guid.NewGuid().ToString("N"));
        Assert.AreEqual(0, new FileSystemDirectoryLister().List(missing, CancellationToken.None).Count);
    }

    [TestMethod]
    public void EntryCapIsHonoured()
    {
        var dir = NewTempDir();
        try
        {
            for (var i = 0; i < 5; i++)
            {
                File.WriteAllText(Path.Combine(dir, $"f{i}"), "x");
            }

            Assert.AreEqual(3, new FileSystemDirectoryLister(null, maxEntries: 3).List(dir, CancellationToken.None).Count);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [TestMethod]
    public void SlowFolderTimesOutWithEmptyList()
    {
        using var release = new ManualResetEventSlim();
        var lister = new FileSystemDirectoryLister(TimeSpan.FromMilliseconds(50), (_, _) => Slow(release));
        try
        {
            Assert.AreEqual(0, lister.List(@"\\dead\share", CancellationToken.None).Count);
        }
        finally
        {
            release.Set();
        }
    }

    [TestMethod]
    public void ThrowingEnumerationGivesEmptyList()
    {
        var lister = new FileSystemDirectoryLister(null, (_, _) => throw new UnauthorizedAccessException());
        Assert.AreEqual(0, lister.List("x", CancellationToken.None).Count);
    }

    [TestMethod]
    public void CancelledTokenThrows()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(() => new FileSystemDirectoryLister().List("x", cts.Token));
    }

    [TestMethod]
    public void CancellingWhileWaitingThrows()
    {
        using var release = new ManualResetEventSlim();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        var lister = new FileSystemDirectoryLister(TimeSpan.FromSeconds(30), (_, _) => Slow(release));
        try
        {
            Assert.ThrowsExactly<OperationCanceledException>(() => lister.List("x", cts.Token));
        }
        finally
        {
            release.Set();
        }
    }

    private static IEnumerable<DirectoryItem> Slow(ManualResetEventSlim release)
    {
        release.Wait(TimeSpan.FromSeconds(10));
        yield return new DirectoryItem("late", true, false);
    }
}
