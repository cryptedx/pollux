using Pollux.Core.Operations;

namespace Pollux.Core.Tests.Operations;

[TestClass]
public sealed class UndoHistoryTests
{
    private DirectoryInfo _root = null!;
    private FakeRecycleBin _bin = null!;
    private DiskUndoTarget _target = null!;
    private string Src => Path.Combine(_root.FullName, "src");
    private string Dst => Path.Combine(_root.FullName, "dst");

    [TestInitialize]
    public void Setup()
    {
        _root = Directory.CreateTempSubdirectory("pollux-undo-");
        _bin = new FakeRecycleBin(Path.Combine(_root.FullName, "$bin"));
        _target = new DiskUndoTarget(_bin.Recycle, _ => _bin.Folder);
        Directory.CreateDirectory(Path.Combine(Src, "folder", "sub"));
        Directory.CreateDirectory(Dst);
        File.WriteAllText(Path.Combine(Src, "a.txt"), "aaa");
        File.WriteAllText(Path.Combine(Src, "folder", "b.txt"), "bbbb");
        File.WriteAllText(Path.Combine(Src, "folder", "sub", "c.txt"), "cc");
    }

    [TestCleanup]
    public void Cleanup() => _root.Delete(true);

    private string S(params string[] parts) => Path.Combine([Src, .. parts]);
    private string D(params string[] parts) => Path.Combine([Dst, .. parts]);
    private int BinCount => Directory.Exists(_bin.Folder) ? Directory.GetFiles(_bin.Folder, "$I*").Length : 0;

    private static async Task<TransferJob> Run(TransferKind kind, IReadOnlyList<string> sources, string target, ConflictPolicy policy = ConflictPolicy.Skip)
    {
        var job = new TransferJob(new TransferRequest(kind, sources, target, policy));
        await TransferRunner.RunAsync(job, _ => Task.FromResult(new ConflictDecision(ConflictChoice.Skip, true)), CancellationToken.None);
        return job;
    }

    // ---------------- history ----------------

    [TestMethod]
    public void NothingToUndoOrRedo()
    {
        var history = new UndoHistory();

        Assert.IsFalse(history.CanUndo);
        Assert.AreEqual(new UndoResult(false, "Nothing to undo"), history.Undo(_target));
        Assert.AreEqual(new UndoResult(false, "Nothing to redo"), history.Redo(_target));
    }

    [TestMethod]
    public void RenameIsUndoneAndRedone()
    {
        var history = new UndoHistory();
        File.Move(S("a.txt"), S("b.txt"));
        history.Record(UndoEntry.Rename(S("a.txt"), S("b.txt")));

        var undo = history.Undo(_target);
        Assert.IsTrue(undo.Ok, undo.Message);
        Assert.AreEqual("Undone: Rename a.txt → b.txt", undo.Message);
        Assert.IsTrue(File.Exists(S("a.txt")));
        Assert.IsFalse(File.Exists(S("b.txt")));
        Assert.IsTrue(history.CanRedo);

        var redo = history.Redo(_target);
        Assert.AreEqual("Redone: Rename a.txt → b.txt", redo.Message);
        Assert.IsTrue(File.Exists(S("b.txt")));
        Assert.IsTrue(history.CanUndo);
        Assert.IsFalse(history.CanRedo);
    }

    [TestMethod]
    public void UndoRefusesWhenTheDestinationIsTakenAndDropsTheEntry()
    {
        var history = new UndoHistory();
        File.Move(S("a.txt"), S("b.txt"));
        history.Record(UndoEntry.Rename(S("a.txt"), S("b.txt")));
        File.WriteAllText(S("a.txt"), "new");

        var undo = history.Undo(_target);

        Assert.IsFalse(undo.Ok);
        StringAssert.StartsWith(undo.Message, "Cannot undo Rename a.txt → b.txt: ");
        StringAssert.Contains(undo.Message, "already exists");
        Assert.AreEqual("new", File.ReadAllText(S("a.txt")));
        Assert.IsTrue(File.Exists(S("b.txt")));
        Assert.IsFalse(history.CanUndo, "a refused entry would block every older one");
    }

    [TestMethod]
    public void UndoRefusesWhenTheEntryIsGone()
    {
        var history = new UndoHistory();
        history.Record(UndoEntry.Rename(S("x.txt"), S("a2.txt")));

        var undo = history.Undo(_target);

        Assert.IsFalse(undo.Ok);
        StringAssert.Contains(undo.Message, "a2.txt no longer exists");
    }

