using Pollux.Copilot.Context;

namespace Pollux.Copilot.Tests.Context;

/// <summary>Context chips: what a message carries, what @ offers, and what the CLI is told.</summary>
[TestClass]
public sealed class ContextChipsTests
{
    private static ContextSnapshot Snapshot(string? folder = @"C:\repos\app", string[]? selection = null, PaneContext? other = null, string[]? tabs = null) =>
        new(new PaneContext(folder, selection ?? [], [@"C:\repos\app\a.txt", @"C:\repos\app\notes.md", @"C:\repos\app\src"]), other, tabs ?? []);

    [TestMethod]
    public void DefaultsAreLocationAndSelectionOfTheActivePane()
    {
        var chips = ContextChips.Defaults(Snapshot(selection: [@"C:\repos\app\a.txt", @"C:\repos\app\src"]));

        CollectionAssert.AreEqual(new[] { ContextChipKind.Location, ContextChipKind.Selection }, chips.Select(c => c.Kind).ToArray());
        Assert.AreEqual(@"C:\repos\app", chips[0].Label);
        Assert.AreEqual("2 selected", chips[1].Label);
        CollectionAssert.AreEqual(new[] { @"C:\repos\app\a.txt", @"C:\repos\app\src" }, chips[1].Paths.ToArray());
    }

    [TestMethod]
    public void SingleSelectionIsLabelledByName()
    {
        var chips = ContextChips.Defaults(Snapshot(selection: [@"C:\repos\app\a.txt"]));

        Assert.AreEqual("a.txt", chips[1].Label);
    }

    [TestMethod]
    public void HomeAndEmptySelectionGiveNoChips()
    {
        Assert.HasCount(0, ContextChips.Defaults(Snapshot(folder: null)));
        Assert.HasCount(1, ContextChips.Defaults(Snapshot()));
    }

    [TestMethod]
    public void ChipIdsAreStableForTheSameContentAndDifferOtherwise()
    {
        var a = ContextChips.Defaults(Snapshot(selection: [@"C:\repos\app\a.txt"]));
        var b = ContextChips.Defaults(Snapshot(selection: [@"C:\repos\app\a.txt"]));
        var c = ContextChips.Defaults(Snapshot(selection: [@"C:\repos\app\notes.md"]));

        Assert.AreEqual(a[1].Id, b[1].Id);
        Assert.AreNotEqual(a[1].Id, c[1].Id);
        Assert.AreNotEqual(a[0].Id, a[1].Id);
    }

    [TestMethod]
    public void MentionsOfferTheOtherPaneTabsAndFiles()
    {
        var snap = Snapshot(other: new PaneContext(@"D:\data", [], []), tabs: [@"C:\repos\app", @"E:\music"]);

        var all = ContextChips.Mentions(snap, "").Select(c => c.Kind).ToArray();

        CollectionAssert.AreEqual(new[] { ContextChipKind.Pane, ContextChipKind.Tab, ContextChipKind.Tab, ContextChipKind.File, ContextChipKind.File, ContextChipKind.File }, all);
    }

    [TestMethod]
    public void MentionQueryFiltersByLabelCaseInsensitively()
    {
        var snap = Snapshot(other: new PaneContext(@"D:\data", [], []), tabs: [@"E:\music"]);

        var chips = ContextChips.Mentions(snap, "NOT");

        Assert.AreEqual(@"C:\repos\app\notes.md", chips.Single().Paths.Single());
        Assert.AreEqual(ContextChipKind.File, chips.Single().Kind);
        Assert.HasCount(0, ContextChips.Mentions(snap, "zzz"));
    }

    [TestMethod]
    public void OtherPaneOnHomeIsNotOffered()
    {
        var snap = Snapshot(other: new PaneContext(null, [], []));

        Assert.IsFalse(ContextChips.Mentions(snap, "").Any(c => c.Kind == ContextChipKind.Pane));
    }

    [TestMethod]
    public void ComposeAddsExactlyTheGivenChips()
    {
        var chips = ContextChips.Defaults(Snapshot(selection: [@"C:\repos\app\a.txt"]));

        var prompt = ContextPrompt.Compose("Explain this", chips);

        StringAssert.StartsWith(prompt, "Explain this");
        StringAssert.Contains(prompt, @"C:\repos\app");
        StringAssert.Contains(prompt, @"C:\repos\app\a.txt");
        Assert.IsFalse(prompt.Contains("notes.md", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ResumedHistoryShowsTheUsersTextWithoutTheAttachedContext()
    {
        var chips = ContextChips.Defaults(Snapshot(selection: [@"C:\repos\app\a.txt"]));

        Assert.AreEqual("Explain this\nplease", ContextPrompt.StripContext(ContextPrompt.Compose("Explain this\nplease", chips)));
        Assert.AreEqual("Plain", ContextPrompt.StripContext("Plain"));
    }

    [TestMethod]
    public void ComposeWithoutChipsIsTheTextUnchanged()
    {
        Assert.AreEqual("Hello", ContextPrompt.Compose("Hello", []));
    }

    [TestMethod]
    public void RemovedChipIsNotInTheRequest()
    {
        var chips = ContextChips.Defaults(Snapshot(selection: [@"C:\repos\app\a.txt"]));
        var withoutSelection = chips.Where(c => c.Kind != ContextChipKind.Selection).ToList();

        var prompt = ContextPrompt.Compose("Q", withoutSelection);

        Assert.IsFalse(prompt.Contains("a.txt", StringComparison.Ordinal));
        StringAssert.Contains(prompt, @"C:\repos\app");
    }

    [TestMethod]
    public void RegistryResolvesOnlyChipsItIssuedAndKeepsTheOrder()
    {
        var registry = new ContextRegistry();
        var chips = ContextChips.Defaults(Snapshot(selection: [@"C:\repos\app\a.txt"]));
        registry.Issue(chips);

        var resolved = registry.Resolve([chips[1].Id, "forged:123", chips[0].Id]);

        CollectionAssert.AreEqual(new[] { chips[1].Id, chips[0].Id }, resolved.Select(c => c.Id).ToArray());
    }

    [TestMethod]
    public void RegistryDoesNotDuplicateChipIds()
    {
        var registry = new ContextRegistry();
        var chips = ContextChips.Defaults(Snapshot());
        registry.Issue(chips);

        Assert.HasCount(1, registry.Resolve([chips[0].Id, chips[0].Id]));
    }

    [TestMethod]
    public void DroppedPathsBecomeFileChips()
    {
        var chips = ContextChips.FromPaths([@"C:\x\a.txt", @"C:\x\a.txt", @"C:\x\dir\"]);

        Assert.HasCount(2, chips);
        Assert.IsTrue(chips.All(c => c.Kind == ContextChipKind.File));
        Assert.AreEqual("a.txt", chips[0].Label);
        Assert.AreEqual("dir", chips[1].Label);
    }
}
