using Pollux.Core.Clipboard;

namespace Pollux.Core.Tests.Clipboard;

[TestClass]
public sealed class ClipSearchTests
{
    private static readonly DateTime T0 = new(2026, 10, 1, 12, 0, 0);

    private static ClipboardHistory History(params Clip[] oldestFirst)
    {
        var history = new ClipboardHistory();
        foreach (var clip in oldestFirst)
        {
            history.Add(clip, T0);
        }

        return history;
    }

    private static Clip Text(string text, string? app = "notepad") => Clip.FromText(text, T0, app, null);

    private static string[] Labels(IEnumerable<Clip> clips) =>
        clips.Select(c => c.Title ?? c.Text ?? string.Join(';', c.Files)).ToArray();

    [TestMethod]
    public void EmptyQueryListsTheWholeStackInOrder()
    {
        var history = History(Text("a"), Text("b"));

        CollectionAssert.AreEqual(new[] { "b", "a" }, Labels(ClipSearch.Find(history.All, "  ")));
    }

    [TestMethod]
    public void TextIsSearchedCaseInsensitively()
    {
        var history = History(Text("git push --force"), Text("Hello World"), Text("dotnet test"));

        CollectionAssert.AreEqual(new[] { "Hello World" }, Labels(ClipSearch.Find(history.All, "WORLD")));
    }

    [TestMethod]
    public void FileNamesAreSearchedFuzzilyButNotTheirFolders()
    {
        var history = History(
            Clip.FromFiles([@"C:\Work\Directory.Build.props"], T0, "explorer", null),
            Clip.FromFiles([@"C:\Work\readme.md", @"C:\Work\notes.txt"], T0, "explorer", null));

        CollectionAssert.AreEqual(new[] { @"C:\Work\Directory.Build.props" }, Labels(ClipSearch.Find(history.All, "dbp")));
        CollectionAssert.AreEqual(new[] { @"C:\Work\readme.md;C:\Work\notes.txt" }, Labels(ClipSearch.Find(history.All, "notes")));
        Assert.AreEqual(0, ClipSearch.Find(history.All, "work").Count);
    }

    [TestMethod]
    public void KeptClipTitlesAreSearched()
    {
        var history = History(Text("ssh admin@10.0.0.1"), Text("other"));
        history.Keep(history.Recent[1].Id, "Build Server");

        CollectionAssert.AreEqual(new[] { "Build Server" }, Labels(ClipSearch.Find(history.All, "bs")));
        CollectionAssert.AreEqual(new[] { "Build Server" }, Labels(ClipSearch.Find(history.All, "server")));
    }

    [TestMethod]
    public void AtAppNarrowsToClipsFromThatApp()
    {
        var history = History(Text("one", "Code"), Text("two", "notepad"), Text("three", "Code - Insiders"), Text("four", null));

        CollectionAssert.AreEqual(new[] { "three", "one" }, Labels(ClipSearch.Find(history.All, "@code")));
        CollectionAssert.AreEqual(new[] { "one" }, Labels(ClipSearch.Find(history.All, "on @CODE")));
        CollectionAssert.AreEqual(new[] { "one" }, Labels(ClipSearch.Find(history.All, "@code on")));
    }

    [TestMethod]
    public void SeveralAtAppsMeanAnyOfThem()
    {
        var history = History(Text("one", "Code"), Text("two", "notepad"), Text("three", "explorer"));

        CollectionAssert.AreEqual(new[] { "two", "one" }, Labels(ClipSearch.Find(history.All, "@code @notepad")));
    }

    [TestMethod]
    public void ALoneAtWhileTypingDoesNotFilter()
    {
        var history = History(Text("one", "Code"), Text("two", null));

        CollectionAssert.AreEqual(new[] { "two", "one" }, Labels(ClipSearch.Find(history.All, "@")));
    }

    [TestMethod]
    public void EveryTermMustMatchSomewhere()
    {
        var history = History(
            Text("deploy to staging"),
            Text("deploy to production"),
            Clip.FromFiles([@"C:\Work\staging.json"], T0, "explorer", null));

        CollectionAssert.AreEqual(new[] { "deploy to staging" }, Labels(ClipSearch.Find(history.All, "staging deploy")));
    }

    [TestMethod]
    public void AnEmailAddressInTextIsSearchTextNotAnAppFilter()
    {
        var history = History(Text("mail me@example.com"), Text("other"));

        CollectionAssert.AreEqual(new[] { "mail me@example.com" }, Labels(ClipSearch.Find(history.All, "me@example")));
    }

    [TestMethod]
    public void AFullHistoryFiltersWellUnderAFrame()
    {
        var filler = string.Concat(Enumerable.Repeat("lorem ipsum dolor sit amet ", 400)); // ~10 KB per Clip
        var clips = Enumerable.Range(0, 500).Select(i => Text($"{filler} item{i}", i % 2 == 0 ? "Code" : "notepad")).ToArray();
        _ = ClipSearch.Find(clips, "item49 @code"); // warm up JIT; shared CI runners are noisy
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var hits = ClipSearch.Find(clips, "item498 @code");
        sw.Stop();

        Assert.AreEqual(1, hits.Count);
        Assert.IsTrue(sw.ElapsedMilliseconds < 100, $"took {sw.ElapsedMilliseconds} ms");
    }

    [TestMethod]
    public void OnlyTheStartOfALongTextIsMatchedFuzzily()
    {
        var padding = new string('x', ClipSearch.FuzzyTextLimit);
        var history = History(Text($"Some Long Text {padding}"), Text($"{padding} Some Long Text"));

        CollectionAssert.AreEqual(new[] { $"Some Long Text {padding}" }, Labels(ClipSearch.Find(history.All, "slt")));
    }

    [TestMethod]
    public void TextIsAlsoMatchedFuzzily_AfterAllSubstringMatches_EachGroupInStackOrder()
    {
        var history = History(Text("Small Light Tower"), Text("has slt inside"), Text("Some Long Text"), Text("salt"), Text("unrelated"));

        CollectionAssert.AreEqual(
            new[] { "has slt inside", "Some Long Text", "Small Light Tower" },
            Labels(ClipSearch.Find(history.All, "slt")));
    }

    [TestMethod]
    public void AClipMatchingAnyTermOnlyFuzzilyRanksAfterClipsMatchingEveryTermBySubstring()
    {
        var history = History(Text("deploy to staging"), Text("deploy Some Long Text"), Text("slt deploy"));

        CollectionAssert.AreEqual(
            new[] { "slt deploy", "deploy Some Long Text" },
            Labels(ClipSearch.Find(history.All, "deploy slt")));
    }
}
