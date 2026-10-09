using Pollux.Core.Clipboard;

namespace Pollux.Core.Tests.Clipboard;

[TestClass]
public sealed class ClipLabelTests
{
    private static readonly DateTime T0 = new(2026, 10, 1, 12, 0, 0);

    [TestMethod]
    public void TextShowsItsFirstNonBlankLineTrimmed() =>
        Assert.AreEqual("hello world", ClipLabel.For(Clip.FromText("\r\n   \n  hello world  \r\nsecond", T0, null, null)));

    [TestMethod]
    public void LongTextIsCut()
    {
        var label = ClipLabel.For(Clip.FromText(new string('x', 500), T0, null, null));

        Assert.AreEqual(new string('x', 200) + "…", label);
    }

    [TestMethod]
    public void TabsInTextBecomeSpaces() =>
        Assert.AreEqual("a b", ClipLabel.For(Clip.FromText("a\tb", T0, null, null)));

    [TestMethod]
    public void WhitespaceOnlyText() =>
        Assert.AreEqual("(blank)", ClipLabel.For(Clip.FromText(" \n ", T0, null, null)));

    [TestMethod]
    public void FilesShowTheFirstNameAndHowManyMore()
    {
        Assert.AreEqual("a.txt", ClipLabel.For(Clip.FromFiles([@"C:\x\a.txt"], T0, null, null)));
        Assert.AreEqual("a.txt + 2 more", ClipLabel.For(Clip.FromFiles([@"C:\x\a.txt", @"C:\x\b", @"C:\c.md"], T0, null, null)));
    }

    [TestMethod]
    public void ImagesAreLabelledImage() =>
        Assert.AreEqual("Image", ClipLabel.For(Clip.FromImage([1, 2, 3], T0, null, null)));

    [TestMethod]
    public void AKeptClipTitleWins()
    {
        var history = new ClipboardHistory();
        var clip = history.Add(Clip.FromText("ssh admin@host", T0, null, null), T0);
        history.Keep(clip.Id, "Server");

        Assert.AreEqual("Server", ClipLabel.For(clip));
    }
}
