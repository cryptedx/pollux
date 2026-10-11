using Pollux.Core.Git;

namespace Pollux.Core.Tests.Git;

/// <summary>
/// The git gate's config scan (SECURITY.md S-01; ADR 0006): even inside a Trusted Folder a repository whose own
/// config can make git run commands is refused, and the verdict names the key so the path bar can say why.
/// </summary>
[TestClass]
public sealed class GitGateConfigScanTests
{
    private string _root = null!;

    [TestInitialize]
    public void Setup() => _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "pollux-gitscan-" + Guid.NewGuid().ToString("N"))).FullName;

    [TestCleanup]
    public void Cleanup() => Directory.Delete(_root, recursive: true);

    private string TrustedRepo(string config)
    {
        var repo = Path.Combine(_root, "dev", "app");
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        File.WriteAllText(Path.Combine(repo, ".git", "HEAD"), "ref: refs/heads/main\n");
        File.WriteAllText(Path.Combine(repo, ".git", "config"), config);
        return repo;
    }

    private GitGateResult Evaluate(string repo) => new GitGate([Path.Combine(_root, "dev")], gitStatusEnabled: true, _ => false).Evaluate(repo);

    // git rejects the SHA-1 empty tree as attribute source in a SHA-256 repository: status would fail with no hint.
    [TestMethod]
    [DataRow("[core]\n\trepositoryformatversion = 1\n[extensions]\n\tobjectFormat = sha256\n", "sha256")]
    [DataRow("[core]\n\trepositoryformatversion = 1\n[extensions]\n\tobjectformat = \"SHA256\" ; comment\n", "sha256")]
    [DataRow("[core]\n\trepositoryformatversion = 0\n", null)]
    [DataRow("[extensions]\n\tobjectFormat = sha1\n", "sha1")]
    public void TheGateReadsTheRepositorysObjectFormat(string config, string? format)
    {
        var result = Evaluate(TrustedRepo(config));

        Assert.AreEqual(GitGateMode.RunStatus, result.Mode);
        Assert.AreEqual(format, result.ObjectFormat);
    }

    [TestMethod]
    public void CleanFilterFromTheReviewIsRefusedInsideATrustedFolder()
    {
        // S-01: a repo cloned into C:\dev whose config defines a clean filter would run calc.exe on "git status".
        var repo = TrustedRepo("[core]\n\trepositoryformatversion = 0\n[filter \"x\"]\n\tclean = calc.exe\n");

        var result = Evaluate(repo);

        Assert.AreEqual(GitGateMode.BranchOnly, result.Mode, "git status must not run");
        Assert.AreEqual("main", result.Branch, "the branch is still shown");
        Assert.AreEqual(repo, result.RepoRoot);
        Assert.AreEqual("filter.x.clean", result.RefusedConfigKey);
    }

    [TestMethod]
    [DataRow("[filter \"lfs\"]\n\tsmudge = evil.exe %f\n", "filter.lfs.smudge")]
    [DataRow("[filter \"x\"]\n\tprocess = evil.exe\n", "filter.x.process")]
    [DataRow("[filter \"x\"]\n\trequired\n", "filter.x.required")]
    [DataRow("[diff \"x\"]\n\tcommand = evil.exe\n", "diff.x.command")]
    [DataRow("[diff \"x\"]\n\ttextconv = evil.exe\n", "diff.x.textconv")]
    [DataRow("[merge \"x\"]\n\tdriver = evil.exe %O %A %B\n", "merge.x.driver")]
    [DataRow("[include]\n\tpath = ../../Downloads/evil.config\n", "include.path")]
    [DataRow("[includeIf \"gitdir:~/\"]\n\tpath = evil.config\n", "includeif.gitdir:~/.path")]
    [DataRow("[core]\n\tfsmonitor = evil.exe\n", "core.fsmonitor")]
    [DataRow("[core]\n\tsshCommand = evil.exe\n", "core.sshcommand")]
    [DataRow("[core]\n\tpager = evil.exe\n", "core.pager")]
    [DataRow("[core]\n\teditor = evil.exe\n", "core.editor")]
    [DataRow("[core]\n\thooksPath = ../evil-hooks\n", "core.hookspath")]
    [DataRow("[core]\n\tattributesFile = ../evil-attributes\n", "core.attributesfile")]
    public void EveryHostileKeyIsRefused(string config, string key)
    {
        var result = Evaluate(TrustedRepo("[core]\n\tbare = false\n" + config));

        Assert.AreEqual(GitGateMode.BranchOnly, result.Mode);
        Assert.AreEqual(key, result.RefusedConfigKey);
    }

    [TestMethod]
    [DataRow("[FILTER \"x\"]\n\tCLEAN = evil.exe\n", DisplayName = "section and key names are case-insensitive")]
    [DataRow("[filter.x]\n\tclean = evil.exe\n", DisplayName = "legacy dotted subsection")]
    [DataRow("[filter \"a\\\"b\"]\n\tclean = evil.exe\n", DisplayName = "escaped quote in subsection")]
    [DataRow("[core] fsmonitor = evil.exe\n", DisplayName = "key on the header line")]
    [DataRow("[core]\r\n\tpager = evil.exe\r\n", DisplayName = "CRLF line ends")]
    [DataRow("\uFEFF[core]\n\teditor = evil.exe\n", DisplayName = "byte order mark")]
    [DataRow("[core]\n\tautocrlf = false ; harmless \\\n\tfsmonitor = evil.exe\n", DisplayName = "backslash in a comment does not continue the line")]
    [DataRow("[core]\n\tautocrlf = \"a;b\"\n\tfsmonitor = evil.exe\n", DisplayName = "comment character inside quotes")]
    [DataRow("[core]\n\tbare = false\n[core \"x\"]\n[core]\n\thooksPath = h\n", DisplayName = "section reopened")]
    [DataRow("[core]\n\tfsmonitor\n", DisplayName = "key without value")]
    [DataRow("[core]\n\tfsmonitor = evil.exe", DisplayName = "no final newline")]
    public void HostileKeysAreFoundInEverySpellingGitAccepts(string config) =>
        Assert.IsNotNull(Evaluate(TrustedRepo(config)).RefusedConfigKey);

    [TestMethod]
    [DataRow("[core]\n\tautocrlf = false \\\n[filter \"x\"]\n", DisplayName = "a continued value hides nothing: the header is value text")]
    [DataRow("[core]\n\tautocrlf = \"unterminated\n", DisplayName = "unterminated quote")]
    [DataRow("[core\n\tfsmonitor = x\n", DisplayName = "broken header")]
    [DataRow("fsmonitor = x\n", DisplayName = "key before any section")]
    [DataRow("[core]\n\t0key = x\n", DisplayName = "key not starting with a letter")]
    [DataRow("[core]\n\tbare = \\q\n", DisplayName = "unknown escape")]
    public void TextGitWouldRejectIsRefusedOrHarmless(string config)
    {
        // Either git would reject the file (then the gate refuses: fail closed) or the text is a plain value.
        var result = Evaluate(TrustedRepo(config));

        if (result.RefusedConfigKey is null) Assert.AreEqual(GitGateMode.RunStatus, result.Mode);
        else Assert.AreEqual(GitGateMode.BranchOnly, result.Mode);
        if (config.Contains("autocrlf = false \\", StringComparison.Ordinal)) Assert.IsNull(result.RefusedConfigKey, "git reads the header as part of the value");
        else Assert.AreEqual(GitConfigScan.Unreadable, result.RefusedConfigKey);
    }

    [TestMethod]
    public void CleanConfigFromARealCloneIsAccepted()
    {
        var repo = TrustedRepo("""
            [core]
            	repositoryformatversion = 0
            	filemode = false
            	bare = false
            	logallrefupdates = true
            	symlinks = false
            	ignorecase = true
            	autocrlf = input ; keep LF
            # a comment mentioning [filter "x"] and core.fsmonitor
            [remote "origin"]
            	url = https://github.com/example/repo.git
            	fetch = +refs/heads/*:refs/remotes/origin/*
            [branch "main"]
            	remote = origin
            	merge = refs/heads/main
            [diff "csharp"]
            	xfuncname = "^\\s*class"
            [merge]
            	conflictStyle = zdiff3
            [lfs]
            	repositoryformatversion = 0
            """);

        var result = Evaluate(repo);

        Assert.AreEqual(GitGateMode.RunStatus, result.Mode);
        Assert.IsNull(result.RefusedConfigKey);
        Assert.IsTrue(result.Trusted);
    }

    [TestMethod]
    public void RepositoryWithoutConfigFileIsAccepted()
    {
        var repo = TrustedRepo("");
        File.Delete(Path.Combine(repo, ".git", "config"));

        Assert.AreEqual(GitGateMode.RunStatus, Evaluate(repo).Mode);
    }

    [TestMethod]
    public void PerWorktreeConfigIsScannedToo()
    {
        var repo = TrustedRepo("[core]\n\tbare = false\n");
        File.WriteAllText(Path.Combine(repo, ".git", "config.worktree"), "[core]\n\tfsmonitor = evil.exe\n");

        Assert.AreEqual("core.fsmonitor", Evaluate(repo).RefusedConfigKey);
    }

    [TestMethod]
    public void LinkedWorktreeIsRefusedWhenTheSharedConfigIsHostile()
    {
        // git reads the main repository's config for a linked worktree, so that is the one that counts.
        var main = TrustedRepo("[filter \"x\"]\n\tclean = evil.exe\n");
        var gitDir = Directory.CreateDirectory(Path.Combine(main, ".git", "worktrees", "wt")).FullName;
        File.WriteAllText(Path.Combine(gitDir, "HEAD"), "ref: refs/heads/topic\n");
        File.WriteAllText(Path.Combine(gitDir, "commondir"), "../..\n");
        var worktree = Directory.CreateDirectory(Path.Combine(_root, "dev", "wt")).FullName;
        File.WriteAllText(Path.Combine(worktree, ".git"), "gitdir: ../app/.git/worktrees/wt\n");

        var result = Evaluate(worktree);

        Assert.AreEqual(GitGateMode.BranchOnly, result.Mode);
        Assert.AreEqual("topic", result.Branch);
        Assert.AreEqual("filter.x.clean", result.RefusedConfigKey);
    }

    [TestMethod]
    public void OversizedConfigIsRefusedInsteadOfScannedInPart()
    {
        var repo = TrustedRepo("[core]\n" + new string('#', 2 * 1024 * 1024) + "\n");

        Assert.AreEqual(GitConfigScan.Unreadable, Evaluate(repo).RefusedConfigKey);
    }

    [TestMethod]
    public void UntrustedRepositoryIsNotScannedAndOffersTrust()
    {
        // Outside Trusted Folders the gate never runs git anyway; the path bar offers "Trust this folder" instead.
        var repo = Path.Combine(_root, "Downloads", "evil");
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        File.WriteAllText(Path.Combine(repo, ".git", "HEAD"), "ref: refs/heads/main\n");
        File.WriteAllText(Path.Combine(repo, ".git", "config"), "[filter \"x\"]\n\tclean = evil.exe\n");

        var result = Evaluate(repo);

        Assert.AreEqual(GitGateMode.BranchOnly, result.Mode);
        Assert.IsNull(result.RefusedConfigKey);
        Assert.IsFalse(result.Trusted);
        Assert.IsTrue(result.CanTrust);
    }

    [TestMethod]
    public void HintNamesTheRefusedKey()
    {
        // Spec story 10: a short hint explains why the letters are missing.
        StringAssert.Contains(GitConfigScan.Hint("filter.x.clean"), "filter.x.clean");
        StringAssert.Contains(GitConfigScan.Hint(GitConfigScan.Unreadable), "could not be read");
    }

    [TestMethod]
    public void TrustingFromThePathBarAddsTheRepositoryRootAndEnablesStatus()
    {
        var repo = Path.Combine(_root, "work", "app");
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        File.WriteAllText(Path.Combine(repo, ".git", "HEAD"), "ref: refs/heads/main\n");
        var sub = Directory.CreateDirectory(Path.Combine(repo, "src")).FullName;
        var dev = Path.Combine(_root, "dev");
        var before = new GitGate([dev], gitStatusEnabled: true, _ => false).Evaluate(sub);

        var trusted = GitGate.AddTrustedFolder([dev], before.RepoRoot!, _ => false);
        var after = new GitGate(trusted!, gitStatusEnabled: true, _ => false).Evaluate(sub);

        CollectionAssert.AreEqual(new[] { dev, repo }, trusted, "the repository root is trusted, not the browsed subfolder");
        Assert.AreEqual(GitGateMode.RunStatus, after.Mode);
        Assert.IsFalse(after.CanTrust, "the action disappears once the repository is trusted");
    }

    [TestMethod]
    public void TrustActionRefusesNetworkLocations()
    {
        Assert.IsNull(GitGate.AddTrustedFolder([], @"\\server\share\repo", _ => false));
        Assert.IsNull(GitGate.AddTrustedFolder([], Path.Combine(_root, "mapped"), _ => true));
    }

    [TestMethod]
    public void TrustActionOnACoveredFolderChangesNothing()
    {
        var dev = Path.Combine(_root, "dev");

        CollectionAssert.AreEqual(new[] { dev }, GitGate.AddTrustedFolder([dev], Path.Combine(dev, "app"), _ => false));
    }
}
