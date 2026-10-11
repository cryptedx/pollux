using Pollux.Core.Clipboard;

namespace Pollux.Core.Tests.Clipboard;

[TestClass]
public sealed class ClipHistoryKeeperTests
{
    private static readonly DateTime T0 = new(2026, 10, 1, 12, 0, 0);

    /// <summary>Reversible fake of DPAPI whose reads and writes can be locked, failed or held up from the test.</summary>
    private sealed class ControlledProtector : IClipProtector
    {
        public volatile bool Locked;
        public volatile bool FailWrites;
        public ManualResetEventSlim? HoldWrites;
        public ManualResetEventSlim WriteStarted { get; } = new();

        public byte[] Protect(byte[] data)
        {
            WriteStarted.Set();
            HoldWrites?.Wait(TimeSpan.FromSeconds(10));
            if (FailWrites) throw new IOException("disk full");
            return data.Select(b => (byte)(b ^ 0x5A)).ToArray();
        }

        public byte[] Unprotect(byte[] data)
        {
            if (Locked) throw new UnauthorizedAccessException("locked");
            return data.Select(b => (byte)(b ^ 0x5A)).ToArray();
        }
    }

    private sealed class FixedTime(DateTime local) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local)).ToUniversalTime();
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Local;
    }

    private sealed class Fixture : IDisposable
    {
        public DirectoryInfo Dir { get; } = Directory.CreateTempSubdirectory();
        public ControlledProtector Protector { get; } = new();
        public ClipRetention Retention { get; set; } = ClipRetention.Default;
        public List<string> Infos { get; } = [];
        public List<Exception> Errors { get; } = [];
        public ClipStore Store { get; }
        public ClipHistoryKeeper Keeper { get; }

        public Fixture()
        {
            Store = new ClipStore(Dir.FullName, Protector);
            Keeper = new ClipHistoryKeeper(Store, () => Retention, Infos.Add, Errors.Add, new FixedTime(T0.AddMinutes(10)));
        }

        public ClipStore Reopen() => new(Dir.FullName, Protector);

        public void Dispose() => Dir.Delete(recursive: true);
    }

    private static void SeedStored(Fixture f, params string[] texts)
    {
        var history = new ClipboardHistory();
        for (var i = 0; i < texts.Length; i++)
        {
            history.Add(Clip.FromText(texts[i], T0.AddMinutes(i), null, null), T0.AddMinutes(i));
        }

        f.Store.Save(history);
    }

    private static byte[] StoredBytes(Fixture f) => File.ReadAllBytes(Path.Combine(f.Dir.FullName, "history.dat"));

    [TestMethod]
    public async Task AnUnreadableStoreIsNeverWrittenAndRetriedLater()
    {
        using var f = new Fixture();
        SeedStored(f, "old");
        var before = StoredBytes(f);
        f.Protector.Locked = true;

        var history = f.Keeper.History;
        Assert.AreEqual(0, history.Recent.Count);
        history.Add(Clip.FromText("new", T0.AddMinutes(5), null, null), T0.AddMinutes(5));
        f.Keeper.MarkChanged();
        var merged = 0;
        f.Keeper.Merged += () => merged++;

        await f.Keeper.FlushAsync();
        f.Keeper.Shutdown();

        CollectionAssert.AreEqual(before, StoredBytes(f));
        Assert.IsTrue(f.Keeper.IsDirty, "still waiting for a readable store");
        Assert.AreEqual(0, merged);
        Assert.IsTrue(f.Infos.Any(i => i.Contains("could not be read")));

        f.Protector.Locked = false;
        await f.Keeper.FlushAsync();

        Assert.AreEqual(1, merged);
        CollectionAssert.AreEquivalent(new[] { "new", "old" }, history.Recent.Select(c => c.Text).ToArray());
        CollectionAssert.AreEquivalent(new[] { "new", "old" }, f.Reopen().Load().Recent.Select(c => c.Text).ToArray());
        Assert.IsFalse(f.Keeper.IsDirty);
    }

    [TestMethod]
    public async Task AFailedWriteMarksTheHistoryDirtyAgainAndTheNextFlushRetries()
    {
        using var f = new Fixture();
        f.Keeper.History.Add(Clip.FromText("a", T0, null, null), T0);
        f.Keeper.MarkChanged();
        f.Protector.FailWrites = true;

        await f.Keeper.FlushAsync();

        Assert.IsTrue(f.Keeper.IsDirty);
        Assert.AreEqual(1, f.Errors.Count);

        f.Protector.FailWrites = false;
        await f.Keeper.FlushAsync();

        Assert.IsFalse(f.Keeper.IsDirty);
        Assert.AreEqual("a", f.Reopen().Load().Recent.Single().Text);
    }

    [TestMethod]
    public async Task ShutdownWaitsForTheWriteInFlightThenFlushesWhatChangedMeanwhile()
    {
        using var f = new Fixture();
        f.Keeper.History.Add(Clip.FromText("first", T0, null, null), T0);
        f.Keeper.MarkChanged();
        f.Protector.HoldWrites = new ManualResetEventSlim();

        var flush = f.Keeper.FlushAsync();
        Assert.IsTrue(f.Protector.WriteStarted.Wait(TimeSpan.FromSeconds(10)));
        f.Keeper.History.Add(Clip.FromText("second", T0.AddMinutes(1), null, null), T0.AddMinutes(1));
        f.Keeper.MarkChanged();

        var shutdown = Task.Run(f.Keeper.Shutdown);
        await Task.Delay(200);
        Assert.IsFalse(shutdown.IsCompleted, "Shutdown must wait for the write in flight");

        f.Protector.HoldWrites.Set();
        await shutdown.WaitAsync(TimeSpan.FromSeconds(10));
        await flush;

        CollectionAssert.AreEqual(new[] { "second", "first" }, f.Reopen().Load().Recent.Select(c => c.Text).ToArray());
        Assert.IsFalse(f.Keeper.IsDirty);
    }

    [TestMethod]
    public async Task TheSnapshotIsTakenOnTheCallingThreadBeforeFlushAsyncReturns()
    {
        using var f = new Fixture();
        f.Keeper.History.Add(Clip.FromText("first", T0, null, null), T0);
        f.Keeper.MarkChanged();
        f.Protector.HoldWrites = new ManualResetEventSlim();

        var flush = f.Keeper.FlushAsync();
        // Changed after FlushAsync handed back its task: the snapshot already exists, so this is not part of that write.
        f.Keeper.History.Add(Clip.FromText("later", T0.AddMinutes(1), null, null), T0.AddMinutes(1));
        f.Keeper.MarkChanged();
        f.Protector.HoldWrites.Set();
        await flush;

        Assert.AreEqual("first", f.Reopen().Load().Recent.Single().Text);
        Assert.IsTrue(f.Keeper.IsDirty);
    }

    [TestMethod]
    public async Task ChangingTheRetentionPrunesAndMarksDirty()
    {
        using var f = new Fixture();
        var history = f.Keeper.History;
        for (var i = 0; i < 5; i++) history.Add(Clip.FromText("c" + i, T0.AddMinutes(i), null, null), T0.AddMinutes(i));
        f.Keeper.MarkChanged();
        await f.Keeper.FlushAsync();
        Assert.IsFalse(f.Keeper.IsDirty);
        Assert.IsFalse(f.Keeper.ApplyRetention(), "unchanged retention does nothing");

        f.Retention = new ClipRetention(MaxClips: 2, MaxAgeDays: 30);

        Assert.IsTrue(f.Keeper.ApplyRetention());
        Assert.AreEqual(2, history.Recent.Count);
        Assert.IsTrue(f.Keeper.IsDirty);
        await f.Keeper.FlushAsync();
        Assert.AreEqual(2, f.Reopen().Load().Recent.Count);
    }

    [TestMethod]
    public void RetentionIsAppliedWhenTheHistoryLoads()
    {
        using var f = new Fixture();
        SeedStored(f, "a", "b", "c");
        f.Retention = new ClipRetention(MaxClips: 1, MaxAgeDays: 30);

        f.Keeper.BeginLoad();

        Assert.AreEqual("c", f.Keeper.History.Recent.Single().Text);
        Assert.IsFalse(f.Keeper.ApplyRetention());
    }

    [TestMethod]
    public async Task ClearKeepsKeptClipsAndIsWritten()
    {
        using var f = new Fixture();
        var history = f.Keeper.History;
        var kept = history.Add(Clip.FromText("keep", T0, null, null), T0);
        history.Add(Clip.FromText("drop", T0.AddMinutes(1), null, null), T0.AddMinutes(1));
        history.Keep(kept.Id);
        f.Keeper.MarkChanged();
        await f.Keeper.FlushAsync();

        f.Keeper.Clear();
        Assert.IsTrue(f.Keeper.IsDirty);
        await f.Keeper.FlushAsync();

        var loaded = f.Reopen().Load();
        Assert.AreEqual("keep", loaded.Kept.Single().Text);
        Assert.AreEqual(0, loaded.Recent.Count);
    }

    [TestMethod]
    public void ShutdownWithoutChangesWritesNothing()
    {
        using var f = new Fixture();
        _ = f.Keeper.History;

        f.Keeper.Shutdown();

        Assert.IsFalse(File.Exists(Path.Combine(f.Dir.FullName, "history.dat")));
    }
}
