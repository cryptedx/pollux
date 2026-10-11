using System.IO.Compression;
using Pollux.Core.Archives;
using Pollux.Core.Operations;

namespace Pollux.Core.Tests.Archives;

[TestClass]
public sealed class ZipTests
{
    private string _root = "";

    [TestInitialize]
    public void Setup() => _root = Directory.CreateTempSubdirectory("pollux-zip-").FullName;

    [TestCleanup]
    public void Cleanup() => Directory.Delete(_root, recursive: true);

    private string Make(string rel, string content = "x")
    {
        var p = Path.Combine(_root, rel.Replace('\\', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, content);
        return p;
    }

    [TestMethod]
    public void ParseSplitsAtTheZipSegment()
    {
        Assert.IsTrue(ZipPath.TryParse(@"C:\data\a.ZIP\docs\b.txt", out var archive, out var inner));
        Assert.AreEqual(@"C:\data\a.ZIP", archive);
        Assert.AreEqual("docs/b.txt", inner);
        Assert.IsTrue(ZipPath.TryParse(@"C:\data\a.zip", out archive, out inner));
        Assert.AreEqual("", inner);
        Assert.IsTrue(ZipPath.TryParse(@"C:\data\a.zip\", out _, out inner));
        Assert.AreEqual("", inner);
        Assert.IsFalse(ZipPath.TryParse(@"C:\data\folder", out _, out _));
        Assert.IsFalse(ZipPath.TryParse(@"C:\data\.zip", out _, out _));
        Assert.IsFalse(ZipPath.TryParse(@"C:\data\a.zipx\b", out _, out _));
    }

    [TestMethod]
    public void CombineRoundTrips()
    {
        Assert.AreEqual(@"C:\a.zip", ZipPath.Combine(@"C:\a.zip", ""));
        Assert.AreEqual(@"C:\a.zip\x\y.txt", ZipPath.Combine(@"C:\a.zip", "x/y.txt"));
    }

    [TestMethod]
    public void ChildrenListsOneLevelAndSynthesizesFolders()
    {
        var t = new DateTime(2024, 1, 2);
        ZipEntryInfo[] entries = [new("top.txt", 5, t), new("docs/a.txt", 7, t), new("docs/sub/b.txt", 9, t), new("empty/", 0, t)];

        var root = ZipListing.Children(@"C:\a.zip", entries, "");
        CollectionAssert.AreEquivalent(new[] { "top.txt", "docs", "empty" }, root.Select(e => e.Name).ToArray());
        Assert.IsTrue(root.Single(e => e.Name == "docs").IsDirectory);
        Assert.AreEqual(@"C:\a.zip\docs", root.Single(e => e.Name == "docs").FullPath);
        Assert.AreEqual(5, root.Single(e => e.Name == "top.txt").Size);

        var docs = ZipListing.Children(@"C:\a.zip", entries, "docs");
        CollectionAssert.AreEquivalent(new[] { "a.txt", "sub" }, docs.Select(e => e.Name).ToArray());
        Assert.AreEqual(@"C:\a.zip\docs\sub", docs.Single(e => e.Name == "sub").FullPath);
    }

    [TestMethod]
    public void CompressThenExtractRoundTrips()
    {
        var one = Make(@"src\proj\one.txt", "1");
        Make(@"src\proj\sub\two.txt", "2");
        Directory.CreateDirectory(Path.Combine(_root, "src", "proj", "empty"));
        var loose = Make(@"src\loose.txt", "L");
        var dest = Path.Combine(_root, "out");
        Directory.CreateDirectory(dest);

        var zip = ZipArchiver.Compress([Path.GetDirectoryName(one)!, loose], dest);
        Assert.IsTrue(File.Exists(zip));
        Assert.AreEqual("src.zip", Path.GetFileName(zip));
        Assert.IsFalse(File.Exists(zip + ".part"));

        var target = ZipArchiver.ExtractTargetFor(zip, here: false);
        Assert.AreEqual("src", Path.GetFileName(target));
        var result = ZipArchiver.Extract(zip, target);
        Assert.AreEqual(3, result.Extracted);
        Assert.AreEqual("2", File.ReadAllText(Path.Combine(target, "proj", "sub", "two.txt")));
        Assert.AreEqual("L", File.ReadAllText(Path.Combine(target, "loose.txt")));
        Assert.IsTrue(Directory.Exists(Path.Combine(target, "proj", "empty")));
    }

    [TestMethod]
    public void CompressPicksAUniqueName()
    {
        var file = Make("doc.txt");
        var first = ZipArchiver.Compress([file], _root);
        var second = ZipArchiver.Compress([file], _root);
        Assert.AreEqual("doc.zip", Path.GetFileName(first));
        Assert.AreEqual("doc (2).zip", Path.GetFileName(second));
    }

    [TestMethod]
    public void ExtractSkipsEntriesThatEscapeTheTarget()
    {
        var zipPath = Path.Combine(_root, "evil.zip");
        using (var fs = File.Create(zipPath))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            foreach (var name in new[] { "../escaped.txt", "a/../../escaped2.txt", "..\\escaped3.txt", "ok.txt" })
            {
                using var w = new StreamWriter(zip.CreateEntry(name).Open());
                w.Write("data");
            }
        }

        var target = Path.Combine(_root, "t");
        var result = ZipArchiver.Extract(zipPath, target);

        Assert.AreEqual(1, result.Extracted);
        Assert.AreEqual(3, result.SkippedUnsafe);
        Assert.IsTrue(File.Exists(Path.Combine(target, "ok.txt")));
        Assert.IsFalse(File.Exists(Path.Combine(_root, "escaped.txt")));
        Assert.IsFalse(File.Exists(Path.Combine(_root, "escaped2.txt")));
        Assert.IsFalse(File.Exists(Path.Combine(_root, "escaped3.txt")));
    }

    [TestMethod]
    public void ExtractNeverOverwritesExistingFiles()
    {
        var file = Make("keep.txt", "new");
        var zip = ZipArchiver.Compress([file], _root);
        File.WriteAllText(file, "old");

        var result = ZipArchiver.Extract(zip, _root);

        Assert.AreEqual(1, result.SkippedExisting);
        Assert.AreEqual("old", File.ReadAllText(file));
    }

    [TestMethod]
    public void ExtractSelectionCopiesFoldersAndFilesByName()
    {
        Make(@"p\docs\a.txt", "A");
        Make(@"p\docs\sub\b.txt", "B");
        Make(@"p\other.txt", "O");
        var zip = ZipArchiver.Compress([Path.Combine(_root, "p")], _root);
        var target = Path.Combine(_root, "copy");

        var result = ZipArchiver.ExtractSelection(zip, ["p/docs", "p/other.txt"], target);

        Assert.AreEqual(3, result.Extracted);
        Assert.AreEqual("B", File.ReadAllText(Path.Combine(target, "docs", "sub", "b.txt")));
        Assert.AreEqual("O", File.ReadAllText(Path.Combine(target, "other.txt")));
    }

    [TestMethod]
    public void CancelledCompressLeavesNoFile()
    {
        var file = Make("big.txt");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsExactly<OperationCanceledException>(() => ZipArchiver.Compress([file], _root, null, cts.Token));
        Assert.AreEqual(0, Directory.GetFiles(_root, "*.zip*").Length);
    }

    private static bool TryLinkDirectory(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    [TestMethod]
    public void CompressDoesNotFollowDirectoryLinks()
    {
        Make(@"outside\huge.txt", "not part of the archive");
        var proj = Path.GetDirectoryName(Make(@"proj\one.txt", "1"))!;
        if (!TryLinkDirectory(Path.Combine(proj, "link"), Path.Combine(_root, "outside")))
        {
            Assert.Inconclusive("Directory links cannot be created here.");
        }

        var dest = Path.Combine(_root, "out");
        Directory.CreateDirectory(dest);
        var zip = ZipArchiver.Compress([proj], dest);

        using var archive = ZipFile.OpenRead(zip);
        CollectionAssert.AreEquivalent(new[] { "proj/", "proj/one.txt" }, archive.Entries.Select(e => e.FullName).ToArray());
    }

    [TestMethod]
    public void CompressSurvivesALinkLoop()
    {
        var proj = Path.GetDirectoryName(Make(@"proj\one.txt", "1"))!;
        if (!TryLinkDirectory(Path.Combine(proj, "loop"), proj))
        {
            Assert.Inconclusive("Directory links cannot be created here.");
        }

        var dest = Path.Combine(_root, "out");
        Directory.CreateDirectory(dest);
        var zip = ZipArchiver.Compress([proj], dest);

        using var archive = ZipFile.OpenRead(zip);
        Assert.IsTrue(archive.Entries.Any(e => e.FullName == "proj/one.txt"));
        Assert.IsFalse(archive.Entries.Any(e => e.FullName.Contains("loop")));
    }

    [TestMethod]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public void CompressSkipsAnUnreadableSubfolderInsteadOfFailing()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Folder permissions are set with Unix modes in this test.");
        }

        var proj = Path.GetDirectoryName(Make(@"proj\one.txt", "1"))!;
        Make(@"proj\locked\secret.txt", "s");
        var locked = Path.Combine(proj, "locked");
        File.SetUnixFileMode(locked, UnixFileMode.None);
        try
        {
            if (Directory.Exists(locked) && CanList(locked))
            {
                Assert.Inconclusive("Running with rights that ignore folder permissions.");
            }

            var dest = Path.Combine(_root, "out");
            Directory.CreateDirectory(dest);
            var zip = ZipArchiver.Compress([proj], dest);

            using var archive = ZipFile.OpenRead(zip);
            Assert.IsTrue(archive.Entries.Any(e => e.FullName == "proj/one.txt"));
            Assert.IsFalse(archive.Entries.Any(e => e.FullName.EndsWith("secret.txt", StringComparison.Ordinal)));
        }
        finally
        {
            File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private const string InternetZone = "[ZoneTransfer]\r\nZoneId=3\r\nHostUrl=https://example.com/a.zip\r\n";

    /// <summary>A zip with the given (name, size) entries of zeros: deflate shrinks them about 1000:1, the shape of a zip bomb.</summary>
    private string Zeros(string zipName, params (string Name, int Size)[] entries)
    {
        var zipPath = Path.Combine(_root, zipName);
        using var fs = File.Create(zipPath);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        foreach (var (name, size) in entries)
        {
            using var w = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
            w.Write(new byte[size]);
        }

        return zipPath;
    }

    // S-10: extraction must not launder a download; every extracted file carries the archive's zone mark.
    [TestMethod]
    public void ExtractCarriesTheArchiveZoneMarkToEveryFile()
    {
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("Zone streams exist only on Windows (NTFS).");
        Make(@"p\a.txt", "A");
        Make(@"p\sub\b.txt", "B");
        var zip = ZipArchiver.Compress([Path.Combine(_root, "p")], _root);
        if (!ZoneMark.Write(zip, InternetZone)) Assert.Inconclusive("The temp folder does not support alternate data streams.");

        var target = Path.Combine(_root, "out");
        ZipArchiver.Extract(zip, target);

        Assert.AreEqual(InternetZone, ZoneMark.Read(Path.Combine(target, "p", "a.txt")));
        Assert.AreEqual(InternetZone, ZoneMark.Read(Path.Combine(target, "p", "sub", "b.txt")));
    }

    [TestMethod]
    public void ExtractFromAnUnmarkedArchiveLeavesFilesUnmarked()
    {
        var zip = ZipArchiver.Compress([Make("a.txt", "A")], _root);
        var target = Path.Combine(_root, "out");
        ZipArchiver.Extract(zip, target);

        Assert.IsNull(ZoneMark.Read(Path.Combine(target, "a.txt")));
    }

    // S-19: a zip bomb (tiny archive, huge ratio) asks first; refusing writes nothing.
    [TestMethod]
    public void ExtractAsksBeforeAZipBombAndWritesNothingWhenRefused()
    {
        var zip = Zeros("bomb.zip", ("zeros.bin", 4 << 20), ("ok.txt", 10));
        var target = Path.Combine(_root, "out");
        ExtractLimitBreach? asked = null;

        var ex = Assert.ThrowsExactly<ExtractLimitExceededException>(() =>
            ZipArchiver.Extract(zip, target, confirm: b => { asked = b; return false; }));

        Assert.IsNotNull(asked);
        Assert.AreSame(asked, ex.Breach);
        Assert.AreEqual("zeros.bin", asked.WorstEntry);
        Assert.IsGreaterThan(100.0, asked.WorstRatio);
        Assert.IsFalse(Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any());
    }

    [TestMethod]
    public void ExtractRefusesAZipBombWhenNobodyCanBeAsked()
    {
        var zip = Zeros("bomb.zip", ("zeros.bin", 4 << 20));
        var target = Path.Combine(_root, "out");

        Assert.ThrowsExactly<ExtractLimitExceededException>(() => ZipArchiver.Extract(zip, target));
        Assert.IsFalse(File.Exists(Path.Combine(target, "zeros.bin")));
    }

    [TestMethod]
    public void ExtractGoesAheadWhenTheBreachIsConfirmed()
    {
        var zip = Zeros("bomb.zip", ("zeros.bin", 4 << 20));
        var target = Path.Combine(_root, "out");

        var result = ZipArchiver.Extract(zip, target, confirm: _ => true);

        Assert.AreEqual(1, result.Extracted);
        Assert.AreEqual(4L << 20, new FileInfo(Path.Combine(target, "zeros.bin")).Length);
    }

    [TestMethod]
    public void SmallCompressibleFilesDoNotAsk()
    {
        var zip = Zeros("small.zip", ("zeros.bin", 200_000));
        var result = ZipArchiver.Extract(zip, Path.Combine(_root, "out"), confirm: _ => throw new AssertFailedException("asked"));
        Assert.AreEqual(1, result.Extracted);
    }

    [TestMethod]
    public void ExtractAsksWhenTheTotalSizeExceedsTheCeiling()
    {
        var zip = ZipArchiver.Compress([Make(@"p\a.txt", "0123456789"), Make(@"p\b.txt", "0123456789")], _root);
        ExtractLimitBreach? asked = null;

        Assert.ThrowsExactly<ExtractLimitExceededException>(() => ZipArchiver.Extract(zip, Path.Combine(_root, "out"),
            limits: ExtractLimits.Default with { MaxTotalBytes = 15 }, confirm: b => { asked = b; return false; }));

        Assert.IsNotNull(asked);
        Assert.IsTrue(asked.OverCeiling);
        Assert.IsFalse(asked.OverFreeSpace);
        Assert.AreEqual(20L, asked.TotalBytes);
    }

    [TestMethod]
    public void ExtractAsksWhenTheTotalSizeDoesNotFitTheFreeSpace()
    {
        var zip = ZipArchiver.Compress([Make("a.txt", "0123456789")], _root);
        ExtractLimitBreach? asked = null;

        // A margin larger than any disk: whatever is free, 10 bytes plus the margin do not fit.
        Assert.ThrowsExactly<ExtractLimitExceededException>(() => ZipArchiver.Extract(zip, Path.Combine(_root, "out"),
            limits: ExtractLimits.Default with { FreeSpaceMargin = long.MaxValue / 2 }, confirm: b => { asked = b; return false; }));

        Assert.IsNotNull(asked);
        Assert.IsTrue(asked.OverFreeSpace);
        Assert.IsNotNull(asked.FreeBytes);
    }

    [TestMethod]
    public void NoLimitsExtractsWithoutAsking()
    {
        var zip = Zeros("bomb.zip", ("zeros.bin", 4 << 20));
        var result = ZipArchiver.Extract(zip, Path.Combine(_root, "out"), limits: ExtractLimits.None,
            confirm: _ => throw new AssertFailedException("asked"));
        Assert.AreEqual(1, result.Extracted);
    }

    // S-19: "Extract here" into a folder that already holds a junction/symlink must not write through it.
    [TestMethod]
    public void ExtractRefusesToWriteThroughALinkInTheTarget()
    {
        var outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(outside);
        var target = Path.Combine(_root, "t");
        Directory.CreateDirectory(target);
        if (!TryLinkDirectory(Path.Combine(target, "link"), outside))
        {
            Assert.Inconclusive("Directory links cannot be created here.");
        }

        var zipPath = Path.Combine(_root, "evil.zip");
        using (var fs = File.Create(zipPath))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            zip.CreateEntry("link/");
            zip.CreateEntry("link/made/");
            foreach (var name in new[] { "link/planted.txt", "link/deep/planted2.txt", "ok/fine.txt" })
            {
                using var w = new StreamWriter(zip.CreateEntry(name).Open());
                w.Write("data");
            }
        }

        var result = ZipArchiver.Extract(zipPath, target);

        Assert.AreEqual(1, result.Extracted);
        Assert.AreEqual(4, result.SkippedUnsafe);
        Assert.IsTrue(File.Exists(Path.Combine(target, "ok", "fine.txt")));
        Assert.IsEmpty(Directory.EnumerateFileSystemEntries(outside).ToList());
    }

    [TestMethod]
    public void ExtractIntoATargetThatIsItselfALinkIsAllowed()
    {
        var real = Path.Combine(_root, "real");
        Directory.CreateDirectory(real);
        var linked = Path.Combine(_root, "linked");
        if (!TryLinkDirectory(linked, real)) Assert.Inconclusive("Directory links cannot be created here.");
        Make(@"p\a.txt", "A");
        var zip = ZipArchiver.Compress([Path.Combine(_root, "p")], _root);

        var result = ZipArchiver.Extract(zip, linked);

        Assert.AreEqual(1, result.Extracted);
        Assert.AreEqual("A", File.ReadAllText(Path.Combine(real, "p", "a.txt")));
    }

    private static bool CanList(string folder)
    {
        try
        {
            return Directory.EnumerateFileSystemEntries(folder).Any();
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
