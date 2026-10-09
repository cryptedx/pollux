using Pollux.Core.Clipboard;
using Pollux.Core.Settings;

namespace Pollux.Core.Tests.Clipboard;

[TestClass]
public sealed class ClipCaptureTests
{
    private static readonly DateTime T0 = new(2026, 10, 1, 12, 0, 0);

    private static ClipboardSettings On() => new() { Enabled = true };

    [TestMethod]
    public void CopiedTextBecomesATextClipWithItsSource()
    {
        var copied = new CopiedContent { Text = "hello", SourceApp = "notepad", SourcePath = @"C:\Windows\notepad.exe" };

        var result = ClipCapture.Decide(copied, On(), T0);

        Assert.AreEqual(CaptureSkip.None, result.Skip);
        Assert.IsNotNull(result.Clip);
        Assert.AreEqual(ClipKind.Text, result.Clip.Kind);
        Assert.AreEqual("hello", result.Clip.Text);
        Assert.AreEqual("notepad", result.Clip.SourceApp);
        Assert.AreEqual(@"C:\Windows\notepad.exe", result.Clip.SourcePath);
        Assert.AreEqual(T0, result.Clip.CapturedAt);
    }

    [TestMethod]
    public void CopiedFilesBecomeAFileListClipEvenWhenTextComesAlong()
    {
        var copied = new CopiedContent { Files = [@"C:\a.txt", @"C:\b.txt"], Text = @"C:\a.txt", SourceApp = "explorer" };

        var clip = ClipCapture.Decide(copied, On(), T0).Clip;

        Assert.IsNotNull(clip);
        Assert.AreEqual(ClipKind.Files, clip.Kind);
        CollectionAssert.AreEqual(new[] { @"C:\a.txt", @"C:\b.txt" }, clip.Files);
    }

