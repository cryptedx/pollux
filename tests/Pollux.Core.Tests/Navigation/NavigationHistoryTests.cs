using Pollux.Core.Navigation;

namespace Pollux.Core.Tests.Navigation;

[TestClass]
public sealed class NavigationHistoryTests
{
    [TestMethod]
    public void BackAndForward()
    {
        var h = new NavigationHistory(@"C:\a");
        h.Navigate(@"C:\b");
        h.Navigate(@"C:\c");

        Assert.AreEqual(@"C:\b", h.Back());
        Assert.AreEqual(@"C:\a", h.Back());
        Assert.IsNull(h.Back());
        Assert.AreEqual(@"C:\b", h.Forward());
        Assert.AreEqual(@"C:\b", h.Current);
    }

    [TestMethod]
    public void NavigatingDropsForwardEntries()
    {
        var h = new NavigationHistory(@"C:\a");
        h.Navigate(@"C:\b");
        h.Back();
        h.Navigate(@"C:\x");

        Assert.IsFalse(h.CanGoForward);
        Assert.AreEqual(@"C:\a", h.Back());
    }

    [TestMethod]
    public void NavigatingToCurrentIsIgnored()
    {
        var h = new NavigationHistory(@"C:\a");
        h.Navigate(@"C:\A");

        Assert.IsFalse(h.CanGoBack);
    }

    [TestMethod]
    public void HistoryIsCapped()
    {
        var h = new NavigationHistory(@"C:\0", capacity: 3);
        for (var i = 1; i <= 5; i++) h.Navigate($@"C:\{i}");

        Assert.AreEqual(@"C:\4", h.Back());
        Assert.AreEqual(@"C:\3", h.Back());
        Assert.IsNull(h.Back());
    }
}
