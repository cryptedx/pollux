using Pollux.Core.Operations;

namespace Pollux.Core.Tests.Operations;

[TestClass]
public sealed class DropDecisionTests
{
    private const DropOperation CopyMove = DropOperation.Copy | DropOperation.Move;
    private const DropOperation All = DropOperation.Copy | DropOperation.Move | DropOperation.Link;
    private static readonly string[] OnC = [@"C:\src\a.txt", @"C:\src\Docs"];

    private static DropOperation Op(DropKeys keys, string target, DropOperation allowed = CopyMove, params string[] sources) =>
        DropDecision.Decide(keys, sources.Length == 0 ? OnC : sources, target, allowed).Operation;

    [TestMethod]
    public void SameVolumeMovesAndOtherVolumeCopies()
    {
        Assert.AreEqual(DropOperation.Move, Op(DropKeys.None, @"C:\dst"));
        Assert.AreEqual(DropOperation.Move, Op(DropKeys.None, @"c:\dst"));
        Assert.AreEqual(DropOperation.Copy, Op(DropKeys.None, @"D:\dst"));
        Assert.AreEqual(DropOperation.Copy, Op(DropKeys.None, @"\\nas\share\dst"));
        Assert.AreEqual(DropOperation.Move, Op(DropKeys.None, @"\\NAS\Share\b", CopyMove, @"\\nas\share\a\x.txt"));
        Assert.AreEqual(DropOperation.Copy, Op(DropKeys.None, @"\\nas\other\b", CopyMove, @"\\nas\share\a\x.txt"));
    }

    [TestMethod]
    public void MixedVolumesCopy() =>
        Assert.AreEqual(DropOperation.Copy, Op(DropKeys.None, @"C:\dst", CopyMove, @"C:\a.txt", @"D:\b.txt"));

    [TestMethod]
    public void ModifiersOverrideTheDefault()
    {
        Assert.AreEqual(DropOperation.Copy, Op(DropKeys.Control, @"C:\dst"));
        Assert.AreEqual(DropOperation.Move, Op(DropKeys.Shift, @"D:\dst"));
        Assert.AreEqual(DropOperation.Link, Op(DropKeys.Alt, @"C:\dst"));
        Assert.AreEqual(DropOperation.Link, Op(DropKeys.Control | DropKeys.Shift, @"C:\dst"));
    }

    [TestMethod]
    public void DefaultMoveFallsBackToCopyButAForcedMoveIsRefused()
    {
        Assert.AreEqual(DropOperation.Copy, Op(DropKeys.None, @"C:\dst", DropOperation.Copy));
        Assert.AreEqual(DropOperation.None, Op(DropKeys.Shift, @"C:\dst", DropOperation.Copy));
        Assert.AreEqual(DropOperation.None, Op(DropKeys.Control, @"C:\dst", DropOperation.Move));
        Assert.AreEqual(DropOperation.Link, Op(DropKeys.Alt, @"C:\dst", DropOperation.Copy));
        Assert.AreEqual(DropOperation.None, Op(DropKeys.Alt, @"C:\dst", DropOperation.Move));
    }

