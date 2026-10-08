using Pollux.Core.Git;

namespace Pollux.Core.Tests.Git;

[TestClass]
public sealed class GitStatusTests
{
    private const string Porcelain = " M src/App.cs\0?? notes/new.md\0A  docs/adr/0005.md\0!! bin/\0R  new.txt\0old.txt\0 D gone.txt\0";

    [TestMethod]
    public void FilesInRepoRoot()
    {
        var status = GitStatus.Parse(Porcelain);

        Assert.AreEqual(GitMark.Renamed, status.MarkFor("", "new.txt", isDirectory: false));
        Assert.AreEqual(GitMark.None, status.MarkFor("", "README.md", isDirectory: false));
        Assert.AreEqual(GitMark.Ignored, status.MarkFor("", "bin", isDirectory: true));
    }

    [TestMethod]
    public void FoldersAggregateTheirContents()
    {
        var status = GitStatus.Parse(Porcelain);

        Assert.AreEqual(GitMark.Modified, status.MarkFor("", "src", isDirectory: true));
        Assert.AreEqual(GitMark.Untracked, status.MarkFor("", "notes", isDirectory: true));
        Assert.AreEqual(GitMark.Added, status.MarkFor("docs", "adr", isDirectory: true));
    }

    [TestMethod]
    public void FilesInSubfolder()
    {
        var status = GitStatus.Parse(Porcelain);

        Assert.AreEqual(GitMark.Modified, status.MarkFor("src", "App.cs", isDirectory: false));
        Assert.AreEqual(GitMark.Untracked, status.MarkFor("notes", "new.md", isDirectory: false));
        Assert.AreEqual(GitMark.Ignored, status.MarkFor("bin", "Debug", isDirectory: true));
    }
}
