using System.Buffers.Binary;
using System.Text;
using Pollux.Core.Commands;
using Pollux.Core.Navigation;
using Pollux.Core.Recycle;
using Pollux.Core.Session;
using Pollux.Core.Settings;

namespace Pollux.Core.Tests.Recycle;

[TestClass]
public sealed class RecycleBinTests
{
    private const string Recycled = @"C:\$Recycle.Bin\S-1-5-21-1-1001\$RAB12CD.txt";
    private static readonly DateTime Deleted = new(2026, 10, 9, 14, 30, 0, DateTimeKind.Utc);

    /// <summary>A Windows 10+ "$I" file (version 2): header, size, deletion time, path length in chars, path.</summary>
    private static byte[] InfoV2(string originalPath, long size, DateTime deletedUtc)
    {
        var path = Encoding.Unicode.GetBytes(originalPath + "\0");
        var data = new byte[28 + path.Length];
        BinaryPrimitives.WriteInt64LittleEndian(data, 2);
        BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(8), size);
        BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(16), deletedUtc.ToFileTimeUtc());
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(24), originalPath.Length + 1);
        path.CopyTo(data, 28);
        return data;
    }

    /// <summary>A Vista–8.1 "$I" file (version 1): the path is a fixed 260-char buffer.</summary>
    private static byte[] InfoV1(string originalPath, long size, DateTime deletedUtc)
    {
        var data = new byte[24 + 520];
        BinaryPrimitives.WriteInt64LittleEndian(data, 1);
        BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(8), size);
        BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(16), deletedUtc.ToFileTimeUtc());
        Encoding.Unicode.GetBytes(originalPath).CopyTo(data, 24);
        return data;
    }

    // ---------------- the location ----------------

    [TestMethod]
    public void RecycleBinTabHasATitleAndNoFolder()
    {
        var tab = new TabModel(WinPath.RecycleBin);

        Assert.IsTrue(tab.IsRecycleBin);
        Assert.IsFalse(tab.IsHome);
        Assert.AreEqual("Recycle Bin", tab.Title);
        Assert.IsNull(tab.Folder);
    }

    [TestMethod]
    public void FolderTabsKeepTheirFolder()
    {
        Assert.AreEqual(@"C:\work", new TabModel(@"C:\work").Folder);
        Assert.IsNull(new TabModel(WinPath.Home).Folder);
        Assert.IsFalse(new TabModel(@"C:\work").IsRecycleBin);
    }

    [TestMethod]
    public void RecycleBinHasNoParent() => Assert.IsNull(WinPath.Parent(WinPath.RecycleBin));

    [TestMethod]
    [DataRow("recycle bin")]
    [DataRow("Recycle Bin")]
    [DataRow("recyclebin")]
    [DataRow("shell:RecycleBinFolder")]
    [DataRow("  pollux:recycle-bin ")]
    public void TypedNamesOfTheRecycleBin(string typed) => Assert.AreEqual(WinPath.RecycleBin, WinPath.WellKnown(typed));

    [TestMethod]
    [DataRow("home")]
    [DataRow("pollux:home")]
    public void TypedNamesOfHome(string typed) => Assert.AreEqual(WinPath.Home, WinPath.WellKnown(typed));

    [TestMethod]
    [DataRow(@"C:\recycle bin")]
    [DataRow("recycle")]
    [DataRow("")]
    public void OtherTextIsNoWellKnownLocation(string typed) => Assert.IsNull(WinPath.WellKnown(typed));

    // ---------------- $I files ----------------

    [TestMethod]
    public void InfoFileSitsNextToTheRecycledFile() =>
        Assert.AreEqual(@"C:\$Recycle.Bin\S-1-5-21-1-1001\$IAB12CD.txt", RecycleInfoFile.InfoPathFor(Recycled));

    [TestMethod]
    [DataRow(@"C:\$Recycle.Bin\S-1-5-21-1-1001\desktop.ini")]
    [DataRow(@"C:\$Recycle.Bin\S-1-5-21-1-1001\$IAB12CD.txt")]
    [DataRow("$R")]
    public void OnlyRecycledNamesHaveAnInfoFile(string path) => Assert.IsNull(RecycleInfoFile.InfoPathFor(path));

    [TestMethod]
    public void ParsesVersion2()
    {
        var item = RecycleInfoFile.Parse(InfoV2(@"C:\Users\marko\Documents\report.txt", 1234, Deleted), Recycled, isDirectory: false);

        Assert.IsNotNull(item);
        Assert.AreEqual(@"C:\Users\marko\Documents\report.txt", item.OriginalPath);
        Assert.AreEqual("report.txt", item.Name);
        Assert.AreEqual(@"C:\Users\marko\Documents", item.OriginalFolder);
        Assert.AreEqual(1234, item.Size);
        Assert.AreEqual(Deleted, item.DeletedUtc);
        Assert.AreEqual(Recycled, item.RecycledPath);
        Assert.IsFalse(item.IsDirectory);
    }

    [TestMethod]
    public void ParsesVersion1()
    {
        var item = RecycleInfoFile.Parse(InfoV1(@"D:\old\notes", 99, Deleted), @"D:\$Recycle.Bin\S-1\$R1", isDirectory: true);

        Assert.IsNotNull(item);
        Assert.AreEqual(@"D:\old\notes", item.OriginalPath);
        Assert.AreEqual(99, item.Size);
        Assert.AreEqual(Deleted, item.DeletedUtc);
        Assert.IsTrue(item.IsDirectory);
    }

    [TestMethod]
    public void RejectsTruncatedOrUnknownInfoFiles()
    {
        var good = InfoV2(@"C:\a.txt", 1, Deleted);
        var unknown = (byte[])good.Clone();
        BinaryPrimitives.WriteInt64LittleEndian(unknown, 7);
        var badTime = (byte[])good.Clone();
        BinaryPrimitives.WriteInt64LittleEndian(badTime.AsSpan(16), -1);

        Assert.IsNull(RecycleInfoFile.Parse(good.AsSpan(0, 20), Recycled, false));
        Assert.IsNull(RecycleInfoFile.Parse(unknown, Recycled, false));
        Assert.IsNull(RecycleInfoFile.Parse(badTime, Recycled, false));
        Assert.IsNull(RecycleInfoFile.Parse(InfoV2("", 1, Deleted), Recycled, false));
    }

    [TestMethod]
    public void PathLengthBeyondTheFileIsCutAtTheEnd()
    {
        var data = InfoV2(@"C:\a.txt", 1, Deleted);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(24), 5000);

        Assert.AreEqual(@"C:\a.txt", RecycleInfoFile.Parse(data, Recycled, false)?.OriginalPath);
    }

    [TestMethod]
    public void EntryShowsTheOriginalNameAndIsIdentifiedByTheRecycledPath()
    {
        var item = new RecycledItem(@"C:\Users\marko\report.txt", Deleted, 1234, false, Recycled);

        var entry = item.ToEntry();

        Assert.AreEqual("report.txt", entry.Name);
        Assert.AreEqual(Recycled, entry.FullPath);
        Assert.AreEqual(@"C:\Users\marko", entry.OriginalLocation);
        Assert.AreEqual(Deleted.ToLocalTime(), entry.Modified);
        Assert.AreEqual(1234, entry.Size);
        Assert.AreEqual("txt", entry.Extension);
    }

    [TestMethod]
    public void ItemWithoutKnownOriginHasNoFolder()
    {
        var item = new RecycledItem("report.txt", Deleted, 0, false, Recycled);

        Assert.AreEqual("report.txt", item.Name);
        Assert.AreEqual("", item.OriginalFolder);
    }

    // ---------------- commands ----------------

    [TestMethod]
    [DataRow("file.delete")]
    [DataRow("file.deletePermanent")]
    public void DeletingInTheRecycleBinIsForGood(string id) =>
        Assert.AreEqual(RecycleBinCommands.DeletePermanent, RecycleBinCommands.Route(id, inRecycleBin: true));

    [TestMethod]
    [DataRow("file.open")]
    [DataRow("file.rename")]
    [DataRow("file.copy")]
    [DataRow("file.cut")]
    [DataRow("file.paste")]
    [DataRow("file.copyToOther")]
    [DataRow("file.moveToOther")]
    [DataRow("file.newFolder")]
    [DataRow("file.openWith")]
    [DataRow("file.properties")]
    [DataRow("file.copyPath")]
    [DataRow("file.shellMenu")]
    [DataRow("file.showInExplorer")]
    [DataRow("bookmark.addSelection")]
    [DataRow("integration.vscode")]
    [DataRow("integration.copilot")]
    [DataRow("tab.moveToNewWindow")]
    [DataRow("copilot.action.explain")]
    public void FileCommandsAreIgnoredInTheRecycleBin(string id) => Assert.IsNull(RecycleBinCommands.Route(id, inRecycleBin: true));

    [TestMethod]
    [DataRow("file.preview")]
    [DataRow("file.selectAll")]
    [DataRow("file.selectNone")]
    [DataRow("file.invertSelection")]
    [DataRow("file.selectByPattern")]
    [DataRow("file.deselectByPattern")]
    [DataRow("view.refresh")]
    [DataRow("nav.back")]
    [DataRow("tab.new")]
    [DataRow("filter.show")]
    [DataRow(RecycleBinCommands.Restore)]
    [DataRow(RecycleBinCommands.DeletePermanent)]
    [DataRow(RecycleBinCommands.Empty)]
    public void ViewingAndRecycleCommandsRunInTheRecycleBin(string id) => Assert.AreEqual(id, RecycleBinCommands.Route(id, inRecycleBin: true));

    [TestMethod]
    public void RecycleBinCommandsAreInThePalette()
    {
        var ids = CommandRegistry.Commands(new CopilotSettings(), copilotAvailable: false).Where(c => c.ShowInPalette).Select(c => c.Id).ToList();

        CollectionAssert.IsSubsetOf(new[] { "nav.recycleBin", RecycleBinCommands.Restore, RecycleBinCommands.DeletePermanent, RecycleBinCommands.Empty }, ids);
    }

    [TestMethod]
    public void RestoreAndDeleteNeedTheRecycleBin()
    {
        Assert.IsNull(RecycleBinCommands.Route(RecycleBinCommands.Restore, inRecycleBin: false));
        Assert.IsNull(RecycleBinCommands.Route(RecycleBinCommands.DeletePermanent, inRecycleBin: false));
        Assert.AreEqual(RecycleBinCommands.Empty, RecycleBinCommands.Route(RecycleBinCommands.Empty, inRecycleBin: false));
        Assert.AreEqual("file.copy", RecycleBinCommands.Route("file.copy", inRecycleBin: false));
        Assert.AreEqual("file.delete", RecycleBinCommands.Route("file.delete", inRecycleBin: false));
    }

    [TestMethod]
    public void QuestionsNameWhatIsLostForGood()
    {
        Assert.AreEqual("Permanently delete \"a.txt\"? This cannot be undone.", RecycleBinCommands.DeleteQuestion(["a.txt"]));
        Assert.AreEqual("Permanently delete these 3 items? This cannot be undone.", RecycleBinCommands.DeleteQuestion(["a", "b", "c"]));
        Assert.AreEqual("Permanently delete 1 item (612 B) in the Recycle Bin? This cannot be undone.", RecycleBinCommands.EmptyQuestion(1, 612));
        Assert.AreEqual("Permanently delete 12 items (1.5 K) in the Recycle Bin? This cannot be undone.", RecycleBinCommands.EmptyQuestion(12, 1536));
        Assert.AreEqual("Permanently delete everything in the Recycle Bin? This cannot be undone.", RecycleBinCommands.EmptyQuestion(null, 0));
    }
}
