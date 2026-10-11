using Pollux.Core.Git;

namespace Pollux.Core.Tests.Git;

/// <summary>The git gate on real temporary folders (SECURITY.md S-01, S-05; ADR 0006).</summary>
[TestClass]
public sealed class GitGateTests
{
    private string _root = null!;

    [TestInitialize]
    public void Setup() => _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "pollux-gitgate-" + Guid.NewGuid().ToString("N"))).FullName;

    [TestCleanup]
    public void Cleanup() => Directory.Delete(_root, recursive: true);

    private static string Write(string folder, string relative, string text)
    {
        var path = Path.Combine(folder, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    /// <summary>A repository with a .git directory whose HEAD points at <paramref name="branch"/>.</summary>
    private string Repo(string relative, string branch = "main")
    {
        var repo = Path.Combine(_root, relative);
        Write(repo, Path.Combine(".git", "HEAD"), $"ref: refs/heads/{branch}\n");
        Write(repo, Path.Combine(".git", "config"), "[core]\n\trepositoryformatversion = 0\n");
        return repo;
    }

    private static GitGate Gate(IReadOnlyList<string> trusted, bool gitStatus = true, Func<string, bool>? isNetwork = null) =>
        new(trusted, gitStatus, isNetwork ?? (_ => false));

    [TestMethod]
    public void UntrustedRepositoryShowsOnlyTheBranch()
    {
        // S-01: a repo from an extracted zip outside every Trusted Folder never gets git started.
        var repo = Repo(Path.Combine("Downloads", "evil"), "feature/x");

        var result = Gate([Path.Combine(_root, "dev")]).Evaluate(repo);

        Assert.AreEqual(GitGateMode.BranchOnly, result.Mode);
        Assert.AreEqual("feature/x", result.Branch);
        Assert.AreEqual(repo, result.RepoRoot);
    }

    [TestMethod]
    public void TrustedRepositoryRunsStatusAtItsRoot()
    {
        var repo = Repo(Path.Combine("dev", "app"));
        var sub = Directory.CreateDirectory(Path.Combine(repo, "src", "lib")).FullName;

        var result = Gate([Path.Combine(_root, "dev")]).Evaluate(sub);

        Assert.AreEqual(GitGateMode.RunStatus, result.Mode);
        Assert.AreEqual(repo, result.RepoRoot);
        Assert.AreEqual("main", result.Branch);
    }

    [TestMethod]
    public void GitStatusSwitchOffShowsOnlyTheBranchEvenWhenTrusted()
    {
        var repo = Repo(Path.Combine("dev", "app"));

        var result = Gate([Path.Combine(_root, "dev")], gitStatus: false).Evaluate(repo);

        Assert.AreEqual(GitGateMode.BranchOnly, result.Mode);
        Assert.AreEqual("main", result.Branch);
    }

    [TestMethod]
    public void TrustedFolderCoversNestedRepositoriesButNotSiblingsSharingItsPrefix()
    {
        var nested = Repo(Path.Combine("dev", "group", "deep", "app"));
        var sibling = Repo(Path.Combine("dev2", "app"));
        var gate = Gate([Path.Combine(_root, "dev")]);

        Assert.AreEqual(GitGateMode.RunStatus, gate.Evaluate(nested).Mode);
        Assert.AreEqual(GitGateMode.BranchOnly, gate.Evaluate(sibling).Mode);
    }

    [TestMethod]
    public void TrustedFoldersMatchAfterCanonicalisation()
    {
        var repo = Repo(Path.Combine("dev", "app"));
        var download = Repo(Path.Combine("Downloads", "evil"));
        var spelledOddly = Path.Combine(_root, "other", "..", "DEV") + Path.DirectorySeparatorChar;
        var escapingFolder = Path.Combine(repo, "..", "..", "Downloads", "evil");

        Assert.AreEqual(GitGateMode.RunStatus, Gate([spelledOddly]).Evaluate(repo).Mode);
        Assert.AreEqual(GitGateMode.BranchOnly, Gate([Path.Combine(_root, "dev")]).Evaluate(escapingFolder).Mode, "'..' in the folder resolves before matching");
        Assert.AreEqual(GitGateMode.RunStatus, Gate([Path.Combine(_root, "dev", "..", "Downloads")]).Evaluate(download).Mode);
    }

    [TestMethod]
    public void FolderOutsideAnyRepositoryGetsNothing()
    {
        var plain = Directory.CreateDirectory(Path.Combine(_root, "dev", "notes")).FullName;

        Assert.AreEqual(GitGateResult.None, Gate([_root]).Evaluate(plain));
    }

    [TestMethod]
    [DataRow("pollux:home")]
    [DataRow("")]
    [DataRow(@"relative\folder")]
    public void NonFolderLocationsGetNothing(string location) =>
        Assert.AreEqual(GitGateResult.None, Gate([_root]).Evaluate(location));

    [TestMethod]
    public void DetachedHeadShowsTheShortHash()
    {
        var repo = Repo("detached");
        File.WriteAllText(Path.Combine(repo, ".git", "HEAD"), "0123456789abcdef0123456789abcdef01234567\n");

        Assert.AreEqual("0123456", Gate([]).Evaluate(repo).Branch);
    }

    [TestMethod]
    public void GitFileWithLocalGitdirIsFollowed()
    {
        // Worktrees and submodules: ".git" is a file naming the real git dir, often relatively.
        var main = Repo(Path.Combine("dev", "main"));
        Write(main, Path.Combine(".git", "worktrees", "wt", "HEAD"), "ref: refs/heads/topic\n");
        Write(main, Path.Combine(".git", "worktrees", "wt", "commondir"), "../..\n");
        var worktree = Directory.CreateDirectory(Path.Combine(_root, "dev", "wt")).FullName;
        File.WriteAllText(Path.Combine(worktree, ".git"), "gitdir: ../main/.git/worktrees/wt\n");

        var result = Gate([Path.Combine(_root, "dev")]).Evaluate(worktree);

        Assert.AreEqual(GitGateMode.RunStatus, result.Mode);
        Assert.AreEqual(worktree, result.RepoRoot);
        Assert.AreEqual("topic", result.Branch);
    }

    [TestMethod]
    [DataRow(@"\\attacker\share\x")]
    [DataRow("//attacker/share/x")]
    [DataRow(@"\\?\UNC\attacker\share\x")]
    [DataRow(@"\\.\UNC\attacker\share\x")]
    public void GitFileWithUncGitdirIsIgnoredWithoutTouchingIt(string gitdir)
    {
        // S-05: "gitdir: \\attacker\x" made Pollux authenticate against the attacker's SMB server.
        var repo = Directory.CreateDirectory(Path.Combine(_root, "dev", "evil")).FullName;
        File.WriteAllText(Path.Combine(repo, ".git"), $"gitdir: {gitdir}\n");
        if (!OperatingSystem.IsWindows() && !gitdir.StartsWith('/'))
        {
            // Off Windows a backslash is an ordinary file name character: plant a decoy the old code would have read.
            Write(repo, Path.Combine(gitdir, "HEAD"), "ref: refs/heads/leaked\n");
        }

        var asked = new List<string>();
        var result = Gate([Path.Combine(_root, "dev")], isNetwork: p => { asked.Add(p); return false; }).Evaluate(repo);

        Assert.AreEqual(GitGateResult.None, result);
        Assert.IsFalse(asked.Exists(p => p.Contains("attacker", StringComparison.Ordinal)), "the UNC target is refused by its spelling alone");
    }

    [TestMethod]
    public void GitFileWithGitdirOnANetworkDriveIsIgnoredWithoutReadingIt()
    {
        // S-05 via a mapped network drive ("gitdir: Z:\x"); the fake drive check marks the "mapped" folder as network.
        var mapped = Path.Combine(_root, "mapped");
        Write(mapped, "HEAD", "ref: refs/heads/leaked\n");
        var repo = Directory.CreateDirectory(Path.Combine(_root, "dev", "evil")).FullName;
        File.WriteAllText(Path.Combine(repo, ".git"), $"gitdir: {mapped}\n");

        var result = Gate([Path.Combine(_root, "dev")], isNetwork: p => p.StartsWith(mapped, StringComparison.Ordinal)).Evaluate(repo);

        Assert.AreEqual(GitGateResult.None, result);
    }

    [TestMethod]
    public void DotGitLinkToANetworkLocationIsNotFollowed()
    {
        // S-05 variant: ".git" as a symbolic link to a UNC path.
        var repo = Directory.CreateDirectory(Path.Combine(_root, "dev", "evil")).FullName;
        try { Directory.CreateSymbolicLink(Path.Combine(repo, ".git"), "//attacker/share/x"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Assert.Inconclusive("cannot create symbolic links here: " + ex.Message); }

        Assert.AreEqual(GitGateResult.None, Gate([Path.Combine(_root, "dev")]).Evaluate(repo));
    }

    [TestMethod]
    public void WorktreeWhoseCommondirIsRemoteNeverRunsStatus()
    {
        var gitDir = Path.Combine(_root, "dev", "gitdirs", "wt");
        Write(gitDir, "HEAD", "ref: refs/heads/topic\n");
        Write(gitDir, "commondir", "//attacker/share/x\n");
        var worktree = Directory.CreateDirectory(Path.Combine(_root, "dev", "wt")).FullName;
        File.WriteAllText(Path.Combine(worktree, ".git"), $"gitdir: {gitDir}\n");

        var result = Gate([Path.Combine(_root, "dev")]).Evaluate(worktree);

        Assert.AreEqual(GitGateMode.BranchOnly, result.Mode);
        Assert.AreEqual("topic", result.Branch);
    }

    [TestMethod]
    public void NetworkLocationsAreNeverTrustedFolders()
    {
        // S-01, ADR 0006: remote content never drives local git, even when settings.json lists the share.
        var mapped = Path.Combine(_root, "mapped");
        var repo = Repo(Path.Combine("mapped", "app"));
        var gate = Gate([@"\\server\share", "//server/share", mapped], isNetwork: p => p.StartsWith(mapped, StringComparison.Ordinal));

        Assert.IsFalse(gate.IsTrusted(repo));
        Assert.IsFalse(gate.IsTrusted(@"\\server\share\repo"));
        Assert.AreEqual(GitGateResult.None, gate.Evaluate(repo), "a folder on a network drive gets no git at all");
        Assert.AreEqual(GitGateResult.None, gate.Evaluate(@"\\server\share\repo"));
    }

    [TestMethod]
    public void SanitizeDropsNetworkRelativeAndDuplicateEntries()
    {
        var dev = Path.Combine(_root, "dev");
        var mapped = Path.Combine(_root, "mapped");

        var result = GitGate.Sanitize(
            [dev, "  ", @"\\server\share", "//server/share", "relative", mapped, dev + Path.DirectorySeparatorChar, dev.ToUpperInvariant()],
            p => p.StartsWith(mapped, StringComparison.Ordinal));

        CollectionAssert.AreEqual(new[] { dev }, result);
    }

    [TestMethod]
    public void SanitizeSplitsEntriesThatHoldSeveralLines()
    {
        // A WinUI TextBox separates lines with a bare CR; a list saved from it arrived as one entry,
        // which dropped every real Trusted Folder and smuggled the network path into settings.json.
        var dev = Path.Combine(_root, "dev");
        var repos = Path.Combine(_root, "repos");

        var result = GitGate.Sanitize([dev + "\r" + @"\\server\share" + "\r\n" + repos + "\n"], _ => false);

        CollectionAssert.AreEqual(new[] { dev, repos }, result);
    }
}
