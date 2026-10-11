using Pollux.Core.Operations;

namespace Pollux.Core.Tests.Operations;

[TestClass]
public sealed class TransferTests
{
    private DirectoryInfo _root = null!;
    private string Src => Path.Combine(_root.FullName, "src");
    private string Dst => Path.Combine(_root.FullName, "dst");

    [TestInitialize]
    public void Setup()
    {
        _root = Directory.CreateTempSubdirectory("pollux-ops-");
        Directory.CreateDirectory(Path.Combine(Src, "folder", "sub"));
        Directory.CreateDirectory(Dst);
        File.WriteAllText(Path.Combine(Src, "a.txt"), "aaa");
        File.WriteAllText(Path.Combine(Src, "folder", "b.txt"), "bbbb");
        File.WriteAllBytes(Path.Combine(Src, "folder", "sub", "big.bin"), new byte[3 * 1024 * 1024]);
    }

    [TestCleanup]
    public void Cleanup() => _root.Delete(true);

    private static Task<ConflictDecision> Always(ConflictChoice choice) => Task.FromResult(new ConflictDecision(choice, ApplyToAll: true));

    private static async Task<TransferJob> Run(TransferKind kind, IReadOnlyList<string> sources, string target, ConflictPolicy policy = ConflictPolicy.Ask, Func<TransferConflict, Task<ConflictDecision>>? ask = null)
    {
        var job = new TransferJob(new TransferRequest(kind, sources, target, policy));
        await TransferRunner.RunAsync(job, ask ?? (_ => Always(ConflictChoice.Skip)), CancellationToken.None);
        return job;
    }

    [TestMethod]
    public async Task CopiesFilesAndFolderTreesWithProgress()
    {
        var job = await Run(TransferKind.Copy, [Path.Combine(Src, "a.txt"), Path.Combine(Src, "folder")], Dst);

        Assert.AreEqual(TransferState.Completed, job.State);
        Assert.AreEqual("aaa", File.ReadAllText(Path.Combine(Dst, "a.txt")));
        Assert.AreEqual(3 * 1024 * 1024, new FileInfo(Path.Combine(Dst, "folder", "sub", "big.bin")).Length);
        Assert.AreEqual(3, job.Progress.TotalFiles);
        Assert.AreEqual(job.Progress.TotalBytes, job.Progress.DoneBytes);
        Assert.IsTrue(File.Exists(Path.Combine(Src, "a.txt")), "copy keeps the source");
    }

    [TestMethod]
    public async Task MoveRemovesSources()
    {
        var job = await Run(TransferKind.Move, [Path.Combine(Src, "folder")], Dst);

        Assert.AreEqual(TransferState.Completed, job.State);
        Assert.IsTrue(File.Exists(Path.Combine(Dst, "folder", "b.txt")));
        Assert.IsFalse(Directory.Exists(Path.Combine(Src, "folder")));
    }

    [TestMethod]
    public async Task ConflictPoliciesSkipOverwriteRename()
    {
        File.WriteAllText(Path.Combine(Dst, "a.txt"), "old");

        await Run(TransferKind.Copy, [Path.Combine(Src, "a.txt")], Dst, ConflictPolicy.Skip);
        Assert.AreEqual("old", File.ReadAllText(Path.Combine(Dst, "a.txt")));

        await Run(TransferKind.Copy, [Path.Combine(Src, "a.txt")], Dst, ConflictPolicy.Rename);
        Assert.AreEqual("aaa", File.ReadAllText(Path.Combine(Dst, "a (2).txt")));

        await Run(TransferKind.Copy, [Path.Combine(Src, "a.txt")], Dst, ConflictPolicy.Overwrite);
        Assert.AreEqual("aaa", File.ReadAllText(Path.Combine(Dst, "a.txt")));
    }

    [TestMethod]
    public async Task AskWithApplyToAllIsAskedOnce()
    {
        File.WriteAllText(Path.Combine(Dst, "a.txt"), "old");
        Directory.CreateDirectory(Path.Combine(Dst, "folder"));
        File.WriteAllText(Path.Combine(Dst, "folder", "b.txt"), "old");
        var asked = 0;

        var job = await Run(TransferKind.Copy, [Path.Combine(Src, "a.txt"), Path.Combine(Src, "folder")], Dst, ConflictPolicy.Ask, c =>
        {
            asked++;
            Assert.IsTrue(c.TargetPath.EndsWith("a.txt", StringComparison.Ordinal));
            return Always(ConflictChoice.Overwrite);
        });

        Assert.AreEqual(1, asked);
        Assert.AreEqual("bbbb", File.ReadAllText(Path.Combine(Dst, "folder", "b.txt")));
        Assert.AreEqual(TransferState.Completed, job.State);
    }

    [TestMethod]
    public async Task CancelFromConflictStopsAndLeavesNoPartialFile()
    {
        File.WriteAllText(Path.Combine(Dst, "a.txt"), "old");

        var job = await Run(TransferKind.Copy, [Path.Combine(Src, "a.txt"), Path.Combine(Src, "folder")], Dst, ConflictPolicy.Ask, _ => Always(ConflictChoice.Cancel));

        Assert.AreEqual(TransferState.Cancelled, job.State);
        Assert.IsFalse(Directory.Exists(Path.Combine(Dst, "folder")));
    }

