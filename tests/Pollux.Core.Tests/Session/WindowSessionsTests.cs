using Pollux.Core.Session;

namespace Pollux.Core.Tests.Session;

[TestClass]
public sealed class WindowSessionsTests
{
    private static WindowSession Saved(string loc, int width = 900)
    {
        var s = new WindowSession { Width = width, Height = 600 };
        s.Panes[0].Tabs = [new TabSession { Location = loc }, new TabSession { Location = "/two" }];
        return s;
    }

    private static bool Dir(string p) => p.StartsWith("/dir", StringComparison.Ordinal);

    [TestMethod]
    public void RestoresNthSavedWindow()
    {
        var saved = new List<WindowSession> { Saved("/a"), Saved("/b") };
        Assert.AreSame(saved[1], WindowSessions.ForNewWindow(true, true, saved, 1, false, null, Dir));
    }

    [TestMethod]
    public void FreshWindowWhenNotRestoringOrNoneLeft()
    {
        var saved = new List<WindowSession> { Saved("/a", 777) };
        foreach (var s in new[]
        {
            WindowSessions.ForNewWindow(false, true, saved, 0, false, null, Dir),
            WindowSessions.ForNewWindow(true, false, saved, 0, false, null, Dir),
            WindowSessions.ForNewWindow(true, true, saved, 1, false, null, Dir),
        })
        {
            Assert.AreNotSame(saved[0], s);
            Assert.AreEqual(777, s.Width);
            Assert.AreEqual(600, s.Height);
            Assert.IsFalse(s.DualPane);
        }
        Assert.AreEqual(1280, WindowSessions.ForNewWindow(false, true, [], 0, true, null, Dir).Width);
    }

    [TestMethod]
    public void NavigateToReplacesTabsInFreshWindow()
    {
        var s = WindowSessions.ForNewWindow(false, true, [], 0, true, "/dirX", Dir);
        Assert.AreEqual(1, s.Panes[0].Tabs.Count);
        Assert.AreEqual("/dirX", s.Panes[0].Tabs[0].Location);
        Assert.AreEqual(0, s.Panes[0].ActiveTab);
    }

    [TestMethod]
    public void NavigateToPrependsToRestoredTabsAndUsesFileFolder()
    {
        var saved = new List<WindowSession> { Saved("/a") };
        saved[0].Panes[0].ActiveTab = 1;
        var s = WindowSessions.ForNewWindow(true, true, saved, 0, true, "/folder/file.txt", Dir);
        // Path.GetDirectoryName gives "\folder" on Windows and "/folder" elsewhere.
        CollectionAssert.AreEqual(new[] { Path.GetDirectoryName("/folder/file.txt"), "/a", "/two" }, s.Panes[0].Tabs.Select(t => t.Location).ToArray());
        Assert.AreEqual(0, s.Panes[0].ActiveTab);
    }
}
