using Pollux.Core.Navigation;

namespace Pollux.Core.Tests.Navigation;

[TestClass]
public sealed class BreadcrumbMenuTests
{
    private static FakeLister Disk() => new FakeLister()
        .Add(@"C:\", FakeLister.Dir("b10"), FakeLister.Dir("b2"), FakeLister.Dir("A"), FakeLister.Dir("hid", hidden: true), FakeLister.File("f.txt"))
        .Add(@"\\srv\share", FakeLister.Dir("X"));

    [TestMethod]
    public void ListsOnlyFoldersInNaturalOrder()
    {
        var r = BreadcrumbMenu.Children(@"C:\", false, Disk());
        CollectionAssert.AreEqual(new[] { "A", "b2", "b10" }, r.Items.Select(i => i.Name).ToArray());
        CollectionAssert.AreEqual(new[] { @"C:\A", @"C:\b2", @"C:\b10" }, r.Items.Select(i => i.FullPath).ToArray());
        Assert.AreEqual(0, r.More);
    }

    [TestMethod]
    public void HiddenFollowsFlag()
    {
        Assert.AreEqual(4, BreadcrumbMenu.Children(@"C:\", true, Disk()).Items.Count);
    }

    [TestMethod]
    public void CapCountsTheRest()
    {
        var r = BreadcrumbMenu.Children(@"C:\", true, Disk(), max: 3);
        Assert.AreEqual(3, r.Items.Count);
        Assert.AreEqual(1, r.More);
    }

    [TestMethod]
    public void UncShareAndTrailingSeparator()
    {
        var r = BreadcrumbMenu.Children(@"\\srv\share\", false, Disk());
        Assert.AreEqual(@"\\srv\share\X", r.Items.Single().FullPath);
    }

    [TestMethod]
    public void VirtualAndServerOnlyLocationsHaveNoChildren()
    {
        var lister = Disk();
        Assert.AreEqual(0, BreadcrumbMenu.Children(WinPath.Home, false, lister).Items.Count);
        Assert.AreEqual(0, BreadcrumbMenu.Children(WinPath.RecycleBin, false, lister).Items.Count);
        Assert.AreEqual(0, BreadcrumbMenu.Children(@"\\srv", false, lister).Items.Count);
        Assert.AreEqual(0, lister.Calls.Count);
    }
}
