using Pollux.Core.Files;
using Pollux.Core.Search;

namespace Pollux.Core.Tests.Search;

[TestClass]
public sealed class FolderSearchQueryTests
{
    private static readonly DateTime Now = new(2026, 10, 11, 15, 30, 0);

    private static FolderSearchQuery Parse(string text, string? content = null)
    {
        var parsed = FolderSearchQuery.Parse(text, content, Now);
        Assert.IsNull(parsed.Error, parsed.Error);
        return parsed.Query!;
    }

    private static string Error(string text, string? content = null)
    {
        var parsed = FolderSearchQuery.Parse(text, content, Now);
        Assert.IsNull(parsed.Query);
        return parsed.Error!;
    }

    private static FileEntry File(string name, long size = 100, DateTime? modified = null) =>
        new(name, @"C:\r\" + name, false, size, modified ?? Now, FileAttributes.Normal);

    private static FileEntry Folder(string name) => new(name, @"C:\r\" + name, true, 0, Now, FileAttributes.Directory);

    [TestMethod]
    public void EmptyQueryIsAnError() => StringAssert.Contains(Error("  "), "name");

    [TestMethod]
    public void WordsAreNameTermsThatMustAllMatch()
    {
        var q = Parse("report 2024");

        CollectionAssert.AreEqual(new[] { "report", "2024" }, q.NameTerms.ToArray());
        Assert.IsTrue(q.Matches(File("2024 Q1 Report.pdf")));
        Assert.IsFalse(q.Matches(File("Report 2023.pdf")));
    }

    [TestMethod]
    public void QuotesKeepSpacesInOneTerm()
    {
        var q = Parse("\"q1 report\"");

        Assert.IsTrue(q.Matches(File("Q1 Report.pdf")));
        Assert.IsFalse(q.Matches(File("Q1 big Report.pdf")));
    }

    [TestMethod]
    public void WildcardsMatchTheWholeName()
    {
        var q = Parse("*.cs");

        Assert.IsTrue(q.Matches(File("PaneView.xaml.CS")));
        Assert.IsFalse(q.Matches(File("notes.csv")));
        Assert.IsTrue(Parse("pane?iew*").Matches(File("PaneView.xaml")));
    }

    [TestMethod]
    public void FoldersMatchByNameUnlessAFileOnlyFilterIsSet()
    {
        Assert.IsTrue(Parse("src").Matches(Folder("src")));
        Assert.IsFalse(Parse("src size>1k").Matches(Folder("src")));
        Assert.IsFalse(Parse("src", content: "x").Matches(Folder("src")));
    }

    [TestMethod]
    public void SizeFiltersUnderstandUnitsAndBounds()
    {
        var q = Parse("size>1kb size<=2m");

        Assert.AreEqual(1025L, q.MinSize);
        Assert.AreEqual(2L * 1024 * 1024, q.MaxSize);
        Assert.IsFalse(q.Matches(File("a", 1024)));
        Assert.IsTrue(q.Matches(File("a", 1025)));
        Assert.IsTrue(q.Matches(File("a", 2 * 1024 * 1024)));
        Assert.IsFalse(q.Matches(File("a", 2 * 1024 * 1024 + 1)));
        Assert.AreEqual(1536L, Parse("size>=1.5k").MinSize);
        Assert.AreEqual(99L, Parse("size<100").MaxSize);
        Assert.AreEqual(1024L * 1024 * 1024, Parse("size:>=1GB").MinSize);
    }

    [TestMethod]
    public void DatesAreWholeDaysOnOrAfterAndOnOrBefore()
    {
        var q = Parse("after:2026-01-01 before:2026-01-31");

        Assert.IsFalse(q.Matches(File("a", modified: new DateTime(2025, 12, 31, 23, 59, 0))));
        Assert.IsTrue(q.Matches(File("a", modified: new DateTime(2026, 1, 1))));
        Assert.IsTrue(q.Matches(File("a", modified: new DateTime(2026, 1, 31, 23, 59, 0))));
        Assert.IsFalse(q.Matches(File("a", modified: new DateTime(2026, 2, 1))));
    }

    [TestMethod]
    public void RelativeDatesCountBackFromNow()
    {
        Assert.AreEqual(Now.AddDays(-7), Parse("after:7d").ModifiedAfter);
        Assert.AreEqual(Now.AddHours(-3), Parse("after:3h").ModifiedAfter);
        Assert.AreEqual(Now.AddDays(-14), Parse("before:2w").ModifiedBefore);
        Assert.AreEqual(Now.Date, Parse("after:today").ModifiedAfter);
        Assert.AreEqual(Now.Date.AddDays(-1), Parse("after:yesterday").ModifiedAfter);
    }

    [TestMethod]
    public void FiltersAloneAreAValidQuery()
    {
        var q = Parse("after:7d");

        Assert.AreEqual(0, q.NameTerms.Count);
        Assert.IsTrue(q.Matches(File("anything")));
    }

    [TestMethod]
    public void ContentComesFromTheBoxOrATextFilter()
    {
        Assert.AreEqual("needle", Parse("*.cs", content: "needle").Content);
        Assert.AreEqual("two words", Parse("*.cs text:\"two words\"").Content);
        Assert.AreEqual("box", Parse("text:token", content: "box").Content);
        Assert.IsNull(Parse("*.cs", content: "   ").Content);
        Assert.AreEqual("only content", Parse("", content: "only content").Content);
    }

    [TestMethod]
    public void InvalidFiltersExplainThemselves()
    {
        StringAssert.Contains(Error("after:soon"), "after:");
        StringAssert.Contains(Error("size>big"), "size");
        StringAssert.Contains(Error("color:red"), "color:");
        StringAssert.Contains(Error("size>2m size<1m"), "size");
    }

    [TestMethod]
    public void HighlightsCoverPlainTermsInTheName()
    {
        var q = Parse("view pane *.cs");

        CollectionAssert.AreEqual(new[] { new MatchRange(0, 4), new MatchRange(4, 4) }, q.Highlights("PaneView.cs").ToArray());
    }
}
