using System.IO.Compression;
using Pollux.Core.Archives;
using Pollux.Core.Files;
using Pollux.Core.Navigation;

namespace Pollux.Core.Tests.Navigation;

[TestClass]
public sealed class VirtualLocationTests
{
    private sealed class FakeShell : IShellNamespace
    {
        public string? Asked;

        public IEnumerable<FileEntry> Enumerate(string parsingName, CancellationToken cancellationToken)
        {
            Asked = parsingName;
            yield return new FileEntry("Local Disk (C:)", @"C:\", true, 0, default, FileAttributes.Directory);
            yield return new FileEntry("Phone", "pollux:shell:uuid:1", true, 0, default, FileAttributes.Directory);
        }

        public IReadOnlyList<LocationSegment> Segments(string location) => [new("This PC", WinPath.ThisPc), new("Phone", location)];

        public string? FileSystemPath(string location) => null;
    }

    private sealed class FakeShares : IServerShares
    {
        public IReadOnlyList<NetShare> List(string server) => server == "nas"
            ? [new("media", 0, "Movies"), new("C$", 0x80000000, "Default share"), new("IPC$", 0x80000003, null), new("printer", 1, null), new("backup$", 0, null)]
            : throw new IOException("The network path was not found.");
    }

    private static bool ZipExists(string path) => path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);

    private static async Task<List<FileEntry>> ReadAll(LocationReader reader, string location)
    {
        var all = new List<FileEntry>();
        await foreach (var batch in reader.ReadAsync(location, 2)) all.AddRange(batch);
        return all;
    }

    [TestMethod]
    public void SharesAreDiskSharesWithAdminSharesHidden()
    {
        var entries = ServerShares.Entries("nas", new FakeShares().List("nas"));
        CollectionAssert.AreEqual(new[] { "media", "C$", "backup$" }, entries.Select(e => e.Name).ToArray());
        Assert.AreEqual(@"\\nas\media", entries[0].FullPath);
        Assert.IsTrue(entries.All(e => e.IsDirectory));
        CollectionAssert.AreEqual(new[] { false, true, true }, entries.Select(e => e.IsHidden).ToArray());
    }

    [TestMethod]
    public async Task ReaderListsAServerThroughItsShares()
    {
        var reader = new LocationReader(new FakeShell(), new FakeShares(), ZipExists);
        var entries = await ReadAll(reader, @"\\nas");
        Assert.AreEqual(3, entries.Count);
        await Assert.ThrowsExactlyAsync<IOException>(() => ReadAll(reader, @"\\gone"));
    }

