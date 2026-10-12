using System.IO.Compression;
using Pollux.Core.Archives;

namespace Pollux.Core.Tests.Archives;

[TestClass]
public sealed class ZipFolderTests
{
    private string _root = "";
    private string _zip = "";

    [TestInitialize]
    public void Setup()
    {
        _root = Directory.CreateTempSubdirectory("pollux-zipfolder-").FullName;
        _zip = Path.Combine(_root, "a.zip");
        Write(_zip, ("top.txt", "hello"), ("docs/a.txt", "A"), ("docs/sub/b.txt", "BB"), ("empty/", ""));
    }

    [TestCleanup]
    public void Cleanup()
    {
        ZipTemp.Delete(_root);
    }

    private static void Write(string path, params (string Name, string Content)[] entries)
    {
        using var fs = File.Create(path);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        foreach (var (name, content) in entries)
        {
            var e = zip.CreateEntry(name);
            e.LastWriteTime = new DateTimeOffset(new DateTime(2024, 3, 4, 5, 6, 8));
            if (name.EndsWith('/')) continue;
            using var w = new StreamWriter(e.Open());
            w.Write(content);
        }
    }

    [TestMethod]
    public void ParseKeepsTheArchiveAsWritten()
    {
        Assert.IsTrue(ZipFolder.TryParse("/tmp/x/a.zip/docs/sub", out var archive, out var inner));
        Assert.AreEqual("/tmp/x/a.zip", archive);
        Assert.AreEqual("docs/sub", inner);
        Assert.IsTrue(ZipFolder.TryParse(@"C:\x\A.ZIP\", out archive, out inner));
        Assert.AreEqual(@"C:\x\A.ZIP", archive);
        Assert.AreEqual("", inner);
        Assert.IsFalse(ZipFolder.TryParse(@"C:\x\.zip\y", out _, out _));
        Assert.IsFalse(ZipFolder.TryParse(@"C:\x\a.zipx", out _, out _));
    }

    [TestMethod]
    [DataRow("a.txt", true)]
    [DataRow("docs/sub/b.txt", true)]
    [DataRow("docs/", true)]
    [DataRow("../evil.txt", false)]
    [DataRow("a/../../evil.txt", false)]
    [DataRow(@"..\evil.txt", false)]
    [DataRow("/etc/passwd", false)]
    [DataRow(@"C:\evil.txt", false)]
    [DataRow("C:evil.txt", false)]
    [DataRow("a//b", false)]
    [DataRow("./a", false)]
    [DataRow("", false)]
    public void SafeEntryNames(string name, bool safe) => Assert.AreEqual(safe, ZipFolder.IsSafeEntryName(name));

    [TestMethod]
    public void ListsTheRootWithSynthesizedFolders()
    {
        var root = new ZipFolder().Children(_zip, "");
        CollectionAssert.AreEquivalent(new[] { "top.txt", "docs", "empty" }, root.Select(e => e.Name).ToArray());
        var top = root.Single(e => e.Name == "top.txt");
        Assert.AreEqual(5, top.Size);
        Assert.IsFalse(top.IsDirectory);
        Assert.IsTrue(top.Attributes.HasFlag(FileAttributes.ReadOnly));
        Assert.AreEqual(new DateTime(2024, 3, 4, 5, 6, 8), top.Modified);
        Assert.IsTrue(root.Single(e => e.Name == "docs").IsDirectory);
    }

    [TestMethod]
    public void ListsAFolderInsideByLocation()
    {
        var docs = new ZipFolder().Children(ZipPath.Combine(_zip, "docs"));
        CollectionAssert.AreEquivalent(new[] { "a.txt", "sub" }, docs.Select(e => e.Name).ToArray());
        Assert.AreEqual(ZipPath.Combine(_zip, "docs/sub"), docs.Single(e => e.Name == "sub").FullPath);
        Assert.AreEqual(0, new ZipFolder().Children(_zip, "empty").Count);
    }

    [TestMethod]
    public void AMissingFolderIsNotFound()
    {
        Assert.ThrowsExactly<DirectoryNotFoundException>(() => new ZipFolder().Children(_zip, "nope"));
        Assert.ThrowsExactly<DirectoryNotFoundException>(() => new ZipFolder().Children(_zip, "top.txt"));
    }

    [TestMethod]
    public void NeverListsEscapingEntries()
    {
        var evil = Path.Combine(_root, "evil.zip");
        Write(evil, ("../up.txt", "x"), ("a/../../up2.txt", "x"), (@"..\up3.txt", "x"), ("ok.txt", "x"));
        var names = new ZipFolder().Children(evil, "").Select(e => e.Name).ToArray();
        CollectionAssert.AreEqual(new[] { "ok.txt" }, names);
    }

    [TestMethod]
    public void ACorruptArchiveIsAnIOException()
    {
        var bad = Path.Combine(_root, "bad.zip");
        File.WriteAllText(bad, "not a zip");
        Assert.ThrowsExactly<IOException>(() => new ZipFolder().Children(bad, ""));
    }

    [TestMethod]
    public void RereadsAChangedArchive()
    {
        var folder = new ZipFolder();
        Assert.AreEqual(3, folder.Children(_zip, "").Count);
        Write(_zip, ("only.txt", "1"));
        File.SetLastWriteTimeUtc(_zip, DateTime.UtcNow.AddMinutes(1));
        CollectionAssert.AreEqual(new[] { "only.txt" }, folder.Children(_zip, "").Select(e => e.Name).ToArray());
    }

    [TestMethod]
    public void ExtractForOpenWritesAReadOnlyCopyOnce()
    {
        var temp = Path.Combine(_root, "temp", "session");
        var folder = new ZipFolder();
        var path = folder.ExtractForOpen(ZipPath.Combine(_zip, "docs/sub/b.txt"), temp);

        Assert.AreEqual("b.txt", Path.GetFileName(path));
        Assert.IsTrue(path.StartsWith(temp, StringComparison.Ordinal));
        Assert.AreEqual("BB", File.ReadAllText(path));
        Assert.IsTrue(File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly));
        Assert.AreEqual(path, folder.ExtractForOpen(ZipPath.Combine(_zip, "docs/sub/b.txt"), temp));
        Assert.ThrowsExactly<FileNotFoundException>(() => folder.ExtractForOpen(ZipPath.Combine(_zip, "docs"), temp));
    }

    [TestMethod]
    public void CopyOutExtractsFilesAndFolders()
    {
        var target = Path.Combine(_root, "out");
        var result = ZipFolder.CopyOut([ZipPath.Combine(_zip, "docs"), ZipPath.Combine(_zip, "top.txt")], target);

        Assert.AreEqual(3, result.Extracted);
        Assert.AreEqual("BB", File.ReadAllText(Path.Combine(target, "docs", "sub", "b.txt")));
        Assert.AreEqual("hello", File.ReadAllText(Path.Combine(target, "top.txt")));
        Assert.ThrowsExactly<ArgumentException>(() => ZipFolder.CopyOut([ZipPath.Combine(_zip, "top.txt"), Path.Combine(_root, "other.zip", "x")], target));
    }

    [TestMethod]
    public void TempCleanUpKeepsTheSessionAndRecentFolders()
    {
        var root = Path.Combine(_root, "temp");
        var mine = Directory.CreateDirectory(Path.Combine(root, "mine")).FullName;
        var old = Directory.CreateDirectory(Path.Combine(root, "old")).FullName;
        var recent = Directory.CreateDirectory(Path.Combine(root, "recent")).FullName;
        var locked = Path.Combine(old, "x.txt");
        File.WriteAllText(locked, "x");
        File.SetAttributes(locked, FileAttributes.ReadOnly);
        var cutoff = DateTime.UtcNow.AddDays(-1);
        Directory.SetLastWriteTimeUtc(mine, cutoff.AddDays(-5));
        Directory.SetLastWriteTimeUtc(old, cutoff.AddDays(-5));

        Assert.AreEqual(1, ZipTemp.CleanUp(root, mine, cutoff));
        Assert.IsTrue(Directory.Exists(mine));
        Assert.IsTrue(Directory.Exists(recent));
        Assert.IsFalse(Directory.Exists(old));
    }
}
