using Pollux.Core.Files;

namespace Pollux.Core.Tests.Files;

[TestClass]
public sealed class FilterMatcherTests
{
    [TestMethod]
    public void EmptyQueryMatchesEverything() => Assert.IsTrue(FilterMatcher.Match("anything", "").IsMatch);

    [TestMethod]
    public void SubstringIsCaseInsensitiveAndHighlighted()
    {
        var m = FilterMatcher.Match("GLOSSARY.md", "sary");

        Assert.IsTrue(m.IsMatch);
        CollectionAssert.AreEqual(new[] { new MatchRange(4, 4) }, m.Ranges.ToArray());
    }

    [TestMethod]
    public void InitialsMatchWordStarts()
    {
        var m = FilterMatcher.Match("Directory.Build.props", "dbp");

        Assert.IsTrue(m.IsMatch);
        CollectionAssert.AreEqual(new[] { new MatchRange(0, 1), new MatchRange(10, 1), new MatchRange(16, 1) }, m.Ranges.ToArray());
    }

    [TestMethod]
    public void InitialsUnderstandCamelCaseAndSeparators()
    {
        Assert.IsTrue(FilterMatcher.Match("PolluxPathsTests.cs", "ppt").IsMatch);
        Assert.IsTrue(FilterMatcher.Match("node-v24_x64.msi", "nvx").IsMatch);
    }

    [TestMethod]
    public void InitialsCanBeDisabled() => Assert.IsFalse(FilterMatcher.Match("Directory.Build.props", "dbp", matchInitials: false).IsMatch);

    [TestMethod]
    public void NoMatch() => Assert.IsFalse(FilterMatcher.Match("README.md", "xyz").IsMatch);

    [TestMethod]
    public void TenThousandEntriesWellUnderAFrame()
    {
        var names = Enumerable.Range(0, 10_000).Select(i => $"Some_File-Name{i}.txt").ToArray();
        _ = names.Count(n => FilterMatcher.Match(n, "sfn9").IsMatch); // warm up JIT; shared CI runners are noisy
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var count = names.Count(n => FilterMatcher.Match(n, "sfn9").IsMatch);
        sw.Stop();

        Assert.IsTrue(count > 0);
        Assert.IsTrue(sw.ElapsedMilliseconds < 100, $"took {sw.ElapsedMilliseconds} ms");
    }
}