    [TestMethod]
    public void HistoryIsBoundedAndANewEntryClearsRedo()
    {
        var history = new UndoHistory(capacity: 2);
        for (var i = 0; i < 4; i++)
        {
            var folder = S($"n{i}");
            Directory.CreateDirectory(folder);
            history.Record(UndoEntry.NewFolder(folder));
        }

        Assert.IsTrue(history.Undo(_target).Ok);
        Assert.IsTrue(history.Undo(_target).Ok);
        Assert.IsFalse(history.CanUndo);
        Assert.IsTrue(Directory.Exists(S("n1")), "the oldest entries fell out");
        Assert.IsFalse(Directory.Exists(S("n3")));

        Directory.CreateDirectory(S("n9"));
        history.Record(UndoEntry.NewFolder(S("n9")));
        Assert.IsFalse(history.CanRedo);
    }

    [TestMethod]
    public void IgnoresNullAndEmptyEntries()
    {
        var history = new UndoHistory();
        history.Record(null);
        history.Record(new UndoEntry("nothing", []));

        Assert.IsFalse(history.CanUndo);
    }

    [TestMethod]
    public void AFailingStepRollsBackAndKeepsTheEntry()
    {
        var history = new UndoHistory();
        File.Move(S("a.txt"), D("a.txt"));
        File.WriteAllText(D("blocker"), "a file where a folder is needed");
        File.WriteAllText(D("z.txt"), "z");
        history.Record(new UndoEntry("Two moves", [UndoStep.Move(D("a.txt"), S("a.txt")), UndoStep.Move(D("z.txt"), D("blocker", "z.txt"))]));

        var undo = history.Undo(_target);

        Assert.IsFalse(undo.Ok);
        StringAssert.StartsWith(undo.Message, "Cannot undo Two moves: ");
        Assert.IsTrue(File.Exists(D("a.txt")), "the first move was rolled back");
        Assert.IsFalse(File.Exists(S("a.txt")));
        Assert.IsTrue(history.CanUndo, "a failure (unlike a refusal) may work on a retry");
    }

    // ---------------- new file / folder ----------------

    [TestMethod]
    public void NewFileGoesToTheRecycleBinAndComesBack()
    {
        var history = new UndoHistory();
        var path = S("new.txt");
        File.WriteAllText(path, "");
        history.Record(UndoEntry.NewFile(path, _target));

        Assert.AreEqual("Undone: New file new.txt", history.Undo(_target).Message);
        Assert.IsFalse(File.Exists(path));
        Assert.AreEqual(1, BinCount);

        Assert.IsTrue(history.Redo(_target).Ok);
        Assert.IsTrue(File.Exists(path));
        Assert.AreEqual(0, BinCount);
    }

    [TestMethod]
    public void ChangedNewFileIsNotRemoved()
    {
        var history = new UndoHistory();
        var path = S("new.txt");
        File.WriteAllText(path, "");
        history.Record(UndoEntry.NewFile(path, _target));
        File.WriteAllText(path, "typed something");

        var undo = history.Undo(_target);

        Assert.IsFalse(undo.Ok);
        StringAssert.Contains(undo.Message, "new.txt was changed");
        Assert.IsTrue(File.Exists(path));
    }

    [TestMethod]
    public void NewFolderIsRemovedOnlyWhileEmpty()
    {
        var history = new UndoHistory();
        Directory.CreateDirectory(S("empty"));
        history.Record(UndoEntry.NewFolder(S("empty")));
        Directory.CreateDirectory(S("full"));
        history.Record(UndoEntry.NewFolder(S("full")));
        File.WriteAllText(S("full", "x.txt"), "x");

        var refused = history.Undo(_target);
        Assert.IsFalse(refused.Ok);
        StringAssert.Contains(refused.Message, "full is no longer empty");
        Assert.IsTrue(File.Exists(S("full", "x.txt")));

        Assert.AreEqual("Undone: New folder empty", history.Undo(_target).Message);
        Assert.IsFalse(Directory.Exists(S("empty")));
        Assert.IsTrue(history.Redo(_target).Ok);
        Assert.IsTrue(Directory.Exists(S("empty")));
    }

    // ---------------- delete to the recycle bin ----------------

    [TestMethod]
    public void RecycledEntriesAreRestoredAndRecycledAgain()
    {
        var history = new UndoHistory();
        var since = DateTime.UtcNow;
        _bin.Recycle([S("a.txt"), S("folder")]);
        history.Record(UndoEntry.Recycle([S("a.txt"), S("folder")], since, _target));

        Assert.AreEqual("Undone: Delete 2 items", history.Undo(_target).Message);
        Assert.AreEqual("aaa", File.ReadAllText(S("a.txt")));
        Assert.AreEqual("cc", File.ReadAllText(S("folder", "sub", "c.txt")));

        Assert.IsTrue(history.Redo(_target).Ok);
        Assert.IsFalse(File.Exists(S("a.txt")));
        Assert.AreEqual(2, BinCount);

        Assert.IsTrue(history.Undo(_target).Ok, "the redo's own deletion is found again");
        Assert.IsTrue(File.Exists(S("a.txt")));
    }

