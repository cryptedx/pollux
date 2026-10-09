using Pollux.Core.Clipboard;

namespace Pollux.Core.Tests.Clipboard;

[TestClass]
public sealed class ClipFileNameTests
{
    private static readonly DateTime Now = new(2026, 3, 4, 9, 5, 7);

    private static bool None(string name) => false;

    [TestMethod]
    public void Text_is_named_after_its_first_line()
    {
        Assert.AreEqual("Meeting notes.txt", ClipFileName.ForText("Meeting notes\nsecond line", Now, None));
    }

    [TestMethod]
    public void Text_skips_leading_blank_lines()
    {
        Assert.AreEqual("Hello.txt", ClipFileName.ForText("\r\n  \r\nHello\r\nWorld", Now, None));
    }

    [TestMethod]
    public void Invalid_file_name_characters_are_replaced()
    {
        Assert.AreEqual("a-b c d e f g h i.txt", ClipFileName.ForText("a/b:c*d?e\"f<g>h|i", Now, None));
        var name = ClipFileName.ForText("C:\\temp\\x?.log", Now, None);
        Assert.AreEqual(-1, name.IndexOfAny(Path.GetInvalidFileNameChars()));
    }

    [TestMethod]
    public void Long_first_line_is_shortened()
    {
        var name = ClipFileName.ForText(new string('x', 200), Now, None);
        Assert.AreEqual(60 + ".txt".Length, name.Length);
    }

    [TestMethod]
    public void Text_without_usable_first_line_uses_timestamp()
    {
        Assert.AreEqual("2026-03-04_090507.txt", ClipFileName.ForText("   \n\t", Now, None));
        Assert.AreEqual("2026-03-04_090507.txt", ClipFileName.ForText("???", Now, None));
    }

    [TestMethod]
    public void Reserved_device_names_are_not_used()
    {
        Assert.AreEqual("2026-03-04_090507.txt", ClipFileName.ForText("CON", Now, None));
    }

    [TestMethod]
    public void Reserved_device_names_followed_by_an_extension_are_not_used()
    {
        Assert.AreEqual("2026-03-04_090507.txt", ClipFileName.ForText("CON.txt", Now, None));
        Assert.AreEqual("2026-03-04_090507.txt", ClipFileName.ForText("aux.log", Now, None));
        Assert.AreEqual("CONTRACT.txt", ClipFileName.ForText("CONTRACT", Now, None));
    }

    [TestMethod]
    public void Image_is_named_after_its_timestamp()
    {
        Assert.AreEqual("2026-03-04_090507.png", ClipFileName.ForImage(Now, None));
    }

    [TestMethod]
    public void Existing_file_is_never_overwritten_Keep_both()
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Notes.txt", "Notes (2).txt" };
        Assert.AreEqual("Notes (3).txt", ClipFileName.ForText("Notes", Now, taken.Contains));
    }

    [TestMethod]
    public void Existing_image_name_gets_a_counter()
    {
        var taken = new HashSet<string> { "2026-03-04_090507.png" };
        Assert.AreEqual("2026-03-04_090507 (2).png", ClipFileName.ForImage(Now, taken.Contains));
    }
}
