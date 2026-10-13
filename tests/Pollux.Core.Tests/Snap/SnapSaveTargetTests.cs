using Pollux.Core.Snap;

namespace Pollux.Core.Tests.Snap;

[TestClass]
public sealed class SnapSaveTargetTests
{
    private const string Folder = @"C:\Users\me\Pictures\Snaps";
    private static readonly DateTime Now = new(2026, 10, 12, 15, 0, 0);

    [TestMethod]
    public void SavingAnAutoSavedSnapOverwritesItsFile()
    {
        var saved = Path.Combine(Folder, "Snap 2026-10-12 14-03-22.png");

        Assert.AreEqual(saved, SnapSaveTarget.For(saved, Folder, Now, _ => true));
    }

    [TestMethod]
    public void SavingASnapThatWasNeverSavedCreatesANewFileInTheSnapsFolder()
    {
        Assert.AreEqual(Path.Combine(Folder, "Snap 2026-10-12 15-00-00.png"), SnapSaveTarget.For(null, Folder, Now, _ => false));
    }

    [TestMethod]
    public void TheNewFileNeverReplacesAnother()
    {
        var taken = Path.Combine(Folder, "Snap 2026-10-12 15-00-00.png");

        Assert.AreEqual(Path.Combine(Folder, "Snap 2026-10-12 15-00-00 (2).png"), SnapSaveTarget.For(null, Folder, Now, p => p == taken));
    }
}
