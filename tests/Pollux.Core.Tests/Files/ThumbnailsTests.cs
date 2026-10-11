using Pollux.Core.Files;

namespace Pollux.Core.Tests.Files;

[TestClass]
public sealed class ThumbnailsTests
{
    private static FileEntry F(string path, long size = 1, FileAttributes attributes = FileAttributes.Normal) =>
        new(Path.GetFileName(path), path, false, size, new DateTime(2026, 1, 1), attributes);

    private static ThumbnailKey K(string path, int pixels = 96) => ThumbnailKey.For(F(path), pixels);

    // ---------------- key ----------------

    [TestMethod]
    public void KeyChangesWhenTheFileChanges()
    {
        var a = F(@"C:\x\a.jpg", size: 10);

        Assert.AreEqual(ThumbnailKey.For(a, 96), ThumbnailKey.For(a with { }, 96));
        Assert.AreNotEqual(ThumbnailKey.For(a, 96), ThumbnailKey.For(a with { Size = 11 }, 96));
        Assert.AreNotEqual(ThumbnailKey.For(a, 96), ThumbnailKey.For(a with { Modified = a.Modified.AddSeconds(1) }, 96));
        Assert.AreNotEqual(ThumbnailKey.For(a, 96), ThumbnailKey.For(a, 48));
    }

    [TestMethod]
    public void CloudOnlyFilesAndNetworkPathsGetTheirIconOnly()
    {
        Assert.IsFalse(ThumbnailKey.For(F(@"C:\x\a.jpg"), 96).IconOnly);
        Assert.IsTrue(ThumbnailKey.For(F(@"C:\x\a.jpg", attributes: FileAttributes.Offline), 96).IconOnly);
        Assert.IsTrue(ThumbnailKey.For(F(@"C:\x\a.jpg", attributes: (FileAttributes)0x00400000), 96).IconOnly);
        Assert.IsTrue(ThumbnailKey.For(F(@"\\server\share\a.jpg"), 96).IconOnly);
        Assert.IsTrue(ThumbnailKey.For(F(@"Z:\a.jpg"), 96, onNetworkDrive: true).IconOnly, "a mapped network drive");
    }

    // ---------------- cache ----------------

    [TestMethod]
    public void CacheEvictsTheLeastRecentlyUsed()
    {
        var cache = new ThumbnailCache<string>(2);
        cache.Add(K("a"), "A");
        cache.Add(K("b"), "B");
        Assert.IsTrue(cache.TryGet(K("a"), out _)); // a is now the most recent
        cache.Add(K("c"), "C");

        Assert.IsTrue(cache.TryGet(K("a"), out var a));
        Assert.AreEqual("A", a);
        Assert.IsFalse(cache.TryGet(K("b"), out _));
        Assert.IsTrue(cache.TryGet(K("c"), out _));
        Assert.AreEqual(2, cache.Count);
    }

    [TestMethod]
    public void CacheReplacesAnExistingEntry()
    {
        var cache = new ThumbnailCache<string>(2);
        cache.Add(K("a"), "A");
        cache.Add(K("a"), "A2");

        Assert.IsTrue(cache.TryGet(K("a"), out var a));
        Assert.AreEqual("A2", a);
        Assert.AreEqual(1, cache.Count);
    }

    [TestMethod]
    public void CacheRemembersFailuresAsNull()
    {
        var cache = new ThumbnailCache<string>(2);
        cache.Add(K("a"), null);

        Assert.IsTrue(cache.TryGet(K("a"), out var a));
        Assert.IsNull(a);
    }

    // ---------------- requests ----------------

    [TestMethod]
    public void QueueHandsOutTheNewestRequestFirst()
    {
        var q = new ThumbnailRequests<string>(10);
        q.Push("a");
        q.Push("b");
        q.Push("c");

        Assert.IsTrue(q.TryTake(out var first));
        Assert.AreEqual("c", first);
        Assert.IsTrue(q.TryTake(out var second));
        Assert.AreEqual("b", second);
    }

    [TestMethod]
    public void PushingAgainMovesARequestToTheFront()
    {
        var q = new ThumbnailRequests<string>(10);
        q.Push("a");
        q.Push("b");
        q.Push("a");

        Assert.AreEqual(2, q.Count);
        Assert.IsTrue(q.TryTake(out var first));
        Assert.AreEqual("a", first);
    }

    [TestMethod]
    public void AFullQueueDropsTheOldestRequest()
    {
        var q = new ThumbnailRequests<string>(2);
        Assert.IsNull(q.Push("a"));
        Assert.IsNull(q.Push("b"));

        Assert.AreEqual("a", q.Push("c"));
        Assert.AreEqual(2, q.Count);
        Assert.IsTrue(q.TryTake(out var first));
        Assert.AreEqual("c", first);
        Assert.IsTrue(q.TryTake(out var second));
        Assert.AreEqual("b", second);
        Assert.IsFalse(q.TryTake(out _));
    }

    [TestMethod]
    public void TakeWaitsForAPushAndStopsOnCancel()
    {
        var q = new ThumbnailRequests<string>(4);
        var taken = Task.Run(() => q.Take(CancellationToken.None));
        Thread.Sleep(50);
        Assert.IsFalse(taken.IsCompleted);
        q.Push("a");
        Assert.IsTrue(taken.Wait(TimeSpan.FromSeconds(5)));
        Assert.AreEqual("a", taken.Result);

        using var cts = new CancellationTokenSource();
        var cancelled = Task.Run(() => q.Take(cts.Token));
        cts.Cancel();
        Assert.IsTrue(cancelled.Wait(TimeSpan.FromSeconds(5)));
        Assert.IsNull(cancelled.Result);
    }

    // ---------------- pixels ----------------

    [TestMethod]
    public void PixelsWithoutAnyAlphaBecomeOpaque()
    {
        byte[] bgra = [1, 2, 3, 0, 4, 5, 6, 0];

        ThumbnailPixels.MakeOpaqueIfNoAlpha(bgra);

        CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 255, 4, 5, 6, 255 }, bgra);
    }

    [TestMethod]
    public void PixelsWithAlphaStayAsTheyAre()
    {
        byte[] bgra = [1, 2, 3, 0, 4, 5, 6, 128];

        ThumbnailPixels.MakeOpaqueIfNoAlpha(bgra);

        CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 0, 4, 5, 6, 128 }, bgra);
    }
}
