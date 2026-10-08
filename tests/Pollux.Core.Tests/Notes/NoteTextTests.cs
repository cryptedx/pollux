using Pollux.Core.Notes;

namespace Pollux.Core.Tests.Notes;

[TestClass]
public sealed class NoteTextTests
{
    [TestMethod]
    public void TagsFromFrontMatterAndInline()
    {
        var text = """
            ---
            tags: [pollux, Idee]
            ---
            # Plan
            Remember #todo and #pollux, not a#tag or #123 or `#code`.
            """;

        CollectionAssert.AreEquivalent(new[] { "pollux", "idee", "todo" }, NoteText.Tags(text).ToArray());
    }

    [TestMethod]
    public void TagsFromYamlListForm()
    {
        var text = "---\ntags:\n  - work\n  - meeting\n---\nbody";

        CollectionAssert.AreEquivalent(new[] { "work", "meeting" }, NoteText.Tags(text).ToArray());
    }

    [TestMethod]
    public void HeadingsAreNotTags() => Assert.AreEqual(0, NoteText.Tags("# Title\n## Sub").Count);

    [TestMethod]
    public void TitleIsFirstContentLineWithoutMarkdown()
    {
        Assert.AreEqual("Plan for Monday", NoteText.Title("---\ntags: [a]\n---\n\n# Plan for Monday\nmore"));
        Assert.AreEqual("", NoteText.Title("   \n\n"));
    }

    [TestMethod]
    public void FileNameFromTitleOrTimestamp()
    {
        var created = new DateTime(2026, 10, 8, 14, 32, 0);

        Assert.AreEqual("Plan for Monday.md", NoteText.FileName("# Plan for Monday", created));
        Assert.AreEqual("a-b c.md", NoteText.FileName("a/b: c?", created));
        Assert.AreEqual("2026-10-08_1432.md", NoteText.FileName("", created));
        Assert.AreEqual(60 + 3, NoteText.FileName(new string('x', 200), created).Length);
    }

    [TestMethod]
    public void SetTagsWritesFrontMatter()
    {
        Assert.AreEqual("---\ntags: [a, b]\n---\nbody", NoteText.SetTags("body", ["a", "b"]));
        Assert.AreEqual("---\ntitle: x\ntags: [c]\n---\nbody", NoteText.SetTags("---\ntitle: x\ntags: [a]\n---\nbody", ["c"]));
        Assert.AreEqual("---\ntags: [x]\n---\nbody", NoteText.SetTags("---\n---\nbody", ["x"]));
    }
}