    [TestMethod]
    public async Task CancellationDuringCopyDeletesPartialFile()
    {
        var job = new TransferJob(new TransferRequest(TransferKind.Copy, [Path.Combine(Src, "folder")], Dst, ConflictPolicy.Skip));
        using var cts = new CancellationTokenSource();
        job.ProgressChanged += () => { if (job.Progress.DoneBytes > 0) cts.Cancel(); };

        await TransferRunner.RunAsync(job, _ => Always(ConflictChoice.Skip), cts.Token, chunkSize: 64 * 1024);

        Assert.AreEqual(TransferState.Cancelled, job.State);
        Assert.IsFalse(File.Exists(Path.Combine(Dst, "folder", "sub", "big.bin")), "partial file must be removed");
    }

    [TestMethod]
    public async Task CopyIntoSameFolderMakesACopy()
    {
        var job = await Run(TransferKind.Copy, [Path.Combine(Src, "a.txt")], Src);

        Assert.AreEqual(TransferState.Completed, job.State);
        Assert.AreEqual("aaa", File.ReadAllText(Path.Combine(Src, "a - Copy.txt")));
    }

    [TestMethod]
    public async Task FolderIntoItselfIsRefused()
    {
        var job = await Run(TransferKind.Copy, [Path.Combine(Src, "folder")], Path.Combine(Src, "folder", "sub"));

        Assert.AreEqual(TransferState.Failed, job.State);
        StringAssert.Contains(job.Error, "into itself");
    }

    [TestMethod]
    public async Task PauseHoldsTheJobUntilResumed()
    {
        var job = new TransferJob(new TransferRequest(TransferKind.Copy, [Path.Combine(Src, "folder")], Dst, ConflictPolicy.Skip));
        job.Pause();
        var run = TransferRunner.RunAsync(job, _ => Always(ConflictChoice.Skip), CancellationToken.None, chunkSize: 64 * 1024);

        await Task.Delay(200);
        Assert.AreEqual(TransferState.Paused, job.State);
        Assert.AreEqual(0, job.Progress.DoneBytes);

        job.Resume();
        await run;
        Assert.AreEqual(TransferState.Completed, job.State);
    }

    [TestMethod]
    public async Task FolderOntoExistingFileKeepsBoth()
    {
        File.WriteAllText(Path.Combine(Dst, "folder"), "i am a file");

        var job = await Run(TransferKind.Copy, [Path.Combine(Src, "folder")], Dst, ConflictPolicy.Overwrite);

        Assert.AreEqual(TransferState.Completed, job.State);
        Assert.AreEqual("i am a file", File.ReadAllText(Path.Combine(Dst, "folder")), "the existing file must survive");
        Assert.AreEqual("bbbb", File.ReadAllText(Path.Combine(Dst, "folder (2)", "b.txt")));
        Assert.IsTrue(File.Exists(Path.Combine(Dst, "folder (2)", "sub", "big.bin")));
    }

    [TestMethod]
    public async Task FileOntoExistingFolderKeepsBoth()
    {
        Directory.CreateDirectory(Path.Combine(Dst, "a.txt"));
        File.WriteAllText(Path.Combine(Dst, "a.txt", "inside.txt"), "keep me");

        var job = await Run(TransferKind.Copy, [Path.Combine(Src, "a.txt")], Dst, ConflictPolicy.Overwrite);

        Assert.AreEqual(TransferState.Completed, job.State);
        Assert.AreEqual("keep me", File.ReadAllText(Path.Combine(Dst, "a.txt", "inside.txt")), "the existing folder must survive");
        Assert.AreEqual("aaa", File.ReadAllText(Path.Combine(Dst, "a (2).txt")));
    }

    [TestMethod]
    public async Task CancellingAReplaceKeepsTheOriginal()
    {
        Directory.CreateDirectory(Path.Combine(Dst, "sub"));
        File.WriteAllText(Path.Combine(Dst, "sub", "big.bin"), "original");
        var job = new TransferJob(new TransferRequest(TransferKind.Copy, [Path.Combine(Src, "folder", "sub")], Dst, ConflictPolicy.Overwrite));
        using var cts = new CancellationTokenSource();
        job.ProgressChanged += () => { if (job.Progress.DoneBytes > 0) cts.Cancel(); };

        await TransferRunner.RunAsync(job, _ => Always(ConflictChoice.Skip), cts.Token, chunkSize: 64 * 1024);

        Assert.AreEqual(TransferState.Cancelled, job.State);
        Assert.AreEqual("original", File.ReadAllText(Path.Combine(Dst, "sub", "big.bin")));
        Assert.AreEqual(0, Directory.GetFiles(Path.Combine(Dst, "sub"), "*.pollux-part").Length, "no temp files left");
    }

    [TestMethod]
    public async Task KeepsLastWriteTime()
    {
        var src = Path.Combine(Src, "a.txt");
        File.SetLastWriteTimeUtc(src, new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc));

        await Run(TransferKind.Copy, [src], Dst);

