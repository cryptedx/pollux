using Pollux.Core.Files;

namespace Pollux.Core.Tests.Files;

[TestClass]
public sealed class WildcardMatcherTests
{
    [TestMethod]
    public void StarMatchesAnyRun()
    {
        Assert.IsTrue(WildcardMatcher.IsMatch("build.log", "*.log"));
        Assert.IsTrue(WildcardMatcher.IsMatch(".log", "*.log"));
        Assert.IsFalse(WildcardMatcher.IsMatch("build.log.txt", "*.log"));
    }

    [TestMethod]
    public void QuestionMarkMatchesExactlyOneCharacter()
    {
        Assert.IsTrue(WildcardMatcher.IsMatch("a1.txt", "a?.txt"));
        Assert.IsFalse(WildcardMatcher.IsMatch("a.txt", "a?.txt"));
        Assert.IsFalse(WildcardMatcher.IsMatch("a12.txt", "a?.txt"));
    }

    [TestMethod]
    public void MatchingIsCaseInsensitive()
    {
        Assert.IsTrue(WildcardMatcher.IsMatch("README.MD", "*.md"));
        Assert.IsTrue(WildcardMatcher.IsMatch("readme.md", "README.*"));
    }

    [TestMethod]
    public void MatchesTheWholeNameOnly()
    {
        Assert.IsFalse(WildcardMatcher.IsMatch("notes.txt.bak", "notes.txt"));
        Assert.IsTrue(WildcardMatcher.IsMatch("notes.txt", "notes.txt"));
    }

    [TestMethod]
    public void SemicolonSeparatesAlternatives()
    {
        Assert.IsTrue(WildcardMatcher.IsMatch("debug.log", "*.log;*.tmp"));
        Assert.IsTrue(WildcardMatcher.IsMatch("cache.tmp", "*.log; *.tmp"));
        Assert.IsFalse(WildcardMatcher.IsMatch("main.cs", "*.log;*.tmp"));
    }

    [TestMethod]
    public void EmptyAlternativesAreIgnored()
    {
        Assert.IsTrue(WildcardMatcher.IsMatch("x.tmp", ";;*.tmp;"));
        Assert.IsFalse(WildcardMatcher.IsMatch("x.tmp", ";"));
    }

    [TestMethod]
    public void EmptyPatternMatchesNothing()
    {
        Assert.IsFalse(WildcardMatcher.IsMatch("anything", ""));
        Assert.IsFalse(WildcardMatcher.IsMatch("anything", "   "));
    }

    [TestMethod]
    public void StarAloneMatchesEverything()
    {
        Assert.IsTrue(WildcardMatcher.IsMatch("no-extension", "*"));
        Assert.IsTrue(WildcardMatcher.IsMatch("", "*"));
    }

    [TestMethod]
    public void ManyStarsBacktrackCorrectly()
    {
        Assert.IsTrue(WildcardMatcher.IsMatch("a_b_c_d.tar.gz", "*_*_*.tar.*"));
        Assert.IsFalse(WildcardMatcher.IsMatch("a_b.tar", "*_*_*.tar"));
    }

    [TestMethod]
    public void IndexesReturnsMatchingPositionsInOrder()
    {
        string[] names = ["a.log", "b.cs", "c.TMP", "d.log", "e.tmp.cs"];

        CollectionAssert.AreEqual(new[] { 0, 2, 3 }, WildcardMatcher.Indexes(names, "*.log;*.tmp").ToArray());
    }

    [TestMethod]
    public void IndexesOfEmptyPatternAreEmpty()
    {
        string[] names = ["a", "b"];

        CollectionAssert.AreEqual(Array.Empty<int>(), WildcardMatcher.Indexes(names, "").ToArray());
    }
}