    [TestMethod]
    public void CopiedImageBecomesAnImageClip()
    {
        var copied = new CopiedContent { Image = [1, 2, 3], SourceApp = "mspaint" };

        var clip = ClipCapture.Decide(copied, On(), T0).Clip;

        Assert.IsNotNull(clip);
        Assert.AreEqual(ClipKind.Image, clip.Kind);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, clip.Image);
    }

    [TestMethod]
    public void TextWinsOverAnImageOfTheSameCopy()
    {
        var copied = new CopiedContent { Text = "A1\tB1", Image = [1, 2, 3], SourceApp = "EXCEL" };

        Assert.AreEqual(ClipKind.Text, ClipCapture.Decide(copied, On(), T0).Clip?.Kind);
    }

    [TestMethod]
    public void BlankTextIsNotAClip()
    {
        var withImage = new CopiedContent { Text = " \r\n", Image = [1, 2, 3] };
        var alone = new CopiedContent { Text = "  " };

        Assert.AreEqual(ClipKind.Image, ClipCapture.Decide(withImage, On(), T0).Clip?.Kind);
        var skipped = ClipCapture.Decide(alone, On(), T0);
        Assert.IsNull(skipped.Clip);
        Assert.AreEqual(CaptureSkip.Empty, skipped.Skip);
        Assert.AreEqual(CaptureSkip.Empty, ClipCapture.Decide(new CopiedContent(), On(), T0).Skip);
    }

    [TestMethod]
    public void ContentAPasswordManagerMarksAsPrivateIsNotRecorded()
    {
        var copied = new CopiedContent { Text = "hunter2", SourceApp = "KeePass", Private = true };

        var result = ClipCapture.Decide(copied, On(), T0);

        Assert.IsNull(result.Clip);
        Assert.AreEqual(CaptureSkip.Private, result.Skip);
    }

    [TestMethod]
    public void CopiesFromAnExcludedProcessAreNotRecorded()
    {
        var settings = On();
        settings.ExcludeProcesses = ["keepassxc", "Bitwarden.exe"];

        Assert.AreEqual(CaptureSkip.ExcludedApp, ClipCapture.Decide(new() { Text = "x", SourceApp = "KeePassXC" }, settings, T0).Skip);
        Assert.AreEqual(CaptureSkip.ExcludedApp, ClipCapture.Decide(new() { Text = "x", SourceApp = "bitwarden" }, settings, T0).Skip);
        Assert.IsNull(ClipCapture.Decide(new() { Text = "x", SourceApp = "bitwarden" }, settings, T0).Clip);
        Assert.AreEqual(CaptureSkip.None, ClipCapture.Decide(new() { Text = "x", SourceApp = "KeePassXC-Browser" }, settings, T0).Skip);
        Assert.AreEqual(CaptureSkip.None, ClipCapture.Decide(new() { Text = "x" }, settings, T0).Skip);
    }

    [TestMethod]
    public void NothingIsRecordedWhilePaused()
    {
        var settings = On();
        settings.Paused = true;

        var result = ClipCapture.Decide(new() { Text = "x" }, settings, T0);

        Assert.IsNull(result.Clip);
        Assert.AreEqual(CaptureSkip.Paused, result.Skip);
    }

    [TestMethod]
    public void TextLargerThanTheLimitIsNotRecorded()
    {
        var settings = On();
        settings.MaxTextKilobytes = 1;

        Assert.AreEqual(CaptureSkip.None, ClipCapture.Decide(new() { Text = new string('a', 1024) }, settings, T0).Skip);
        var tooLarge = ClipCapture.Decide(new() { Text = new string('a', 1025), Image = [1] }, settings, T0);
        Assert.IsNull(tooLarge.Clip);
        Assert.AreEqual(CaptureSkip.TooLarge, tooLarge.Skip);
        // The limit is on the stored (UTF-8) size: 512 "ä" are 1024 bytes, 513 are too many.
        Assert.AreEqual(CaptureSkip.None, ClipCapture.Decide(new() { Text = new string('ä', 512) }, settings, T0).Skip);
        Assert.AreEqual(CaptureSkip.TooLarge, ClipCapture.Decide(new() { Text = new string('ä', 513) }, settings, T0).Skip);
    }

    [TestMethod]
    public void ImagesLargerThanTheLimitAreNotRecorded()
    {
        var settings = On();
        settings.MaxImageMegabytes = 1;

        Assert.AreEqual(CaptureSkip.None, ClipCapture.Decide(new() { Image = new byte[1024 * 1024] }, settings, T0).Skip);
        Assert.AreEqual(CaptureSkip.TooLarge, ClipCapture.Decide(new() { Image = new byte[1024 * 1024 + 1] }, settings, T0).Skip);
    }

    [TestMethod]
    public void DefaultLimitsAreOneMegabyteOfTextAndTwentyOfImage()
    {
        Assert.AreEqual(CaptureSkip.None, ClipCapture.Decide(new() { Text = new string('a', 1024 * 1024) }, On(), T0).Skip);
        Assert.AreEqual(CaptureSkip.TooLarge, ClipCapture.Decide(new() { Text = new string('a', 1024 * 1024 + 1) }, On(), T0).Skip);
        Assert.AreEqual(CaptureSkip.None, ClipCapture.Decide(new() { Image = new byte[20 * 1024 * 1024] }, On(), T0).Skip);
        Assert.AreEqual(CaptureSkip.TooLarge, ClipCapture.Decide(new() { Image = new byte[20 * 1024 * 1024 + 1] }, On(), T0).Skip);
    }

    [TestMethod]
    public void ALimitOfZeroMeansNoLimit()
    {
        var settings = On();
        settings.MaxTextKilobytes = 0;
        settings.MaxImageMegabytes = 0;

        Assert.AreEqual(CaptureSkip.None, ClipCapture.Decide(new() { Text = new string('a', 2 * 1024 * 1024) }, settings, T0).Skip);
        Assert.AreEqual(CaptureSkip.None, ClipCapture.Decide(new() { Image = new byte[21 * 1024 * 1024] }, settings, T0).Skip);
    }

    [TestMethod]
    public void SkippedLargeCopyGetsALogLineWithoutItsContent()
    {
        var settings = On();
        settings.MaxTextKilobytes = 1;

        var result = ClipCapture.Decide(new() { Text = "secret" + new string('a', 2000), SourceApp = "notepad" }, settings, T0);

        Assert.IsNotNull(result.LogLine);
        StringAssert.Contains(result.LogLine, "text");
        StringAssert.Contains(result.LogLine, "2006 bytes");
        StringAssert.Contains(result.LogLine, "notepad");
        Assert.IsFalse(result.LogLine.Contains("secret", StringComparison.Ordinal));
    }
}
