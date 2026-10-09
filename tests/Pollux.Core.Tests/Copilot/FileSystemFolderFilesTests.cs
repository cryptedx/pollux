using Pollux.Core.Copilot.Guards;

namespace Pollux.Core.Tests.Copilot;

[TestClass]
public sealed class FileSystemFolderFilesTests
{
    private string _root = "";

    [TestInitialize]
    public void Create()
    {
        _root = Path.Combine(Path.GetTempPath(), "pollux-folderfiles-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "src", "certs"));
        Directory.CreateDirectory(Path.Combine(_root, ".git", "objects"));
        File.WriteAllText(Path.Combine(_root, ".env"), "");
        File.WriteAllText(Path.Combine(_root, "src", "Program.cs"), "");
        File.WriteAllText(Path.Combine(_root, "src", "certs", "prod.key"), "");
        File.WriteAllText(Path.Combine(_root, ".git", "objects", "secret.key"), "");
    }

    [TestCleanup]
    public void Delete() => Directory.Delete(_root, recursive: true);

    [TestMethod]
    public void ListsEveryFileAndFolderBelowWithRelativeBackslashPathsButNotGit()
    {
        var entries = FileSystemFolderFiles.Instance.Below(_root).Select(e => e.IsFolder ? e.Path + @"\" : e.Path).Order(StringComparer.Ordinal).ToList();

        CollectionAssert.AreEqual(new[] { ".env", @"src\", @"src\Program.cs", @"src\certs\", @"src\certs\prod.key" }, entries);
    }

    [TestMethod]
    public void AMissingFolderOrAFileHasNothingBelow()
    {
        Assert.AreEqual(0, FileSystemFolderFiles.Instance.Below(Path.Combine(_root, "missing")).Count());
        Assert.AreEqual(0, FileSystemFolderFiles.Instance.Below(Path.Combine(_root, ".env")).Count());
    }

    /// <summary>Windows allows symbolic links only with Developer Mode or as admin; without, the link tests cannot run there.</summary>
    private static void CreateLink(Action create)
    {
        try
        {
            create();
        }
        catch (Exception e) when (OperatingSystem.IsWindows() && e is UnauthorizedAccessException or IOException)
        {
            Assert.Inconclusive("Creating symbolic links needs Developer Mode on Windows: " + e.Message);
        }
    }

    [TestMethod]
    public void AFolderLinkIsListedAsALinkAndNotEntered()
    {
        CreateLink(() => Directory.CreateSymbolicLink(Path.Combine(_root, "linked"), Path.Combine(_root, "src")));

        var entries = FileSystemFolderFiles.Instance.Below(_root).ToList();

        Assert.Contains(FolderEntry.Link("linked"), entries);
        Assert.IsFalse(entries.Any(e => e.Path.StartsWith(@"linked\", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void CanonicalResolvesALinkInThePath()
    {
        var files = FileSystemFolderFiles.Instance;
        CreateLink(() => Directory.CreateSymbolicLink(Path.Combine(_root, "linked"), Path.Combine(_root, "src", "certs")));

        Assert.AreEqual(files.Canonical(Path.Combine(_root, "src", "certs", "prod.key")), files.Canonical(Path.Combine(_root, "linked", "prod.key")));
        Assert.AreNotEqual(Path.Combine(_root, "linked", "prod.key"), files.Canonical(Path.Combine(_root, "linked", "prod.key")));
    }

    [TestMethod]
    public void CanonicalOfANewFileBelowALinkResolvesTheNearestExistingFolder()
    {
        var files = FileSystemFolderFiles.Instance;
        CreateLink(() => Directory.CreateSymbolicLink(Path.Combine(_root, "linked"), Path.Combine(_root, "src", "certs")));

        Assert.AreEqual(
            Path.Combine(files.Canonical(Path.Combine(_root, "src", "certs"))!, "new", "a.key"),
            files.Canonical(Path.Combine(_root, "linked", "new", "a.key")));
    }

    [TestMethod]
    public void CanonicalFollowsAFileLinkAndChainsOfLinks()
    {
        var files = FileSystemFolderFiles.Instance;
        CreateLink(() => File.CreateSymbolicLink(Path.Combine(_root, "first"), Path.Combine(_root, "second")));
        CreateLink(() => File.CreateSymbolicLink(Path.Combine(_root, "second"), Path.Combine(_root, "src", "certs", "prod.key")));

        Assert.AreEqual(files.Canonical(Path.Combine(_root, "src", "certs", "prod.key")), files.Canonical(Path.Combine(_root, "first")));
    }

    [TestMethod]
    public void CanonicalOfALinkLoopFails()
    {
        CreateLink(() => File.CreateSymbolicLink(Path.Combine(_root, "a"), Path.Combine(_root, "b")));
        CreateLink(() => File.CreateSymbolicLink(Path.Combine(_root, "b"), Path.Combine(_root, "a")));

        Assert.IsNull(FileSystemFolderFiles.Instance.Canonical(Path.Combine(_root, "a")));
    }

    [TestMethod]
    public void CanonicalLeavesAPathThisMachineCannotHaveAlone()
    {
        var foreign = OperatingSystem.IsWindows() ? "/home/x/.ssh" : @"C:\Users\x\.ssh";

        Assert.AreEqual(foreign, FileSystemFolderFiles.Instance.Canonical(foreign));
    }
}
