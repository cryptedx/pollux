using Pollux.Core.Commands;
using Pollux.Core.Copilot;
using Pollux.Core.Settings;

namespace Pollux.Core.Tests.Commands;

[TestClass]
public sealed class CommandRegistryTests
{
    private static CopilotSettings WithTemplate() => SettingsStore.Parse("""
        { "copilot": { "enabled": true, "promptTemplates": [ { "name": "Find bugs", "prompt": "Look for bugs." } ] } }
        """).Settings.Copilot;

    [TestMethod]
    public void EveryDefaultKeyIsBound()
    {
        var all = CommandRegistry.Commands(new CopilotSettings(), copilotAvailable: true);
        var keys = CommandRegistry.Keybindings(new CopilotSettings(), []);

        foreach (var c in all.Where(c => c.DefaultKey is not null))
        {
            Assert.AreEqual(KeyChord.Parse(c.DefaultKey!), keys.KeyFor(c.Id), c.Id);
        }
    }

    [TestMethod]
    public void FeatureCommandsAreBoundToTheirDefaults()
    {
        // Regression: keybindings were built from the file manager's commands only.
        var keys = CommandRegistry.Keybindings(new CopilotSettings(), []);

        Assert.AreEqual("downloads.show", keys.Resolve(KeyChord.Parse("ctrl+shift+o"), Context.Empty));
        Assert.AreEqual("notes.new", keys.Resolve(KeyChord.Parse("ctrl+alt+m"), Context.Empty));
    }

    [TestMethod]
    public void CopilotCommandsAreBoundWhileCopilotIsOff()
    {
        var keys = CommandRegistry.Keybindings(new CopilotSettings { Enabled = false }, []);

        Assert.AreEqual("copilot.togglePanel", keys.Resolve(KeyChord.Parse("ctrl+shift+i"), Context.Empty));
    }

    [TestMethod]
    public void CommandIdsAreUnique()
    {
        var ids = CommandRegistry.Commands(WithTemplate(), copilotAvailable: true).Select(c => c.Id).ToList();

        Assert.AreEqual(ids.Count, ids.Distinct(StringComparer.Ordinal).Count(), string.Join(", ", ids.GroupBy(i => i).Where(g => g.Count() > 1).Select(g => g.Key)));
    }

    [TestMethod]
    public void NoTwoDefaultKeysCollide()
    {
        var bound = CommandRegistry.Commands(new CopilotSettings(), copilotAvailable: true)
            .Where(c => c.DefaultKey is not null)
            .Select(c => (c.Id, Chord: KeyChord.Parse(c.DefaultKey!), c.When))
            .ToList();

        for (var i = 0; i < bound.Count; i++)
        {
            for (var j = i + 1; j < bound.Count; j++)
            {
                var (a, b) = (bound[i], bound[j]);
                // Two bindings of one chord clash unless their "when" contexts can never both hold (different, both set).
                var overlap = a.When is null || b.When is null || a.When == b.When;
                Assert.IsFalse(a.Chord == b.Chord && overlap, $"{a.Id} and {b.Id} share {a.Chord}");
            }
        }
    }

    [TestMethod]
    public void CopilotOffHidesCopilotCommandsAndSelectionActions()
    {
        var off = CommandRegistry.Commands(WithTemplate(), copilotAvailable: false);

        Assert.IsFalse(off.Any(c => CopilotAvailability.IsCopilotCommand(c.Id)));
        Assert.IsTrue(off.Any(c => c.Id == "integration.copilot"));
        Assert.IsTrue(off.Any(c => c.Id == "downloads.show"));
    }

    [TestMethod]
    public void CopilotOnAddsSelectionActionsAfterTheFixedCommands()
    {
        var on = CommandRegistry.Commands(WithTemplate(), copilotAvailable: true);

        Assert.IsTrue(on.Any(c => c.Id == "copilot.togglePanel"));
        var actions = on.Where(c => SelectionActions.IsSelectionAction(c.Id)).ToList();
        CollectionAssert.AreEqual(
            new[] { "Copilot: Explain", "Copilot: Summarize", "Copilot: Rename by content", "Copilot: Find bugs" },
            actions.Select(a => a.Title).ToArray());
        Assert.AreEqual(on.Count - 4, on.ToList().IndexOf(actions[0]));
        Assert.IsTrue(actions.All(a => a.ShowInPalette && a.DefaultKey is null));
    }

    [TestMethod]
    [DataRow("tab.new", CommandKind.Plain)]
    [DataRow("nope.unknown", CommandKind.Plain)]
    [DataRow("workspace.open:", CommandKind.Plain)]
    [DataRow("workspace.open", CommandKind.Plain)]
    [DataRow("bookmark.go", CommandKind.Plain)]
    [DataRow("bookmark.go0", CommandKind.Plain)]
    [DataRow("bookmark.go-1", CommandKind.Plain)]
    [DataRow("bookmark.go+1", CommandKind.Plain)]
    [DataRow("bookmark.go 1", CommandKind.Plain)]
    [DataRow("bookmark.goX", CommandKind.Plain)]
    [DataRow("bookmark.add", CommandKind.Plain)]
    [DataRow("bookmark.addToWorkspace", CommandKind.Plain)]
    [DataRow("copilot.togglePanel", CommandKind.Plain)]
    [DataRow("copilot.action.explain", CommandKind.SelectionAction)]
    [DataRow("copilot.template.0", CommandKind.SelectionAction)]
    [DataRow("workspace.open:Pollux", CommandKind.OpenWorkspace)]
    [DataRow("bookmark.go1", CommandKind.GoToBookmark)]
    public void ParsesKinds(string id, CommandKind kind)
    {
        var parsed = CommandRegistry.Parse(id);

        Assert.AreEqual(kind, parsed.Kind);
        Assert.AreEqual(id, parsed.Id);
    }

