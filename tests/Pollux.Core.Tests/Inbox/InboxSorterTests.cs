using Pollux.Core.Files;
using Pollux.Core.Inbox;

namespace Pollux.Core.Tests.Inbox;

[TestClass]
public sealed class InboxSorterTests
{
    private static FileEntry F(string name, FileAttributes a = FileAttributes.Normal) => new(name, @"C:\Users\m\Desktop\" + name, false, 1, DateTime.Now, a);

    [TestMethod]
    public void SortsByCategorySkippingShortcutsHiddenAndDesktopIni()
    {
        var plan = InboxSorter.Plan(
            [F("Rechnung.pdf"), F("shot.PNG"), F("setup.exe"), F("Teams.lnk"), F("desktop.ini", FileAttributes.Hidden | FileAttributes.System), F("weird.xyz"), new FileEntry("Proj", @"C:\Users\m\Desktop\Proj", true, 0, DateTime.Now, FileAttributes.Directory)],
            @"C:\Users\m\Documents\Inbox");

        CollectionAssert.AreEquivalent(new[] { "Documents", "Images", "Installers", "Other" }, plan.Select(p => p.Category).ToArray());
        Assert.AreEqual(@"C:\Users\m\Documents\Inbox" + Path.DirectorySeparatorChar + "Images", plan.Single(p => p.Category == "Images").TargetDirectory);
    }

    [TestMethod]
    public void ShortcutsAndFoldersOnRequest()
    {
        var plan = InboxSorter.Plan([F("Teams.lnk")], "T", includeShortcuts: true);

        Assert.AreEqual("Shortcuts", plan.Single().Category);
    }
}