    [TestMethod]
    public void OnlyWhatReachedTheRecycleBinIsRecorded()
    {
        var since = DateTime.UtcNow;
        _bin.Recycle([S("a.txt")]);
        File.Delete(S("folder", "b.txt")); // "deleted permanently" (no recycle bin on that drive)

        var entry = UndoEntry.Recycle([S("a.txt"), S("folder", "b.txt"), S("folder")], since, _target);

        Assert.IsNotNull(entry);
        Assert.AreEqual("Delete a.txt", entry.Label);
        Assert.AreEqual(1, entry.Steps.Count);
        Assert.IsNull(UndoEntry.Recycle([S("folder", "b.txt")], since, _target));
    }

    [TestMethod]
    public void RestoreRefusesWhenTheOriginalPathIsTakenOrTheBinWasEmptied()
    {
        var history = new UndoHistory();
        var since = DateTime.UtcNow;
        _bin.Recycle([S("a.txt")]);
        history.Record(UndoEntry.Recycle([S("a.txt")], since, _target));
        File.WriteAllText(S("a.txt"), "new");

        StringAssert.Contains(history.Undo(_target).Message, "a.txt already exists");
        Assert.AreEqual("new", File.ReadAllText(S("a.txt")));

        File.Delete(S("a.txt"));
        _bin.Recycle([S("folder")]);
        history.Record(UndoEntry.Recycle([S("folder")], since, _target));
        Directory.Delete(_bin.Folder, true); // emptied
        StringAssert.Contains(history.Undo(_target).Message, "is no longer in the Recycle Bin");
    }

    // ---------------- copies and moves of the Transfer queue ----------------

    [TestMethod]
    public async Task UndoingACopyRemovesTheCopiesOnly()
    {
        var history = new UndoHistory();
        var job = await Run(TransferKind.Copy, [S("a.txt"), S("folder")], Dst);
        var entry = UndoEntry.Transfer(job);

        Assert.IsNotNull(entry);
        Assert.AreEqual(5, entry.Steps.Count, "a.txt, b.txt and c.txt are recycled, then both copied folders removed");
        history.Record(entry);
        Assert.AreEqual("Undone: Copy 2 items → dst", history.Undo(_target).Message);
        Assert.AreEqual(0, Directory.GetFileSystemEntries(Dst).Length);
        Assert.IsTrue(File.Exists(S("folder", "sub", "c.txt")), "sources are untouched");

        Assert.IsTrue(history.Redo(_target).Ok);
        Assert.AreEqual("cc", File.ReadAllText(D("folder", "sub", "c.txt")));
    }

    [TestMethod]
    public async Task UndoingACopyIntoAnExistingFolderKeepsWhatWasThere()
    {
        Directory.CreateDirectory(D("folder"));
        File.WriteAllText(D("folder", "keep.txt"), "mine");
        File.WriteAllText(D("folder", "b.txt"), "old");
        var history = new UndoHistory();

        history.Record(UndoEntry.Transfer(await Run(TransferKind.Copy, [S("folder")], Dst, ConflictPolicy.Overwrite)));
        Assert.AreEqual("bbbb", File.ReadAllText(D("folder", "b.txt")));
        Assert.IsTrue(history.Undo(_target).Ok);

        Assert.AreEqual("mine", File.ReadAllText(D("folder", "keep.txt")));
        Assert.IsTrue(File.Exists(D("folder", "b.txt")), "a replaced file cannot get its old content back, so it stays");
        Assert.IsFalse(Directory.Exists(D("folder", "sub")));
    }

    [TestMethod]
    public async Task UndoingAFolderCopyKeepsFilesSavedIntoItSince()
    {
        var history = new UndoHistory();
        history.Record(UndoEntry.Transfer(await Run(TransferKind.Copy, [S("folder")], Dst)));
        File.WriteAllText(D("folder", "mine.txt"), "saved after the copy");

        var undo = history.Undo(_target);

        Assert.IsTrue(undo.Ok, undo.Message);
        Assert.AreEqual("saved after the copy", File.ReadAllText(D("folder", "mine.txt")));
        Assert.IsFalse(File.Exists(D("folder", "b.txt")), "the copied files are gone");
        Assert.IsFalse(File.Exists(D("folder", "sub", "c.txt")));
        Assert.IsFalse(Directory.Exists(D("folder", "sub")), "the copied folder that ended up empty is removed");
        Assert.AreEqual(2, BinCount, "only the copied files went to the Recycle Bin");
    }

