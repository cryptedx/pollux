using Pollux.Core.Clipboard;

namespace Pollux.Core.Tests.Clipboard;

[TestClass]
public sealed class ClipPasteTests
{
    private static readonly DateTime T0 = new(2026, 10, 1, 12, 0, 0);

    [TestMethod]
    public void EnterPastesIntoTheReadyPreviousWindow()
    {
        var clip = Clip.FromText("hello", T0, "notepad", null);

        var plan = ClipPaste.Plan(clip, onlyCopy: false, PasteTarget.Ready, _ => true);

        Assert.IsTrue(plan.SetClipboard);
        Assert.IsTrue(plan.SendCtrlV);
        Assert.IsNull(plan.Hint);
    }

    [TestMethod]
    public void ShiftEnterOnlyPutsTheClipOnTheClipboard()
    {
        var plan = ClipPaste.Plan(Clip.FromText("hello", T0, null, null), onlyCopy: true, PasteTarget.Ready, _ => true);

        Assert.IsTrue(plan.SetClipboard);
        Assert.IsFalse(plan.SendCtrlV);
        Assert.IsNull(plan.Hint);
    }

    [TestMethod]
    [DataRow(PasteTarget.Elevated)]
    [DataRow(PasteTarget.None)]
    public void WhenCtrlVCannotReachTheTargetTheClipIsCopiedWithAHint(PasteTarget target)
    {
        var plan = ClipPaste.Plan(Clip.FromText("hello", T0, null, null), onlyCopy: false, target, _ => true);

        Assert.IsTrue(plan.SetClipboard);
        Assert.IsFalse(plan.SendCtrlV);
        Assert.AreEqual("Copied – press Ctrl+V", plan.Hint);
    }

    [TestMethod]
    public void AFileClipPastesOnlyTheFilesThatStillExistWithAHint()
    {
        var clip = Clip.FromFiles([@"C:\a.txt", @"C:\gone.txt", @"C:\b.txt"], T0, null, null);

        var plan = ClipPaste.Plan(clip, onlyCopy: false, PasteTarget.Ready, f => f != @"C:\gone.txt");

        Assert.IsTrue(plan.SetClipboard);
        Assert.IsTrue(plan.SendCtrlV);
        CollectionAssert.AreEqual(new[] { @"C:\a.txt", @"C:\b.txt" }, plan.Files.ToArray());
        Assert.AreEqual("1 of 3 files no longer exists", plan.Hint);
    }

    [TestMethod]
    public void AFileClipWhoseFilesAreAllGoneLeavesTheClipboardAlone()
    {
        var clip = Clip.FromFiles([@"C:\gone.txt", @"C:\gone2.txt"], T0, null, null);

        var plan = ClipPaste.Plan(clip, onlyCopy: false, PasteTarget.Ready, _ => false);

        Assert.IsFalse(plan.SetClipboard);
        Assert.IsFalse(plan.SendCtrlV);
        Assert.AreEqual("The files of this Clip no longer exist", plan.Hint);
    }

    [TestMethod]
    public void AnElevatedTargetAndMissingFilesShowBothHints()
    {
        var clip = Clip.FromFiles([@"C:\a.txt", @"C:\gone.txt"], T0, null, null);

        var plan = ClipPaste.Plan(clip, onlyCopy: false, PasteTarget.Elevated, f => f == @"C:\a.txt");

        Assert.IsFalse(plan.SendCtrlV);
        Assert.AreEqual("Copied – press Ctrl+V · 1 of 2 files no longer exists", plan.Hint);
    }
}
