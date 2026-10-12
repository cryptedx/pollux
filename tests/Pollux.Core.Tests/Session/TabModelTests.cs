using Pollux.Core.Navigation;
using Pollux.Core.Session;

namespace Pollux.Core.Tests.Session;

[TestClass]
public sealed class TabModelTests
{
    private static bool ZipExists(string path) => path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);

    [TestMethod]
    public void FolderOnlyForFilesystemFolders()
    {
        Assert.AreEqual(@"C:\data", new TabModel(@"C:\data", ZipExists).Folder);
        Assert.AreEqual(@"\\nas\share", new TabModel(@"\\nas\share", ZipExists).Folder);
        Assert.IsNull(new TabModel(WinPath.Home, ZipExists).Folder);
        Assert.IsNull(new TabModel(WinPath.RecycleBin, ZipExists).Folder);
        Assert.IsNull(new TabModel(WinPath.ThisPc, ZipExists).Folder);
        Assert.IsNull(new TabModel(@"\\nas", ZipExists).Folder);
        Assert.IsNull(new TabModel(@"C:\a.zip\docs", ZipExists).Folder);
    }

    [TestMethod]
    public void KindFollowsNavigation()
    {
        var tab = new TabModel(@"C:\data", ZipExists);
        Assert.AreEqual(LocationKind.Folder, tab.Kind);
        tab.History.Navigate(@"C:\data\a.zip");
        Assert.AreEqual(LocationKind.Zip, tab.Kind);
        Assert.IsFalse(tab.Access.CanModify);
        Assert.IsTrue(tab.Access.CanCopyOut);
        tab.History.Back();
        Assert.AreEqual(LocationKind.Folder, tab.Kind);
        Assert.IsTrue(tab.Access.CanModify);
    }

    [TestMethod]
    public void Titles()
    {
        Assert.AreEqual("Home", new TabModel(WinPath.Home).Title);
        Assert.AreEqual("Recycle Bin", new TabModel(WinPath.RecycleBin).Title);
        Assert.AreEqual("This PC", new TabModel(WinPath.ThisPc).Title);
        Assert.AreEqual("nas", new TabModel(@"\\nas").Title);
        Assert.AreEqual("docs", new TabModel(@"C:\data\docs").Title);
    }

    [TestMethod]
    public void ShellDisplayNameIsTheTitleOnlyWhileShown()
    {
        const string phone = "pollux:shell:uuid:1";
        var tab = new TabModel(phone);
        Assert.AreEqual("uuid:1", tab.Title);
        tab.SetDisplayName(phone, "Galaxy S24");
        Assert.AreEqual("Galaxy S24", tab.Title);
        tab.History.Navigate(@"C:\x");
        Assert.AreEqual("x", tab.Title);
        tab.History.Back();
        Assert.AreEqual("Galaxy S24", tab.Title);
    }
}