    [TestMethod]
    public async Task UndoingAFolderCopyRefusesWhenACopiedFileWasEdited()
    {
        var history = new UndoHistory();
        history.Record(UndoEntry.Transfer(await Run(TransferKind.Copy, [S("folder")], Dst)));
        File.WriteAllText(D("folder", "b.txt"), "edited after the copy");

        var undo = history.Undo(_target);

        Assert.IsFalse(undo.Ok);
        StringAssert.Contains(undo.Message, "b.txt was changed");
        Assert.IsTrue(File.Exists(D("folder", "sub", "c.txt")));
    }

    // ---------------- batch rename ----------------

    [TestMethod]
    public void BatchRenameIsUndoneAndRedoneEvenWhenItSwappedNames()
    {
        var history = new UndoHistory();
        File.WriteAllText(S("b.txt"), "second");
        // a <-> b through a temporary name, c.txt is renamed on the side.
        File.Move(S("a.txt"), S(".tmp"));
        File.Move(S("b.txt"), S("a.txt"));
        File.Move(S(".tmp"), S("b.txt"));
        var executed = new List<Pollux.Core.Files.RenameStep> { new("a.txt", ".tmp"), new("b.txt", "a.txt"), new(".tmp", "b.txt") };
        history.Record(UndoEntry.BatchRename(Src, executed, 2));
        Assert.AreEqual("second", File.ReadAllText(S("a.txt")));

        var undo = history.Undo(_target);

        Assert.IsTrue(undo.Ok, undo.Message);
        Assert.AreEqual("Undone: Rename 2 items", undo.Message);
        Assert.AreEqual("aaa", File.ReadAllText(S("a.txt")));
        Assert.AreEqual("second", File.ReadAllText(S("b.txt")));
        Assert.IsFalse(File.Exists(S(".tmp")));

        Assert.IsTrue(history.Redo(_target).Ok);
        Assert.AreEqual("second", File.ReadAllText(S("a.txt")));
        Assert.AreEqual("aaa", File.ReadAllText(S("b.txt")));
    }

    [TestMethod]
    public void BatchRenameUndoRefusesWhenAFileIsGone()
    {
        var history = new UndoHistory();
        File.Move(S("a.txt"), S("x.txt"));
        history.Record(UndoEntry.BatchRename(Src, [new("a.txt", "x.txt")], 1));
        File.Delete(S("x.txt"));

        var undo = history.Undo(_target);

        Assert.IsFalse(undo.Ok);
        StringAssert.Contains(undo.Message, "x.txt no longer exists");
    }

    [TestMethod]
    public void BatchRenameWithoutStepsRecordsNothing() => Assert.IsNull(UndoEntry.BatchRename(Src, [], 0));

    [TestMethod]
    public async Task CopyIntoTheSameFolderUndoesTheCopyNotTheOriginal()
    {
        var history = new UndoHistory();
        history.Record(UndoEntry.Transfer(await Run(TransferKind.Copy, [S("a.txt")], Src)));
        Assert.IsTrue(File.Exists(S("a - Copy.txt")));

        Assert.IsTrue(history.Undo(_target).Ok);

        Assert.IsTrue(File.Exists(S("a.txt")));
        Assert.IsFalse(File.Exists(S("a - Copy.txt")));
    }

    [TestMethod]
    public async Task ChangedCopyIsNotRemoved()
    {
        var history = new UndoHistory();
        history.Record(UndoEntry.Transfer(await Run(TransferKind.Copy, [S("a.txt")], Dst)));
        File.WriteAllText(D("a.txt"), "edited after the copy");

        var undo = history.Undo(_target);

        Assert.IsFalse(undo.Ok);
        StringAssert.Contains(undo.Message, "a.txt was changed");
        Assert.IsTrue(File.Exists(D("a.txt")));
    }

    [TestMethod]
    public async Task UndoingAMoveMovesBackAndRedoMovesAgain()
    {
        var history = new UndoHistory();
        history.Record(UndoEntry.Transfer(await Run(TransferKind.Move, [S("a.txt"), S("folder")], Dst)));
        Assert.IsFalse(Directory.Exists(S("folder")));

        Assert.AreEqual("Undone: Move 2 items → dst", history.Undo(_target).Message);
        Assert.AreEqual("aaa", File.ReadAllText(S("a.txt")));
        Assert.AreEqual("cc", File.ReadAllText(S("folder", "sub", "c.txt")));
        Assert.AreEqual(0, Directory.GetFileSystemEntries(Dst).Length);

        Assert.IsTrue(history.Redo(_target).Ok);
        Assert.IsTrue(File.Exists(D("folder", "b.txt")));
        Assert.IsFalse(File.Exists(S("a.txt")));
    }