    [TestMethod]
    public async Task ReaderListsShellFoldersByParsingName()
    {
        var shell = new FakeShell();
        var reader = new LocationReader(shell, new FakeShares(), ZipExists);
        var entries = await ReadAll(reader, WinPath.ThisPc);
        Assert.AreEqual(Locations.ThisPcParsingName, shell.Asked);
        CollectionAssert.AreEqual(new[] { @"C:\", "pollux:shell:uuid:1" }, entries.Select(e => e.FullPath).ToArray());
    }

    [TestMethod]
    public async Task ReaderListsFoldersAndZips()
    {
        var root = Directory.CreateTempSubdirectory("pollux-reader-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(root, "f.txt"), "x");
            var zip = Path.Combine(root, "a.zip");
            using (var fs = File.Create(zip))
            using (var archive = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                archive.CreateEntry("in/side.txt");
            }

            var reader = new LocationReader(new FakeShell(), new FakeShares());
            CollectionAssert.AreEquivalent(new[] { "f.txt", "a.zip" }, (await ReadAll(reader, root)).Select(e => e.Name).ToArray());
            CollectionAssert.AreEqual(new[] { "in" }, (await ReadAll(reader, zip)).Select(e => e.Name).ToArray());
            CollectionAssert.AreEqual(new[] { "side.txt" }, (await ReadAll(reader, ZipPath.Combine(zip, "in"))).Select(e => e.Name).ToArray());
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => ReadAll(reader, WinPath.Home));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task SegmentsComeFromTheShellOnlyForUnnamedShellLocations()
    {
        var reader = new LocationReader(new FakeShell(), new FakeShares(), ZipExists);
        Assert.AreEqual(2, (await reader.SegmentsAsync("pollux:shell:uuid:1")).Count);
        Assert.AreEqual("This PC", (await reader.SegmentsAsync(WinPath.ThisPc)).Single().Label);
        Assert.AreEqual(3, (await reader.SegmentsAsync(@"C:\a\b")).Count);
    }

    [TestMethod]
    public async Task StreamRunsOnAWorkerAndBatches()
    {
        var caller = Environment.CurrentManagedThreadId;
        var producer = 0;
        IEnumerable<FileEntry> Source()
        {
            producer = Environment.CurrentManagedThreadId;
            for (var i = 0; i < 5; i++) yield return new FileEntry(i.ToString(System.Globalization.CultureInfo.InvariantCulture), "x", false, 0, default, 0);
        }

        var sizes = new List<int>();
        await foreach (var batch in DirectoryReader.Stream(Source, 2, staThread: true)) sizes.Add(batch.Count);
        CollectionAssert.AreEqual(new[] { 2, 3 }, sizes);
        Assert.AreNotEqual(caller, producer);
    }

    [TestMethod]
    public async Task StreamSurfacesTheSourcesError()
    {
        static IEnumerable<FileEntry> Broken() => throw new UnauthorizedAccessException("no");
        await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(async () =>
        {
            await foreach (var _ in DirectoryReader.Stream(Broken, staThread: true)) { }
        });
    }

    [TestMethod]
    public void OpenActions()
    {
        static FileEntry File(string path, bool dir = false) => new(Path.GetFileName(path), path, dir, 0, default, 0);

        Assert.AreEqual(new OpenAction.Navigate(@"C:\x"), ItemOpen.For(File(@"C:\x", dir: true), isFile: ZipExists));
        Assert.AreEqual(new OpenAction.Launch(@"C:\x.txt"), ItemOpen.For(File(@"C:\x.txt"), isFile: ZipExists));
        Assert.AreEqual(new OpenAction.Navigate(@"C:\a.zip"), ItemOpen.For(File(@"C:\a.zip"), isFile: ZipExists));
        Assert.AreEqual(new OpenAction.Launch(@"C:\a.zip"), ItemOpen.For(File(@"C:\a.zip"), browseZips: false, isFile: ZipExists));
        Assert.AreEqual(new OpenAction.ExtractAndLaunch(@"C:\a.zip\d\b.txt"), ItemOpen.For(File(@"C:\a.zip\d\b.txt"), isFile: ZipExists));
        Assert.AreEqual(new OpenAction.ExtractAndLaunch(@"C:\a.zip\inner.zip"), ItemOpen.For(File(@"C:\a.zip\inner.zip"), isFile: ZipExists));
        Assert.AreEqual(new OpenAction.Navigate(@"C:\a.zip\d"), ItemOpen.For(File(@"C:\a.zip\d", dir: true), isFile: ZipExists));
        Assert.AreEqual(new OpenAction.InvokeShell("uuid:1"), ItemOpen.For(File("pollux:shell:uuid:1"), isFile: ZipExists));
        Assert.AreEqual(new OpenAction.InvokeShell(@"::{26EE0668-A00A-44D7-9371-BEB064C98683}\0\::{ABC}"), ItemOpen.For(File(@"::{26EE0668-A00A-44D7-9371-BEB064C98683}\0\::{ABC}"), isFile: ZipExists));
        Assert.AreEqual(new OpenAction.Navigate(@"\\nas"), ItemOpen.For(File(@"\\nas"), isFile: ZipExists));
    }

    [TestMethod]
    [DataRow("This PC", WinPath.ThisPc)]
    [DataRow("computer", WinPath.ThisPc)]
    [DataRow("shell:MyComputerFolder", WinPath.ThisPc)]
    [DataRow("network", WinPath.Network)]
    [DataRow("shell:NetworkPlacesFolder", WinPath.Network)]
    [DataRow("Control Panel", WinPath.ControlPanel)]
    [DataRow("shell:ControlPanelFolder", WinPath.ControlPanel)]
    [DataRow("libraries", WinPath.Libraries)]
    [DataRow("home", WinPath.Home)]
    [DataRow("recycle bin", WinPath.RecycleBin)]
    [DataRow("downloads", null)]
    public void WellKnownNames(string typed, string? location) => Assert.AreEqual(location, WinPath.WellKnown(typed));
}