        Assert.AreEqual(new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc), File.GetLastWriteTimeUtc(Path.Combine(Dst, "a.txt")));
    }

    [TestMethod]
    public async Task InaccessibleSubfolderIsReportedAndMoveKeepsIt()
    {
        if (OperatingSystem.IsWindows()) return; // permission setup below is POSIX-only; Windows ACLs are covered by manual testing
        var locked = Path.Combine(Src, "folder", "locked");
        Directory.CreateDirectory(locked);
        File.WriteAllText(Path.Combine(locked, "secret.txt"), "s");
        File.SetUnixFileMode(locked, UnixFileMode.None);
        try
        {
            if (CanList(locked)) Assert.Inconclusive("Running with rights that ignore folder permissions (root in a CI container).");
            var job = await Run(TransferKind.Copy, [Path.Combine(Src, "folder")], Dst, ConflictPolicy.Skip);

            Assert.AreEqual(TransferState.Completed, job.State);
            Assert.IsTrue(job.Errors.Any(e => e.Contains("locked", StringComparison.Ordinal)), "the unreadable folder must be reported");
            Assert.IsTrue(File.Exists(Path.Combine(Dst, "folder", "b.txt")), "the rest is still copied");
        }
        finally
        {
            File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [TestMethod]
    public async Task UnreadableSourceIsReportedAndOthersContinue()
    {
        var job = await Run(TransferKind.Copy, [Path.Combine(Src, "missing.txt"), Path.Combine(Src, "a.txt")], Dst);

        Assert.AreEqual(TransferState.Completed, job.State);
        Assert.AreEqual(1, job.Errors.Count);
        Assert.IsTrue(File.Exists(Path.Combine(Dst, "a.txt")));
    }

    private const string InternetZone = "[ZoneTransfer]\r\nZoneId=3\r\nHostUrl=https://example.invalid/a.txt\r\n";

    [TestMethod]
    public async Task CopyKeepsTheInternetZoneMark()
    {
        // S-10: a downloaded file copied by Pollux's engine must keep its Mark-of-the-Web.
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("Zone streams exist only on Windows (NTFS).");
        var src = Path.Combine(Src, "a.txt");
        if (!ZoneMark.Write(src, InternetZone)) Assert.Inconclusive("The temp folder does not support alternate data streams.");

        var job = await Run(TransferKind.Copy, [src], Dst);

        Assert.AreEqual(TransferState.Completed, job.State);
        Assert.AreEqual(InternetZone, ZoneMark.Read(Path.Combine(Dst, "a.txt")));
    }

    [TestMethod]
    public async Task OverwriteKeepsTheSourceZoneMark()
    {
        // S-10: replacing an existing file must carry the source's mark onto the replacement.
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("Zone streams exist only on Windows (NTFS).");
        var src = Path.Combine(Src, "a.txt");
        if (!ZoneMark.Write(src, InternetZone)) Assert.Inconclusive("The temp folder does not support alternate data streams.");
        File.SetAttributes(src, FileAttributes.ReadOnly);
        File.WriteAllText(Path.Combine(Dst, "a.txt"), "old");

        var job = await Run(TransferKind.Copy, [src], Dst, ConflictPolicy.Overwrite);

        File.SetAttributes(src, FileAttributes.Normal);
        File.SetAttributes(Path.Combine(Dst, "a.txt"), FileAttributes.Normal);
        Assert.AreEqual(TransferState.Completed, job.State);
        Assert.AreEqual("aaa", File.ReadAllText(Path.Combine(Dst, "a.txt")));
        Assert.AreEqual(InternetZone, ZoneMark.Read(Path.Combine(Dst, "a.txt")));
    }

    [TestMethod]
    public async Task UnmarkedSourceStaysUnmarked()
    {
        var job = await Run(TransferKind.Copy, [Path.Combine(Src, "a.txt")], Dst);

        Assert.AreEqual(TransferState.Completed, job.State);
        Assert.IsNull(ZoneMark.Read(Path.Combine(Dst, "a.txt")));
    }

    [TestMethod]
    public void ZoneMarkIsANoOpOffWindows()
    {
        if (OperatingSystem.IsWindows()) Assert.Inconclusive("Covers non-Windows behaviour only.");
        var file = Path.Combine(Src, "a.txt");

        Assert.IsFalse(ZoneMark.Write(file, InternetZone));
        Assert.IsNull(ZoneMark.Read(file));
        Assert.AreEqual(1, Directory.GetFiles(Src).Length, "no stray 'a.txt:Zone.Identifier' file may appear");
    }

    [TestMethod]
    public void EncryptedSourceIsCopiedThroughTheWindowsCopyApi()
    {
        // S-10: a streamed copy of an EFS file would produce a plaintext duplicate.
        Assert.IsTrue(TransferRunner.UsesWindowsCopy(FileAttributes.Encrypted | FileAttributes.Archive, isWindows: true));
        Assert.IsFalse(TransferRunner.UsesWindowsCopy(FileAttributes.Archive, isWindows: true));
        Assert.IsFalse(TransferRunner.UsesWindowsCopy(FileAttributes.Encrypted, isWindows: false));
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