    [TestMethod]
    public void TheSourcesOwnFolderOnlyTakesShortcuts()
    {
        Assert.AreEqual(DropOperation.None, Op(DropKeys.None, @"C:\src"));
        Assert.AreEqual(DropOperation.None, Op(DropKeys.Control, @"C:\src\"));
        Assert.AreEqual(DropOperation.Link, Op(DropKeys.Alt, @"C:\src"));
        Assert.AreEqual(DropOperation.Move, Op(DropKeys.None, @"C:\src\Other", CopyMove, @"C:\src\a.txt"));
    }

    [TestMethod]
    public void AFolderCannotGoIntoItselfOrItsDescendants()
    {
        Assert.AreEqual(DropOperation.None, Op(DropKeys.None, @"C:\src\Docs"));
        Assert.AreEqual(DropOperation.None, Op(DropKeys.Control, @"C:\src\Docs\Deep"));
        Assert.AreEqual(DropOperation.None, Op(DropKeys.Alt, @"C:\src\docs"));
        Assert.AreEqual(DropOperation.Move, Op(DropKeys.None, @"C:\src\Docs2"));
        Assert.IsTrue(DropDecision.IsValidTarget(OnC, @"C:\src\Docs2"));
        Assert.IsFalse(DropDecision.IsValidTarget(OnC, @"C:\src\Docs\x"));
    }

    [TestMethod]
    public void NothingOrNoTargetIsRefused()
    {
        Assert.AreEqual(DropPlan.Refused, DropDecision.Decide(DropKeys.None, [], @"C:\dst", All));
        Assert.AreEqual(DropPlan.Refused, DropDecision.Decide(DropKeys.None, OnC, "", All));
        Assert.AreEqual(DropOperation.None, Op(DropKeys.None, @"C:\dst", DropOperation.None));
    }

    [TestMethod]
    public void TheRightButtonAsksAtDropTime()
    {
        var plan = DropDecision.Decide(DropKeys.RightButton, OnC, @"D:\dst", CopyMove);
        Assert.AreEqual(new DropPlan(DropOperation.Copy, true), plan);
        Assert.IsFalse(DropDecision.Decide(DropKeys.None, OnC, @"D:\dst", CopyMove).Ask);
        Assert.IsFalse(DropDecision.Decide(DropKeys.RightButton, OnC, @"C:\src", CopyMove).Ask);
    }

    [TestMethod]
    public void ChoicesFollowWhatTheSourceAllows()
    {
        CollectionAssert.AreEqual(new[] { DropOperation.Copy, DropOperation.Move, DropOperation.Link }, DropDecision.Choices(CopyMove).ToArray());
        CollectionAssert.AreEqual(new[] { DropOperation.Copy, DropOperation.Link }, DropDecision.Choices(DropOperation.Copy).ToArray());
        CollectionAssert.AreEqual(new[] { DropOperation.Move }, DropDecision.Choices(DropOperation.Move).ToArray());
    }

    [TestMethod]
    public void ALinkIsReportedAsCopyWhenTheSourceOffersNoLink()
    {
        Assert.AreEqual(DropOperation.Copy, DropDecision.Reported(DropOperation.Link, CopyMove));
        Assert.AreEqual(DropOperation.Link, DropDecision.Reported(DropOperation.Link, All));
        Assert.AreEqual(DropOperation.Move, DropDecision.Reported(DropOperation.Move, CopyMove));
    }

    [TestMethod]
    public void VolumesAreDriveLettersOrShares()
    {
        Assert.AreEqual("C:", DropDecision.Volume(@"c:\x\y"));
        Assert.AreEqual(@"\\NAS\SHARE", DropDecision.Volume(@"\\nas\share\x"));
        Assert.IsNull(DropDecision.Volume(@"\\nas"));
        Assert.IsNull(DropDecision.Volume("relative"));
        Assert.IsFalse(DropDecision.SameVolume("relative", "relative"));
    }

    [TestMethod]
    public void CaptionsNameTheTarget()
    {
        Assert.AreEqual("Move to Docs", new DropPlan(DropOperation.Move, false).Caption("Docs"));
        Assert.AreEqual("Copy to Docs", new DropPlan(DropOperation.Copy, false).Caption("Docs"));
        Assert.AreEqual("Create shortcut in Docs", new DropPlan(DropOperation.Link, false).Caption("Docs"));
        Assert.AreEqual("", DropPlan.Refused.Caption("Docs"));
    }

    [TestMethod]
    public void ItemsOfAReadOnlyLocationAreOnlyCopied()
    {
        string[] zip = [@"C:\a.zip\docs\x.txt"];
        foreach (var keys in new[] { DropKeys.None, DropKeys.Shift, DropKeys.Alt, DropKeys.Control | DropKeys.Shift })
        {
            Assert.AreEqual(DropOperation.Copy, DropDecision.Decide(keys, zip, @"C:\dst", All, copyOnly: true).Operation);
        }

        Assert.IsTrue(DropDecision.Decide(DropKeys.RightButton, zip, @"C:\dst", All, copyOnly: true).Ask);
        Assert.AreEqual(DropOperation.None, DropDecision.Decide(DropKeys.None, zip, @"C:\dst", DropOperation.Move, copyOnly: true).Operation);
        CollectionAssert.AreEqual(new[] { DropOperation.Copy }, DropDecision.Choices(All, copyOnly: true).ToArray());
    }

    [TestMethod]
    public void DrivesAndSharesAreOnlyLinked()
    {
        foreach (var keys in new[] { DropKeys.None, DropKeys.Shift, DropKeys.Control, DropKeys.Alt })
        {
            Assert.AreEqual(DropOperation.Link, Op(keys, @"D:\dst", All, @"C:\"));
            Assert.AreEqual(DropOperation.Link, Op(keys, @"C:\Users\me\Desktop", All, @"C:\"));
            Assert.AreEqual(DropOperation.Link, Op(keys, @"C:\dst", All, @"\\nas\share"));
        }

        Assert.AreEqual(DropOperation.Link, Op(DropKeys.None, @"D:\dst", CopyMove, @"C:\"));
        Assert.AreEqual(DropOperation.None, Op(DropKeys.None, @"D:\dst", DropOperation.Move, @"C:\"));
        Assert.IsTrue(DropDecision.Decide(DropKeys.RightButton, [@"C:\"], @"D:\dst", All).Ask);
        CollectionAssert.AreEqual(new[] { DropOperation.Link }, DropDecision.Choices(All, linkOnly: true).ToArray());
    }

    [TestMethod]
    public void VolumeRootsAreDrivesAndShares()
    {
        Assert.IsTrue(DropDecision.IsVolumeRoot(@"C:\"));
        Assert.IsTrue(DropDecision.IsVolumeRoot("d:"));
        Assert.IsTrue(DropDecision.IsVolumeRoot(@"\\nas\share"));
        Assert.IsTrue(DropDecision.IsVolumeRoot(@"\\nas\share\"));
        Assert.IsFalse(DropDecision.IsVolumeRoot(@"C:\Users"));
        Assert.IsFalse(DropDecision.IsVolumeRoot(@"\\nas\share\dir"));
        Assert.IsFalse(DropDecision.IsVolumeRoot(@"\\nas"));
        Assert.IsFalse(DropDecision.IsVolumeRoot("::{20D04FE0-3AEA-1069-A2D8-08002B30309D}"));
    }
}
