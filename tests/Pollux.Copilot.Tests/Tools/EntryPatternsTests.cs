using Pollux.Copilot.Tools;

namespace Pollux.Copilot.Tests.Tools;

[TestClass]
public sealed class EntryPatternsTests
{
    private static readonly PaneEntry[] Entries =
    [
        new("report.pdf", false), new("Scan.PDF", false), new("notes.txt", false),
        new("archive.pdf.zip", false), new("pdfs", true), new("old.pdf", true),
    ];

    private static string[] Match(string[] patterns, EntryKind kind = EntryKind.Any) =>
        [.. EntryPatterns.Match(Entries, patterns, kind).Select(e => e.Name)];

    [TestMethod]
    public void StarMatchesAnyRunCaseInsensitively() =>
        CollectionAssert.AreEqual(new[] { "report.pdf", "Scan.PDF", "old.pdf" }, Match(["*.pdf"]));

    [TestMethod]
    public void NameWithoutWildcardMatchesExactlyButIgnoringCase() =>
        CollectionAssert.AreEqual(new[] { "notes.txt" }, Match(["NOTES.txt"]));

    [TestMethod]
    public void QuestionMarkMatchesOneCharacter() =>
        CollectionAssert.AreEqual(new[] { "pdfs" }, Match(["pdf?"]));

    [TestMethod]
    public void SeveralPatternsAreUnitedWithoutDuplicates() =>
        CollectionAssert.AreEqual(new[] { "report.pdf", "Scan.PDF", "notes.txt", "old.pdf" }, Match(["*.pdf", "*.txt", "report.*"]));

    [TestMethod]
    public void KindRestrictsToFilesOrFolders()
    {
        CollectionAssert.AreEqual(new[] { "report.pdf", "Scan.PDF" }, Match(["*.pdf"], EntryKind.File));
        CollectionAssert.AreEqual(new[] { "old.pdf" }, Match(["*.pdf"], EntryKind.Folder));
    }

    [TestMethod]
    public void EmptyPatternsMatchNothing() => Assert.AreEqual(0, Match([]).Length);

    [TestMethod]
    public void KindParsesLeniently()
    {
        Assert.AreEqual(EntryKind.File, EntryPatterns.ParseKind("Files"));
        Assert.AreEqual(EntryKind.Folder, EntryPatterns.ParseKind("folder"));
        Assert.AreEqual(EntryKind.Any, EntryPatterns.ParseKind(null));
        Assert.AreEqual(EntryKind.Any, EntryPatterns.ParseKind("any"));
    }
}