    [TestMethod]
    public void ParsesParameters()
    {
        Assert.AreEqual("My Project: v2", CommandRegistry.Parse("workspace.open:My Project: v2").Workspace);
        Assert.AreEqual(3, CommandRegistry.Parse("bookmark.go3").Bookmark);
        // Beyond the nine default keys: still a Bookmark, bindable in keybindings.json.
        Assert.AreEqual(12, CommandRegistry.Parse("bookmark.go12").Bookmark);
    }

    [TestMethod]
    public void PaletteListsRecentCommandsFirst()
    {
        var recent = new RecentCommands();
        recent.Note("tab.new");
        recent.Note("downloads.show");

        var palette = recent.Palette(CommandRegistry.Commands(new CopilotSettings(), copilotAvailable: false));

        CollectionAssert.AreEqual(new[] { "downloads.show", "tab.new", "palette.show" }, palette.Take(3).Select(c => c.Id).ToArray());
        Assert.IsFalse(palette.Any(c => !c.ShowInPalette));
    }

    [TestMethod]
    public void RecentCommandsKeepTheLastTenWithoutDuplicates()
    {
        var recent = new RecentCommands();
        for (var i = 0; i < 12; i++) recent.Note("c" + i);
        recent.Note("c5");

        Assert.AreEqual(RecentCommands.Capacity, recent.Ids.Count);
        Assert.AreEqual("c5", recent.Ids[0]);
        Assert.AreEqual("c11", recent.Ids[1]);
        Assert.AreEqual(1, recent.Ids.Count(i => i == "c5"));
        Assert.IsFalse(recent.Ids.Contains("c1"));
    }

    [TestMethod]
    public void RebindReplacesTheDefaultKey()
    {
        var copilot = new CopilotSettings();
        var command = CommandRegistry.Commands(copilot, copilotAvailable: false).Single(c => c.Id == "file.delete");
        var before = CommandRegistry.Keybindings(copilot, []);

        var user = CommandRegistry.Rebind([], command, before.KeyFor(command.Id), KeyChord.Parse("ctrl+e"));
        var after = CommandRegistry.Keybindings(copilot, user);

        Assert.AreEqual(KeyChord.Parse("ctrl+e"), after.KeyFor(command.Id));
        Assert.IsNull(after.Resolve(KeyChord.Parse("delete"), Context.Of("listFocused")));
        // The new binding keeps the command's context.
        Assert.IsNull(after.Resolve(KeyChord.Parse("ctrl+e"), Context.Empty));
        Assert.AreEqual("file.delete", after.Resolve(KeyChord.Parse("ctrl+e"), Context.Of("listFocused")));
    }

    [TestMethod]
    public void RebindTwiceLeavesOnlyTheLatestKey()
    {
        var copilot = new CopilotSettings();
        var command = CommandRegistry.Commands(copilot, copilotAvailable: false).Single(c => c.Id == "tab.new");
        var user = CommandRegistry.Rebind([], command, KeyChord.Parse("ctrl+t"), KeyChord.Parse("ctrl+e"));
        var middle = CommandRegistry.Keybindings(copilot, user);

        user = CommandRegistry.Rebind(user, command, middle.KeyFor(command.Id), KeyChord.Parse("ctrl+shift+alt+k"));
        var after = CommandRegistry.Keybindings(copilot, user);

        Assert.AreEqual(KeyChord.Parse("ctrl+shift+alt+k"), after.KeyFor(command.Id));
        Assert.IsNull(after.Resolve(KeyChord.Parse("ctrl+e"), Context.Empty));
        Assert.IsNull(after.Resolve(KeyChord.Parse("ctrl+t"), Context.Empty));
    }

    [TestMethod]
    public void ClearingRemovesTheKeyAndKeepsOtherEntries()
    {
        var copilot = new CopilotSettings();
        var command = CommandRegistry.Commands(copilot, copilotAvailable: false).Single(c => c.Id == "tab.new");
        KeybindingEntry other = new("ctrl+q", "app.quit");

        var user = CommandRegistry.Rebind([other], command, KeyChord.Parse("ctrl+t"), chord: null);
        var after = CommandRegistry.Keybindings(copilot, user);

        Assert.IsNull(after.KeyFor(command.Id));
        Assert.AreEqual(other, user[0]);
        Assert.AreEqual("app.quit", after.Resolve(KeyChord.Parse("ctrl+q"), Context.Empty));
    }

    [TestMethod]
    public void ClearingAnUnboundCommandChangesNothing()
    {
        var command = CommandRegistry.Commands(new CopilotSettings(), copilotAvailable: false).Single(c => c.Id == "recent.clear");

        Assert.AreEqual(0, CommandRegistry.Rebind([], command, current: null, chord: null).Count);
    }
}
