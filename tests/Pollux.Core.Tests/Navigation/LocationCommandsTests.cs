using Pollux.Core.Navigation;

namespace Pollux.Core.Tests.Navigation;

[TestClass]
public sealed class LocationCommandsTests
{
    [TestMethod]
    [DataRow("file.paste")]
    [DataRow("file.delete")]
    [DataRow("file.deletePermanent")]
    [DataRow("file.rename")]
    [DataRow("file.batchRename")]
    [DataRow("file.cut")]
    [DataRow("file.moveToOther")]
    [DataRow("file.newFolder")]
    [DataRow("file.newFile")]
    [DataRow("file.newFromTemplate")]
    [DataRow("file.createShortcut")]
    [DataRow("file.compress")]
    [DataRow("file.extractHere")]
    public void ChangesAreRefusedInEveryVirtualLocation(string id)
    {
        foreach (var kind in new[] { LocationKind.Zip, LocationKind.Server, LocationKind.Shell })
        {
            Assert.AreEqual(LocationCommands.ReadOnly, LocationCommands.Refusal(id, kind));
        }
    }

    [TestMethod]
    [DataRow("file.paste")]
    [DataRow("file.delete")]
    [DataRow("search.inFolder")]
    public void FoldersHomeAndTheBinAreNotRefusedHere(string id)
    {
        Assert.IsNull(LocationCommands.Refusal(id, LocationKind.Folder));
        Assert.IsNull(LocationCommands.Refusal(id, LocationKind.Home));
        Assert.IsNull(LocationCommands.Refusal(id, LocationKind.RecycleBin));
    }

    [TestMethod]
    public void CopyingOutFollowsTheLocation()
    {
        Assert.IsNull(LocationCommands.Refusal("file.copy", LocationKind.Zip));
        Assert.IsNull(LocationCommands.Refusal("file.copyToOther", LocationKind.Shell));
        Assert.AreEqual(LocationCommands.ReadOnly, LocationCommands.Refusal("file.copyToOther", LocationKind.Server));
    }

    [TestMethod]
    public void FolderFeaturesAreUnavailableAndLookingAroundIsNot()
    {
        Assert.AreEqual(LocationCommands.Unavailable, LocationCommands.Refusal("search.inFolder", LocationKind.Zip));
        Assert.AreEqual(LocationCommands.Unavailable, LocationCommands.Refusal("file.createSymlink", LocationKind.Shell));
        foreach (var id in new[] { "file.open", "file.preview", "file.copyPath", "file.selectAll", "nav.up", "view.refresh", "filter.show", "file.properties" })
        {
            Assert.IsNull(LocationCommands.Refusal(id, LocationKind.Zip), id);
        }
    }

    [TestMethod]
    public void DrivesAndSharesAreNeverCopiedOrMovedWhole()
    {
        foreach (var id in new[] { "file.copy", "file.cut", "file.copyToOther", "file.moveToOther" })
        {
            Assert.AreEqual(LocationCommands.WholeVolume, LocationCommands.VolumeRefusal(id, [@"C:\"]), id);
            Assert.AreEqual(LocationCommands.WholeVolume, LocationCommands.VolumeRefusal(id, [@"C:\a.txt", @"\\srv\share"]), id);
            Assert.IsNull(LocationCommands.VolumeRefusal(id, [@"C:\a.txt", @"\\srv\share\x"]), id);
        }

        Assert.IsNull(LocationCommands.VolumeRefusal("file.createShortcut", [@"C:\"]));
    }
}
