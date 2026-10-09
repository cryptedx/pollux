using System.Text;
using Pollux.Core.Clipboard;

namespace Pollux.Core.Tests.Clipboard;

[TestClass]
public sealed class ClipStoreTests
{
    private static readonly DateTime T0 = new(2026, 10, 1, 12, 0, 0);

    /// <summary>Stands in for DPAPI: reversible, but the bytes on disk differ from the plain content.</summary>
    private sealed class XorProtector : IClipProtector
    {
        public int Calls { get; private set; }

        public byte[] Protect(byte[] data)
        {
            Calls++;
            return data.Select(b => (byte)(b ^ 0x5A)).ToArray();
        }

        public byte[] Unprotect(byte[] data) => data.Select(b => (byte)(b ^ 0x5A)).ToArray();
    }

    /// <summary>Simulates a locked history file (antivirus scan): the first <c>failures</c> reads fail with an access error.</summary>
    private sealed class LockedProtector(int failures) : IClipProtector
    {
        private readonly XorProtector _inner = new();
        private int _failures = failures;

        public byte[] Protect(byte[] data) => _inner.Protect(data);

        public byte[] Unprotect(byte[] data)
        {
            if (_failures-- > 0) throw new UnauthorizedAccessException("locked");
            return _inner.Unprotect(data);
        }
    }

    private static DirectoryInfo TempDir() => Directory.CreateTempSubdirectory();