    [TestMethod]
    public async Task UndoingAMergingMoveRebuildsTheSourceAndKeepsTheExistingTarget()
    {
        Directory.CreateDirectory(D("folder"));
        File.WriteAllText(D("folder", "keep.txt"), "mine");
        Directory.CreateDirectory(S("folder", "empty"));
        var history = new UndoHistory();
        history.Record(UndoEntry.Transfer(await Run(TransferKind.Move, [S("folder")], Dst)));
        Assert.IsFalse(Directory.Exists(S("folder")));

        Assert.IsTrue(history.Undo(_target).Ok);

        Assert.AreEqual("bbbb", File.ReadAllText(S("folder", "b.txt")));
        Assert.AreEqual("cc", File.ReadAllText(S("folder", "sub", "c.txt")));
        Assert.IsTrue(Directory.Exists(S("folder", "empty")));
        CollectionAssert.AreEqual(new[] { D("folder", "keep.txt") }, Directory.GetFileSystemEntries(D("folder")));
    }

    [TestMethod]
    public async Task UndoingAMoveRefusesWhenTheOriginalPlaceIsTaken()
    {
        var history = new UndoHistory();
        history.Record(UndoEntry.Transfer(await Run(TransferKind.Move, [S("a.txt")], Dst)));
        File.WriteAllText(S("a.txt"), "someone else");

        var undo = history.Undo(_target);

        Assert.IsFalse(undo.Ok);
        StringAssert.Contains(undo.Message, "a.txt already exists");
        Assert.IsTrue(File.Exists(D("a.txt")));
    }

    [TestMethod]
    public async Task ATransferThatDidNothingIsNotRecorded()
    {
        File.WriteAllText(D("a.txt"), "old");

        Assert.IsNull(UndoEntry.Transfer(await Run(TransferKind.Copy, [S("a.txt")], Dst, ConflictPolicy.Skip)));
    }

    [TestMethod]
    public async Task FollowsFinishedJobsOfTheQueue()
    {
        var history = new UndoHistory();
        var queue = new Transfers();
        history.Follow(queue);

        queue.Enqueue(new TransferRequest(TransferKind.Copy, [S("a.txt")], Dst), _ => Task.FromResult(new ConflictDecision(ConflictChoice.Skip, true)));
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!history.CanUndo && DateTime.UtcNow < deadline) await Task.Delay(20);

        Assert.AreEqual("Copy a.txt → dst", history.UndoLabel);
    }

    // ---------------- copies and moves through the Windows shell ----------------

    [TestMethod]
    public void ShellCopyRecordsOnlyNewEntriesUnderTheirRealNames()
    {
        // The shell copied a.txt into its own folder as "a - Copy.txt", and folder into dst where "folder" existed (merged).
        File.WriteAllText(S("a - Copy.txt"), "aaa");
        Directory.CreateDirectory(D("folder"));
        var before = new HashSet<string>([S("a.txt"), D("folder")], StringComparer.OrdinalIgnoreCase);

        var copyHere = UndoEntry.ShellTransfer(TransferKind.Copy, [S("a.txt")], Src, [(S("a.txt"), S("a - Copy.txt"))], before.Contains, _target);
        var merged = UndoEntry.ShellTransfer(TransferKind.Copy, [S("folder")], Dst, [], before.Contains, _target);

        Assert.IsNotNull(copyHere);
        Assert.AreEqual(UndoStep.Recycle(S("a - Copy.txt"), FileStamp.Of(S("a - Copy.txt"))), copyHere.Steps.Single());
        Assert.IsNull(merged, "a merge into an existing folder cannot be told apart from what was there");
    }

    [TestMethod]
    public void ShellMoveRecordsTheWayBack()
    {
        File.Move(S("a.txt"), D("a.txt"));
        var history = new UndoHistory();

        history.Record(UndoEntry.ShellTransfer(TransferKind.Move, [S("a.txt"), S("folder")], Dst, [], _ => false, _target));

        Assert.AreEqual("Move 2 items → dst", history.UndoLabel);
        Assert.IsTrue(history.Undo(_target).Ok, "folder was not moved (skipped), so only a.txt goes back");
        Assert.IsTrue(File.Exists(S("a.txt")));
    }
}
