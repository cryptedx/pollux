using Pollux.Core.Places;
using Pollux.Core.Session;

namespace Pollux.Core.Tests.Places;

[TestClass]
public sealed class PlacesTests
{
    [TestMethod]
    public void BookmarksKeepGroupOrderAndUngroupedFirst()
    {
        var list = new BookmarkList();
        list.Add(@"C:\repos\pollux", "pollux", "Code");
        list.Add(@"C:\Users\marko\Downloads", "Downloads");
        list.Add(@"C:\repos\api", "api", "Code");

        CollectionAssert.AreEqual(new[] { "Downloads", "pollux", "api" }, list.Flat.Select(b => b.Name).ToArray());
    }

    [TestMethod]
    public void AddingSamePathTwiceKeepsOne()
    {
        var list = new BookmarkList();
        list.Add(@"C:\a", "a");
        list.Add(@"c:\A", "again");

        Assert.AreEqual(1, list.Flat.Count());
    }

    [TestMethod]
    public void MoveBetweenGroups()
    {
        var list = new BookmarkList();
        list.Add(@"C:\a", "a", "One");
        list.Add(@"C:\b", "b", "Two");
        list.Add(@"C:\c", "c", "Two");

        list.Move(@"C:\a", "Two", 1);

        CollectionAssert.AreEqual(new[] { "b", "a", "c" }, list.Groups.Single(g => g.Name == "Two").Items.Select(b => b.Name).ToArray());
    }

    [TestMethod]
    public void RecentRanksFrequentAndRecent()
    {
        var now = new DateTime(2026, 10, 8, 12, 0, 0);
        var r = new RecentList();
        for (var i = 0; i < 5; i++) r.Visit(@"C:\often", now.AddDays(-2), 50, []);
        r.Visit(@"C:\once-old", now.AddDays(-40), 50, []);
        r.Visit(@"C:\just-now", now, 50, []);

        var ranked = r.Ranked(now).Select(e => e.Path).ToArray();

        Assert.AreEqual(@"C:\once-old", ranked[^1]);
    }

    [TestMethod]
    public void RecentRespectsExcludeAndCap()
    {
        var now = DateTime.Now;
        var r = new RecentList();
        r.Visit(@"C:\Temp\x", now, 2, [@"C:\Temp"]);
        r.Visit(@"C:\a", now, 2, []);
        r.Visit(@"C:\b", now, 2, []);
        r.Visit(@"C:\c", now, 2, []);

        Assert.AreEqual(2, r.Entries.Count);
        Assert.IsFalse(r.Entries.Any(e => e.Path.StartsWith(@"C:\Temp", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void StateFilesRoundTripAndSurviveCorruption()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var file = Path.Combine(dir.FullName, "bookmarks.json");
            var list = new BookmarkList();
            list.Add(@"C:\a", "a", "G");
            JsonFile.Save(file, list, StateJson.Default.BookmarkList);

            Assert.AreEqual("a", JsonFile.Load(file, StateJson.Default.BookmarkList).Flat.Single().Name);

            File.WriteAllText(file, "{ broken");
            Assert.AreEqual(0, JsonFile.Load(file, StateJson.Default.BookmarkList).Groups.Count);

            var session = Path.Combine(dir.FullName, "session.json");
            JsonFile.Save(session, new SessionState { Windows = [new WindowSession { DualPane = false }] }, StateJson.Default.SessionState);
            Assert.IsFalse(JsonFile.Load(session, StateJson.Default.SessionState).Windows[0].DualPane);
        }
        finally { dir.Delete(true); }
    }
}