    [TestMethod]
    public void HistoryRoundTripsThroughEncryptedStorage()
    {
        var dir = TempDir();
        try
        {
            var history = new ClipboardHistory();
            var kept2 = history.Add(Clip.FromText("second kept", T0, "Code", @"C:\Code\Code.exe"), T0);
            var kept1 = history.Add(Clip.FromFiles([@"C:\a.txt", @"D:\b.png"], T0.AddMinutes(1), "explorer", null), T0.AddMinutes(1));
            history.Add(Clip.FromImage([137, 80, 78, 71, 9, 9], T0.AddMinutes(2), "mspaint", null), T0.AddMinutes(2));
            history.Add(Clip.FromText("secret password-like text", T0.AddMinutes(3), "notepad", null), T0.AddMinutes(3));
            history.Keep(kept1.Id, "Two files");
            history.Keep(kept2.Id);

            var store = new ClipStore(dir.FullName, new XorProtector());
            store.Save(history);

            var loadedStore = new ClipStore(dir.FullName, new XorProtector());
            var loaded = loadedStore.Load();

            CollectionAssert.AreEqual(new[] { kept1.Id, kept2.Id }, loaded.Kept.Select(c => c.Id).ToArray());
            Assert.IsTrue(loaded.Kept.All(c => c.Kept));
            Assert.AreEqual("Two files", loaded.Kept[0].Title);
            CollectionAssert.AreEqual(new[] { @"C:\a.txt", @"D:\b.png" }, loaded.Kept[0].Files);
            Assert.AreEqual(ClipKind.Files, loaded.Kept[0].Kind);
            Assert.AreEqual("Code", loaded.Kept[1].SourceApp);
            Assert.AreEqual(@"C:\Code\Code.exe", loaded.Kept[1].SourcePath);
            Assert.AreEqual(T0, loaded.Kept[1].CapturedAt);

            CollectionAssert.AreEqual(new[] { "secret password-like text", null }, loaded.Recent.Select(c => c.Text).ToArray());
            var image = loaded.Recent[1];
            Assert.AreEqual(ClipKind.Image, image.Kind);
            CollectionAssert.AreEqual(new byte[] { 137, 80, 78, 71, 9, 9 }, loadedStore.ReadImage(image));

            // Dedupe still works after a restart.
            loaded.Add(Clip.FromImage([137, 80, 78, 71, 9, 9], T0.AddMinutes(4), "mspaint", null), T0.AddMinutes(4));
            Assert.AreEqual(2, loaded.Recent.Count);
            Assert.AreSame(image, loaded.Recent[0]);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [TestMethod]
    public void NothingIsStoredInPlainText()
    {
        var dir = TempDir();
        try
        {
            var history = new ClipboardHistory();
            history.Add(Clip.FromText("hunter2-plaintext", T0, "notepad", null), T0);
            history.Add(Clip.FromImage(Encoding.UTF8.GetBytes("image-plaintext"), T0, "mspaint", null), T0);

            new ClipStore(dir.FullName, new XorProtector()).Save(history);

            foreach (var file in dir.EnumerateFiles("*", SearchOption.AllDirectories))
            {
                var text = Encoding.UTF8.GetString(File.ReadAllBytes(file.FullName));
                Assert.DoesNotContain("plaintext", text, file.Name);
                Assert.DoesNotContain("notepad", text, file.Name);
            }
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [TestMethod]
    public void ImagesAreWrittenOnceAndRemovedWithTheirClip()
    {
        var dir = TempDir();
        try
        {
            var protector = new XorProtector();
            var store = new ClipStore(dir.FullName, protector);
            var history = new ClipboardHistory();
            var image = history.Add(Clip.FromImage([1, 2, 3], T0, null, null), T0);

            store.Save(history);
            var callsAfterFirstSave = protector.Calls;
            store.Save(history);

            // Only the history file is encrypted again; the image is already on disk.
            Assert.AreEqual(callsAfterFirstSave + 1, protector.Calls);
            CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, store.ReadImage(image));

            history.Delete(image.Id);
            store.Save(history);

            Assert.IsNull(store.ReadImage(image));
            Assert.AreEqual(1, dir.EnumerateFiles("*", SearchOption.AllDirectories).Count());
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [TestMethod]
    public void MissingStorageLoadsAnEmptyHistory()
    {
        var dir = TempDir();
        try
        {
            var loaded = new ClipStore(Path.Combine(dir.FullName, "clipboard"), new XorProtector()).Load();

            Assert.AreEqual(0, loaded.All.Count());
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [TestMethod]
    public void UnreadableStorageLoadsEmptyAndKeepsTheOldFileAside()
    {
        var dir = TempDir();
        try
        {
            var history = new ClipboardHistory();
            history.Add(Clip.FromText("a", T0, null, null), T0);
            new ClipStore(dir.FullName, new XorProtector()).Save(history);
            var file = Directory.GetFiles(dir.FullName).Single();
            File.WriteAllBytes(file, [1, 2, 3]);

            var loaded = new ClipStore(dir.FullName, new XorProtector()).Load();

            Assert.AreEqual(0, loaded.All.Count());
            Assert.IsTrue(File.Exists(file + ".broken"));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [TestMethod]
    public void ATemporaryReadErrorIsRetried()
    {
        var dir = TempDir();
        try
        {
            var history = new ClipboardHistory();
            history.Add(Clip.FromText("a", T0, null, null), T0);
            new ClipStore(dir.FullName, new XorProtector()).Save(history);

            var store = new ClipStore(dir.FullName, new LockedProtector(failures: 1));
            var loaded = store.Load();

            Assert.AreEqual("a", loaded.Recent.Single().Text);
            Assert.IsFalse(store.LoadFailed);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [TestMethod]
    public void AStorageThatCannotBeReadIsNeverOverwritten()
    {
        var dir = TempDir();
        try
        {
            var history = new ClipboardHistory();
            var kept = history.Add(Clip.FromText("kept", T0, null, null), T0);
            history.Keep(kept.Id);
            history.Add(Clip.FromImage([1, 2, 3], T0, null, null), T0);
            new ClipStore(dir.FullName, new XorProtector()).Save(history);
            var before = dir.EnumerateFiles("*", SearchOption.AllDirectories).ToDictionary(f => f.FullName, f => File.ReadAllBytes(f.FullName));

            var store = new ClipStore(dir.FullName, new LockedProtector(failures: int.MaxValue));
            var loaded = store.Load();
            Assert.IsTrue(store.LoadFailed);
            loaded.Add(Clip.FromText("new", T0.AddMinutes(1), null, null), T0.AddMinutes(1));

            Assert.IsFalse(store.Save(loaded));
            Assert.IsNull(store.Snapshot(loaded));

            var after = dir.EnumerateFiles("*", SearchOption.AllDirectories).ToDictionary(f => f.FullName, f => File.ReadAllBytes(f.FullName));
            CollectionAssert.AreEquivalent(before.Keys, after.Keys);
            foreach (var (file, bytes) in before) CollectionAssert.AreEqual(bytes, after[file], file);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [TestMethod]
    public void SavingWorksAgainOnceALoadSucceeds()
    {
        var dir = TempDir();
        try
        {
            var history = new ClipboardHistory();
            history.Add(Clip.FromText("a", T0, null, null), T0);
            new ClipStore(dir.FullName, new XorProtector()).Save(history);

            var store = new ClipStore(dir.FullName, new LockedProtector(failures: 3));
            store.Load();
            Assert.IsTrue(store.LoadFailed);

            var stored = store.Load();
            Assert.IsFalse(store.LoadFailed);
            Assert.IsTrue(store.Save(stored));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [TestMethod]
    public void ASnapshotIsUnaffectedByLaterChangesAndCanBeWrittenElsewhere()
    {
        var dir = TempDir();
        try
        {
            var store = new ClipStore(dir.FullName, new XorProtector());
            var history = new ClipboardHistory();
            var clip = history.Add(Clip.FromText("before", T0, null, null), T0);
            var image = history.Add(Clip.FromImage([4, 5, 6], T0, null, null), T0);

            var snapshot = store.Snapshot(history)!;
            history.SetTitle(clip.Id, "changed after the snapshot");
            history.Add(Clip.FromText("added after the snapshot", T0.AddMinutes(1), null, null), T0.AddMinutes(1));
            store.Write(snapshot);

            var loaded = new ClipStore(dir.FullName, new XorProtector()).Load();
            CollectionAssert.AreEqual(new[] { null, "before" }, loaded.Recent.Select(c => c.Text).ToArray());
            Assert.IsNull(loaded.Recent[1].Title);
            Assert.IsNull(image.Image, "written images are dropped from memory");
            CollectionAssert.AreEqual(new byte[] { 4, 5, 6 }, store.ReadImage(image));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
