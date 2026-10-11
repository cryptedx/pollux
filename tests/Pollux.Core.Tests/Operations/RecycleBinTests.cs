using Pollux.Core.Operations;

namespace Pollux.Core.Tests.Operations;

[TestClass]
public sealed class RecycleBinTests
{
    private DirectoryInfo _root = null!;
    private string Bin => Path.Combine(_root.FullName, "bin");

    [TestInitialize]
    public void Setup() => _root = Directory.CreateTempSubdirectory("pollux-bin-");

    [TestCleanup]
    public void Cleanup() => _root.Delete(true);

    [TestMethod]
    public void ParsesBothInfoFormats()
    {
        var when = new DateTime(2026, 10, 11, 9, 30, 0, DateTimeKind.Utc);

        var v2 = RecycleBin.ParseInfo(FakeRecycleBin.InfoV2(@"C:\Users\me\a.txt", 3, when));
        var v1 = RecycleBin.ParseInfo(FakeRecycleBin.InfoV1(@"D:\old\b.txt", 5, when));

        Assert.AreEqual((@"C:\Users\me\a.txt", when), v2);
        Assert.AreEqual((@"D:\old\b.txt", when), v1);
    }

    [TestMethod]
    public void RejectsBrokenInfo()
    {
        Assert.IsNull(RecycleBin.ParseInfo([1, 2, 3]));
        var bad = FakeRecycleBin.InfoV2(@"C:\a.txt", 1, DateTime.UtcNow);
        bad[0] = 7;
        Assert.IsNull(RecycleBin.ParseInfo(bad));
        var truncated = FakeRecycleBin.InfoV2(@"C:\a.txt", 1, DateTime.UtcNow)[..30];
        Assert.IsNull(RecycleBin.ParseInfo(truncated));
    }

    [TestMethod]
    public void FindsTheFirstEntryDeletedSinceTheGivenTime()
    {
        Directory.CreateDirectory(Bin);
        var path = Path.Combine(_root.FullName, "a.txt");
        var older = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var since = new DateTime(2026, 10, 11, 9, 0, 0, DateTimeKind.Utc);
        File.WriteAllBytes(Path.Combine(Bin, "$IAAAAAA.txt"), FakeRecycleBin.InfoV2(path, 1, older));
        File.WriteAllBytes(Path.Combine(Bin, "$IBBBBBB.txt"), FakeRecycleBin.InfoV2(path, 1, since.AddSeconds(1)));
        File.WriteAllBytes(Path.Combine(Bin, "$ICCCCCC.txt"), FakeRecycleBin.InfoV2(path, 1, since.AddMinutes(5)));
        File.WriteAllBytes(Path.Combine(Bin, "$IDDDDDD.txt"), FakeRecycleBin.InfoV2(path + ".other", 1, since.AddSeconds(2)));
        foreach (var id in new[] { "AAAAAA", "BBBBBB", "CCCCCC", "DDDDDD" }) File.WriteAllText(Path.Combine(Bin, $"$R{id}.txt"), "x");
        File.WriteAllBytes(Path.Combine(Bin, "$IEEEEEE.txt"), FakeRecycleBin.InfoV2(path, 1, since)); // its data is gone

        var entries = RecycleBin.Read(Bin, DateTime.MinValue);
        var found = RecycleBin.Find(entries, path.ToUpperInvariant(), since);

        Assert.AreEqual(5, entries.Count);
        Assert.AreEqual(Path.Combine(Bin, "$RBBBBBB.txt"), found?.DataFile);
        Assert.IsNull(RecycleBin.Find(entries, path, since.AddHours(1)));
    }

    [TestMethod]
    public void ReadingAMissingBinFindsNothing()
    {
        Assert.AreEqual(0, RecycleBin.Read(Path.Combine(_root.FullName, "nope"), DateTime.MinValue).Count);
    }

    [TestMethod]
    public void RestoresFilesAndFoldersToTheirOriginalPlace()
    {
        var bin = new FakeRecycleBin(Bin);
        var file = Path.Combine(_root.FullName, "docs", "a.txt");
        var folder = Path.Combine(_root.FullName, "proj");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "aaa");
        Directory.CreateDirectory(Path.Combine(folder, "sub"));
        File.WriteAllText(Path.Combine(folder, "sub", "b.txt"), "bbb");
        var since = DateTime.UtcNow;
        bin.Recycle([file, folder]);
        Directory.Delete(Path.GetDirectoryName(file)!);

        foreach (var path in new[] { file, folder })
        {
            RecycleBin.Restore(RecycleBin.Find(RecycleBin.Read(Bin, since), path, since)!);
        }

        Assert.AreEqual("aaa", File.ReadAllText(file), "the missing parent folder is created again");
        Assert.AreEqual("bbb", File.ReadAllText(Path.Combine(folder, "sub", "b.txt")));
        Assert.AreEqual(0, Directory.GetFileSystemEntries(Bin).Length, "both $R and $I files are gone");
    }
}
