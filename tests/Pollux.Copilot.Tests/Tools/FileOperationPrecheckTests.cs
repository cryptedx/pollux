using Pollux.Copilot.Tools;

namespace Pollux.Copilot.Tests.Tools;

/// <summary>What stops a file operation before it starts, judged from which files and folders exist.</summary>
[TestClass]
public sealed class FileOperationPrecheckTests
{
    private static FileOperation Op(FileOperationKind kind, string[] paths, string? destination = null, string? name = null) =>
        FileOperation.Create(kind, paths, destination, name, null, out _)!;

    private static string? Precheck(FileOperation op, string[] files, string[] folders) =>
        op.Precheck(p => files.Contains(p, StringComparer.Ordinal), p => folders.Contains(p, StringComparer.Ordinal));

    [TestMethod]
    public void AMissingSourceStopsTheOperation()
    {
        var op = Op(FileOperationKind.Recycle, [@"C:\a.txt", @"C:\b.txt"]);

        Assert.AreEqual(@"C:\b.txt does not exist.", Precheck(op, [@"C:\a.txt"], []));
    }

    [TestMethod]
    public void ASourceMayBeAFileOrAFolder()
    {
        var op = Op(FileOperationKind.Recycle, [@"C:\a.txt", @"C:\dir"]);

        Assert.IsNull(Precheck(op, [@"C:\a.txt"], [@"C:\dir"]));
    }

    [TestMethod]
    public void ANewFolderNeedsAnExistingParentFolder()
    {
        var op = Op(FileOperationKind.NewFolder, [@"C:\parent"], name: "x");

        Assert.AreEqual(@"The folder C:\parent does not exist.", Precheck(op, [], []));
        Assert.AreEqual(@"The folder C:\parent does not exist.", Precheck(op, [@"C:\parent"], []), "a file is not a folder");
        Assert.IsNull(Precheck(op, [], [@"C:\parent"]));
    }

    [TestMethod]
    public void ANewFolderWhoseNameIsTakenExistsAlready()
    {
        var op = Op(FileOperationKind.NewFolder, [@"C:\parent"], name: "x");

        Assert.AreEqual(@"C:\parent\x exists already.", Precheck(op, [], [@"C:\parent", @"C:\parent\x"]));
        Assert.AreEqual(@"C:\parent\x exists already.", Precheck(op, [@"C:\parent\x"], [@"C:\parent"]));
    }

    [TestMethod]
    public void ACopyOrMoveIntoAFileIsRefused()
    {
        foreach (var kind in new[] { FileOperationKind.Copy, FileOperationKind.Move })
        {
            var op = Op(kind, [@"C:\a.txt"], @"D:\dest");

            Assert.AreEqual(@"D:\dest is a file, not a folder.", Precheck(op, [@"C:\a.txt", @"D:\dest"], []));
        }
    }

    [TestMethod]
    public void ACopyOrMoveNeedsNoExistingDestinationFolderHere()
    {
        var op = Op(FileOperationKind.Copy, [@"C:\a.txt"], @"D:\dest");

        Assert.IsNull(Precheck(op, [@"C:\a.txt"], []));
        Assert.IsNull(Precheck(op, [@"C:\a.txt"], [@"D:\dest"]));
    }

    [TestMethod]
    public void ACopyOrMoveDoesNotCheckWhetherTheTargetNameIsTaken()
    {
        var op = Op(FileOperationKind.Copy, [@"C:\a.txt"], @"D:\dest");

        Assert.IsNull(Precheck(op, [@"C:\a.txt", @"D:\dest\a.txt"], [@"D:\dest"]), "the shell renames on collision");
    }

    [TestMethod]
    public void AMissingSourceComesBeforeADestinationThatIsAFile()
    {
        var op = Op(FileOperationKind.Move, [@"C:\a.txt"], @"D:\dest");

        Assert.AreEqual(@"C:\a.txt does not exist.", Precheck(op, [@"D:\dest"], []));
    }

    [TestMethod]
    public void ARenameToATakenNameIsRefused()
    {
        var op = Op(FileOperationKind.Rename, [@"C:\dir\a.txt"], name: "b.txt");

        Assert.AreEqual(@"C:\dir\b.txt exists already.", Precheck(op, [@"C:\dir\a.txt", @"C:\dir\b.txt"], []));
        Assert.AreEqual(@"C:\dir\b.txt exists already.", Precheck(op, [@"C:\dir\a.txt"], [@"C:\dir\b.txt"]));
    }

    [TestMethod]
    public void ARenameToAFreeNameIsFine()
    {
        var op = Op(FileOperationKind.Rename, [@"C:\dir\a.txt"], name: "b.txt");

        Assert.IsNull(Precheck(op, [@"C:\dir\a.txt"], []));
    }

    [TestMethod]
    public void ARenameOnlyChangingTheCaseIsNotTaken()
    {
        var op = Op(FileOperationKind.Rename, [@"C:\dir\a.txt"], name: "A.TXT");

        // Windows is case-insensitive: the new name "exists" because it is the entry itself.
        Assert.IsNull(op.Precheck(p => string.Equals(p, @"C:\dir\a.txt", StringComparison.OrdinalIgnoreCase), _ => false));
    }

    [TestMethod]
    public void ARenameToTheSameNameIsNotTaken()
    {
        var op = Op(FileOperationKind.Rename, [@"C:\dir\a.txt"], name: "a.txt");

        Assert.IsNull(Precheck(op, [@"C:\dir\a.txt"], []));
    }

    [TestMethod]
    public void ABookmarkOnlyNeedsItsEntryToExist()
    {
        Assert.IsNull(Precheck(Op(FileOperationKind.AddBookmark, [@"C:\dir"]), [], [@"C:\dir"]));
        Assert.AreEqual(@"C:\dir does not exist.", Precheck(Op(FileOperationKind.AddBookmark, [@"C:\dir"]), [], []));
    }

    [TestMethod]
    public void TheExistenceChecksAreRequired()
    {
        var op = Op(FileOperationKind.Recycle, [@"C:\a.txt"]);

        Assert.ThrowsExactly<ArgumentNullException>(() => op.Precheck(null!, _ => true));
        Assert.ThrowsExactly<ArgumentNullException>(() => op.Precheck(_ => true, null!));
    }
}
