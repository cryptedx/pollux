using Pollux.Core.Commands;
using Pollux.Core.Files;
using Pollux.Core.Search;
using Pollux.Core.Settings;

namespace Pollux.Core.Tests.Search;

[TestClass]
public sealed class FolderSearchTests
{
    private DirectoryInfo _root = null!;

    [TestInitialize]
    public void CreateTree()
    {
        _root = Directory.CreateTempSubdirectory();
        Write("README.md", "Pollux readme");
        Write(Path.Combine("src", "PaneView.cs"), "class PaneView { // needle }");
        Write(Path.Combine("src", "Views", "MainWindow.cs"), "nothing here");
        Write(Path.Combine("src", "Views", "pane.png"), "needle\0binary");
        Write(Path.Combine("node_modules", "pane.js"), "needle");
        Write(Path.Combine("src", "obj", "PaneView.g.cs"), "needle");
        Write(Path.Combine("docs", "big.txt"), new string('x', 3000) + "needle");
        Write(Path.Combine("docs", "copy.pollux-part"), "needle");
    }

    [TestCleanup]
    public void DeleteTree() => _root.Delete(recursive: true);

    private void Write(string relative, string content)
    {
        var path = Path.Combine(_root.FullName, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static FolderSearchOptions Options(long maxContentBytes = 1024 * 1024, int maxResults = 1000) =>
        new(["node_modules", ".git", "bin", "obj"], maxContentBytes, MaxResults: maxResults);

    private List<FolderSearchHit> Run(string text, string? content = null, FolderSearchOptions? options = null)
    {
        var query = FolderSearchQuery.Parse(text, content, DateTime.Now).Query!;
        return [.. FolderSearch.Enumerate(_root.FullName, query, options ?? Options(), CancellationToken.None)];
    }

    private static string Rel(params string[] parts) => Path.Combine(parts);

    [TestMethod]
    public void FindsByNameBelowTheRootWithRelativePathsAndSkipsExcludedFolders()
    {
        var hits = Run("pane");

        CollectionAssert.AreEquivalent(
            new[] { Rel("src", "PaneView.cs"), Rel("src", "Views", "pane.png") },
            hits.Select(h => h.RelativePath).ToArray());
        Assert.AreEqual(Path.Combine(_root.FullName, "src", "PaneView.cs"), hits.Single(h => h.Entry.Name == "PaneView.cs").Entry.FullPath);
    }

    [TestMethod]
    public void ShallowerResultsComeFirst()
    {
        var hits = Run("*.*");

        Assert.AreEqual("README.md", hits[0].RelativePath);
        var depths = hits.Select(h => h.RelativePath.Count(c => c == Path.DirectorySeparatorChar)).ToList();
        CollectionAssert.AreEqual(depths.Order().ToList(), depths);
    }

    [TestMethod]
    public void FoldersAreResultsToo()
    {
        var hits = Run("views");

        Assert.AreEqual(Rel("src", "Views"), hits.Single().RelativePath);
        Assert.IsTrue(hits.Single().Entry.IsDirectory);
    }

    [TestMethod]
    public void ContentSearchSkipsBinariesExcludedFoldersAndTransferFiles()
    {
        var hits = Run("", content: "NEEDLE");

        CollectionAssert.AreEquivalent(
            new[] { Rel("src", "PaneView.cs"), Rel("docs", "big.txt") },
            hits.Select(h => h.RelativePath).ToArray());
    }

    [TestMethod]
    public void ContentSearchSkipsFilesOverTheSizeLimit()
    {
        var hits = Run("", content: "needle", options: Options(maxContentBytes: 1000));

        CollectionAssert.AreEqual(new[] { Rel("src", "PaneView.cs") }, hits.Select(h => h.RelativePath).ToArray());
    }

    [TestMethod]
    public void HighlightsPointIntoTheRelativePath()
    {
        var hit = Run("view").Single(h => h.Entry.Name == "PaneView.cs");

        CollectionAssert.AreEqual(new[] { new MatchRange("src/Pane".Length, 4) }, hit.Highlights.ToArray());
    }

    [TestMethod]
    public void StopsAtTheResultLimit()
    {
        Assert.AreEqual(2, Run("*", options: Options(maxResults: 2)).Count);
    }

    [TestMethod]
    public void HiddenEntriesAreSkippedUnlessIncluded()
    {
        var hidden = Directory.CreateDirectory(Path.Combine(_root.FullName, ".hidden"));
        File.WriteAllText(Path.Combine(hidden.FullName, "secret-pane.txt"), "");
        hidden.Attributes |= FileAttributes.Hidden;

        Assert.AreEqual(0, Run("secret").Count);
        Assert.AreEqual(1, Run("secret", options: Options() with { IncludeHidden = true }).Count);
    }

    [TestMethod]
    public void DoesNotFollowLinkedFolders()
    {
        var link = Path.Combine(_root.FullName, "src", "Views", "loop");
        try
        {
            Directory.CreateSymbolicLink(link, _root.FullName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Assert.Inconclusive("Creating a symbolic link needs Developer Mode on Windows.");
        }

        Assert.AreEqual(1, Run("readme").Count);
    }

    [TestMethod]
    public void OptionsComeFromSettings()
    {
        var settings = SettingsStore.Parse("""
            { "search": { "maxContentFileMegabytes": 2, "excludeNames": ["dist"] }, "files": { "showHidden": true } }
            """).Settings;

        var options = FolderSearchOptions.For(settings);

        Assert.AreEqual(2L * 1024 * 1024, options.MaxContentBytes);
        CollectionAssert.AreEqual(new[] { "dist" }, options.ExcludeNames.ToArray());
        Assert.IsTrue(options.IncludeHidden);
        Assert.IsFalse(options.IncludeSystem);
        Assert.AreEqual(10L * 1024 * 1024, FolderSearchOptions.For(new PolluxSettings()).MaxContentBytes);
    }

    [TestMethod]
    public void SearchKeysAreBound()
    {
        var keys = CommandRegistry.Keybindings(new CopilotSettings(), []);

        Assert.AreEqual("search.inFolder", keys.Resolve(KeyChord.Parse("ctrl+shift+f"), Context.Empty));
        Assert.AreEqual("search.cancel", keys.Resolve(KeyChord.Parse("escape"), Context.Of("searchActive")));
        Assert.AreEqual("filter.close", keys.Resolve(KeyChord.Parse("escape"), Context.Of("filterFocused")));
        Assert.AreEqual("search.revealInFolder", keys.Resolve(KeyChord.Parse("ctrl+enter"), Context.Of("searchActive")));
        Assert.IsNull(keys.Resolve(KeyChord.Parse("escape"), Context.Empty));
    }

    [TestMethod]
    public void CancellationStopsTheWalk()
    {
        using var cts = new CancellationTokenSource();
        var query = FolderSearchQuery.Parse("*", null, DateTime.Now).Query!;
        cts.Cancel();

        Assert.ThrowsExactly<OperationCanceledException>(() => FolderSearch.Enumerate(_root.FullName, query, Options(), cts.Token).ToList());
    }

    [TestMethod]
    public async Task StreamsBatchesUntilDone()
    {
        var query = FolderSearchQuery.Parse("*", null, DateTime.Now).Query!;
        var all = new List<FolderSearchHit>();

        await foreach (var batch in FolderSearch.SearchAsync(_root.FullName, query, Options(), TimeSpan.FromMilliseconds(1), CancellationToken.None))
        {
            Assert.IsTrue(batch.Count > 0);
            all.AddRange(batch);
        }

        Assert.AreEqual(Run("*").Count, all.Count);
    }

    [TestMethod]
    public async Task StreamingCanBeCancelled()
    {
        using var cts = new CancellationTokenSource();
        var query = FolderSearchQuery.Parse("*", null, DateTime.Now).Query!;
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in FolderSearch.SearchAsync(_root.FullName, query, Options(), TimeSpan.FromMilliseconds(1), cts.Token))
            {
            }
        });
    }
}
