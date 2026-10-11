using Pollux.Core.Git;
using Pollux.Core.Settings;

namespace Pollux.Core.Tests.Git;

/// <summary>
/// One-time seeding of Trusted Folders from Bookmarks on the first start after the update (SECURITY.md S-01,
/// ADR 0006), on real temporary folders.
/// </summary>
[TestClass]
public sealed class TrustedFolderSeedingTests
{
    private string _root = null!;
    private string _profile = null!;
    private string _downloads = null!;
    private string _desktop = null!;
    private string _temp = null!;

    [TestInitialize]
    public void Setup()
    {
        _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "pollux-seed-" + Guid.NewGuid().ToString("N"))).FullName;
        _profile = Path.Combine(_root, "me");
        _downloads = Path.Combine(_profile, "Downloads");
        _desktop = Path.Combine(_profile, "Desktop");
        _temp = Path.Combine(_profile, "AppData", "Local", "Temp");
    }

    [TestCleanup]
    public void Cleanup() => Directory.Delete(_root, recursive: true);

    private static string Repo(string folder)
    {
        Directory.CreateDirectory(Path.Combine(folder, ".git"));
        File.WriteAllText(Path.Combine(folder, ".git", "HEAD"), "ref: refs/heads/main\n");
        return folder;
    }

    private bool Seed(PolluxSettings settings, IEnumerable<string> bookmarks, Func<string, bool>? isNetwork = null) =>
        TrustedFolderSeeding.RunOnce(settings, bookmarks, [_downloads, _desktop, _temp], isNetwork ?? (_ => false));

    [TestMethod]
    public void BookmarksThatAreOrContainRepositoriesBecomeTrustedFolders()
    {
        var repoBookmark = Repo(Path.Combine(_root, "work", "pollux"));
        var codeBookmark = Path.Combine(_root, "code");
        Repo(Path.Combine(codeBookmark, "app"));
        var plainBookmark = Directory.CreateDirectory(Path.Combine(_root, "photos")).FullName;
        var settings = new PolluxSettings();

        var changed = Seed(settings, [repoBookmark, codeBookmark, plainBookmark]);

        Assert.IsTrue(changed);
        CollectionAssert.AreEqual(new[] { repoBookmark, codeBookmark }, settings.Files.TrustedFolders);
        Assert.IsTrue(settings.Files.TrustedFoldersSeeded);
    }

    [TestMethod]
    public void DownloadsDesktopTempAndTheirParentsAreNeverSeeded()
    {
        // S-01: an extracted zip in Downloads must not become trusted just because Downloads is a Bookmark.
        Repo(Path.Combine(_downloads, "evil"));
        Repo(_desktop);
        var tempRepo = Repo(Path.Combine(_temp, "x"));
        Repo(Path.Combine(_profile, "source"));
        var settings = new PolluxSettings();

        Seed(settings, [_downloads, _desktop, tempRepo, _profile, Path.Combine(_downloads, "evil")]);

        CollectionAssert.AreEqual(Array.Empty<string>(), settings.Files.TrustedFolders, "the profile root would cover Downloads and Desktop");
        Assert.IsTrue(settings.Files.TrustedFoldersSeeded);
    }

    [TestMethod]
    public void NetworkBookmarksAreNeverSeeded()
    {
        var mapped = Repo(Path.Combine(_root, "mapped", "repo"));
        var settings = new PolluxSettings();

        Seed(settings, [@"\\server\share\repo", "//server/share/repo", mapped], p => p.StartsWith(Path.Combine(_root, "mapped"), StringComparison.Ordinal));

        CollectionAssert.AreEqual(Array.Empty<string>(), settings.Files.TrustedFolders);
    }

    [TestMethod]
    public void SeedingRunsOnlyOnce()
    {
        var repo = Repo(Path.Combine(_root, "dev", "app"));
        var settings = new PolluxSettings();
        Seed(settings, []);

        var changed = Seed(settings, [repo]);

        Assert.IsFalse(changed);
        CollectionAssert.AreEqual(Array.Empty<string>(), settings.Files.TrustedFolders, "a Bookmark added later is not trusted silently");
    }

    [TestMethod]
    public void SeedingKeepsExistingTrustedFoldersAndAddsNoDuplicates()
    {
        var dev = Path.Combine(_root, "dev");
        var covered = Repo(Path.Combine(dev, "app"));
        var other = Repo(Path.Combine(_root, "other"));
        var settings = new PolluxSettings();
        settings.Files.TrustedFolders = [dev];

        Seed(settings, [covered, other, other + Path.DirectorySeparatorChar]);

        CollectionAssert.AreEqual(new[] { dev, other }, settings.Files.TrustedFolders);
    }

    [TestMethod]
    public void SeededSettingIsRecordedInSettingsJson()
    {
        var settings = new PolluxSettings();
        Seed(settings, []);

        var reloaded = SettingsStore.Parse(SettingsStore.Serialize(settings)).Settings;

        Assert.IsTrue(reloaded.Files.TrustedFoldersSeeded);
        Assert.IsFalse(SettingsStore.Parse("{}").Settings.Files.TrustedFoldersSeeded, "an existing settings.json without the flag seeds once");
    }
}
