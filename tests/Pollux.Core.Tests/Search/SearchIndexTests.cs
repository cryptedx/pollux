using Pollux.Core.Search;

namespace Pollux.Core.Tests.Search;

[TestClass]
public sealed class SearchIndexTests
{
    [TestMethod]
    public void FindsAndRanksAcrossRootsAndSkipsExcluded()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var repo = Directory.CreateDirectory(Path.Combine(dir.FullName, "repos", "pollux", "src"));
            File.WriteAllText(Path.Combine(repo.FullName, "PaneView.xaml.cs"), "");
            File.WriteAllText(Path.Combine(repo.FullName, "Pollux.csproj"), "");
            var nm = Directory.CreateDirectory(Path.Combine(dir.FullName, "repos", "pollux", "node_modules"));
            File.WriteAllText(Path.Combine(nm.FullName, "pane.js"), "");

            var index = new SearchIndex();
            index.Build([dir.FullName], ["node_modules"], CancellationToken.None);

            var hits = index.Find("pane", 10);
            Assert.AreEqual("PaneView.xaml.cs", hits[0].Name);
            Assert.IsFalse(hits.Any(h => h.Name == "pane.js"));

            Assert.AreEqual("pollux", index.Find("pollux", 10)[0].Name);
            Assert.AreEqual("PaneView.xaml.cs", index.Find("src pvx", 10).Single().Name);
        }
        finally { dir.Delete(true); }
    }

    [TestMethod]
    public void ConcurrentUpdatesDoNotBreakSearch()
    {
        var index = new SearchIndex();
        for (var i = 0; i < 3000; i++) index.Add($@"C:\r\file{i}.txt", false);
        var writer = Task.Run(() => { for (var i = 0; i < 3000; i++) { index.Add($@"C:\r\more{i}.txt", false); index.Remove($@"C:\r\file{i}.txt"); } });
        for (var k = 0; k < 50; k++) index.Find("file", 10);
        writer.Wait();
        Assert.AreEqual(0, index.Find("file", 10).Count);
    }

    [TestMethod]
    public void AddAndRemoveKeepIndexFresh()
    {
        var index = new SearchIndex();
        index.Add(@"C:\a\new.txt", false);
        Assert.AreEqual(1, index.Find("new", 5).Count);
        index.Remove(@"C:\a");
        Assert.AreEqual(0, index.Find("new", 5).Count);
    }
}
