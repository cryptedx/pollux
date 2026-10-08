using Pollux.Core.Files;
using Pollux.Core.Settings;

namespace Pollux.Core.Tests.Files;

[TestClass]
public sealed class FilesTests
{
    private static FileEntry F(string name, long size = 0, int minutesAgo = 0) =>
        new(name, name, false, size, new DateTime(2026, 1, 1).AddMinutes(-minutesAgo), FileAttributes.Normal);

    private static FileEntry D(string name) =>
        new(name, name, true, 0, new DateTime(2026, 1, 1), FileAttributes.Directory);

    [TestMethod]
    [DataRow(612L, "612 B")]
    [DataRow(1229L, "1.2 K")]
    [DataRow(31457280L, "30.0 M")]
    [DataRow(5368709120L, "5.0 G")]
    public void FormatsSizes(long bytes, string expected) => Assert.AreEqual(expected, Format.Size(bytes));

    [TestMethod]
    public void NaturalSortFoldersFirst()
    {
        var entries = new[] { F("file10.txt"), D("zeta"), F("file2.txt"), D("Alpha"), F("File1.txt") };

        var sorted = entries.Order(new EntryComparer(SortColumn.Name, false, true)).Select(e => e.Name).ToArray();

        CollectionAssert.AreEqual(new[] { "Alpha", "zeta", "File1.txt", "file2.txt", "file10.txt" }, sorted);
    }

    [TestMethod]
    public void SortBySizeDescending()
    {
        var sorted = new[] { F("a", 1), F("b", 3), F("c", 2) }
            .Order(new EntryComparer(SortColumn.Size, true, true)).Select(e => e.Name).ToArray();

        CollectionAssert.AreEqual(new[] { "b", "c", "a" }, sorted);
    }

    [TestMethod]
    [DataRow("README.md", "MD", "MD")]
    [DataRow("archive.tar.gz", "GZ", "GZ")]
    [DataRow("setup.exe", "EXE", "EX")]
    [DataRow(".gitignore", "File", "··")]
    [DataRow("Makefile", "File", "")]
    public void TypeLabelsAndBadges(string name, string type, string badge)
    {
        Assert.AreEqual(type, Format.TypeLabel(F(name)));
        Assert.AreEqual(badge, Format.Badge(F(name)));
    }

    [TestMethod]
    public void OneDriveCloudOnlyIsDetected()
    {
        var e = F("x.pdf") with { Attributes = FileAttributes.Archive | (FileAttributes)0x00400000 };

        Assert.AreEqual(CloudState.CloudOnly, e.Cloud);
    }

    [TestMethod]
    public async Task ReaderStreamsRealDirectory()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            for (var i = 0; i < 1200; i++) File.WriteAllText(Path.Combine(dir.FullName, $"f{i}.txt"), "x");
            Directory.CreateDirectory(Path.Combine(dir.FullName, "sub"));

            var batches = new List<int>();
            await foreach (var b in DirectoryReader.ReadAsync(dir.FullName, batchSize: 500)) batches.Add(b.Count);

            Assert.AreEqual(1201, batches.Sum());
            Assert.AreEqual(500, batches[0]);
        }
        finally { dir.Delete(true); }
    }
}
