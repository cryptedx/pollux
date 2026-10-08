using Pollux.Core.Commands;

namespace Pollux.Core.Tests.Commands;

[TestClass]
public sealed class KeybindingTests
{
    [TestMethod]
    [DataRow("ctrl+shift+p", KeyModifiers.Control | KeyModifiers.Shift, "p")]
    [DataRow("Ctrl+<", KeyModifiers.Control, "<")]
    [DataRow("ctrl+ö", KeyModifiers.Control, "ö")]
    [DataRow("alt+left", KeyModifiers.Alt, "left")]
    [DataRow("F5", KeyModifiers.None, "f5")]
    [DataRow("win+alt+n", KeyModifiers.Windows | KeyModifiers.Alt, "n")]
    [DataRow("ctrl++", KeyModifiers.Control, "+")]
    public void ParsesChords(string text, KeyModifiers modifiers, string key)
    {
        var chord = KeyChord.Parse(text);

        Assert.AreEqual(modifiers, chord.Modifiers);
        Assert.AreEqual(key, chord.Key);
    }

    [TestMethod]
    public void ChordFormatsInCanonicalOrder()
    {
        Assert.AreEqual("Ctrl+Shift+P", KeyChord.Parse("shift+ctrl+p").ToString());
        Assert.AreEqual("Alt+Left", KeyChord.Parse("alt+left").ToString());
    }

    [TestMethod]
    public void InvalidChordIsRejected()
    {
        Assert.IsFalse(KeyChord.TryParse("ctrl+", out _));
        Assert.IsFalse(KeyChord.TryParse("hyper+x", out _));
    }

    private static readonly CommandDefinition[] Commands =
    [
        new("palette.show", "Show Command Palette", "ctrl+shift+p"),
        new("filter.show", "Filter Current Folder", "ctrl+f"),
        new("filter.close", "Close Filter", "escape", "filterFocused"),
        new("selection.clear", "Clear Selection", "escape", "!filterFocused"),
        new("tab.new", "New Tab", "ctrl+t"),
    ];

    [TestMethod]
    public void ResolvesDefaultBinding()
    {
        var map = KeybindingMap.Build(Commands, []);

        Assert.AreEqual("palette.show", map.Resolve(KeyChord.Parse("ctrl+shift+p"), Context.Empty));
        Assert.IsNull(map.Resolve(KeyChord.Parse("ctrl+shift+x"), Context.Empty));
    }

    [TestMethod]
    public void WhenClauseSelectsBinding()
    {
        var map = KeybindingMap.Build(Commands, []);

        Assert.AreEqual("filter.close", map.Resolve(KeyChord.Parse("escape"), Context.Of("filterFocused")));
        Assert.AreEqual("selection.clear", map.Resolve(KeyChord.Parse("escape"), Context.Empty));
    }

    [TestMethod]
    public void UserBindingOverridesAndRemoves()
    {
        var user = new[]
        {
            new KeybindingEntry("ctrl+n", "tab.new"),
            new KeybindingEntry("ctrl+t", "-tab.new"),
        };

        var map = KeybindingMap.Build(Commands, user);

        Assert.AreEqual("tab.new", map.Resolve(KeyChord.Parse("ctrl+n"), Context.Empty));
        Assert.IsNull(map.Resolve(KeyChord.Parse("ctrl+t"), Context.Empty));
        Assert.AreEqual("Ctrl+N", map.KeyFor("tab.new")?.ToString());
    }

    [TestMethod]
    public void ParsesKeybindingsJsonc()
    {
        var entries = KeybindingMap.ParseUserFile("""
            [
              // mine
              { "key": "ctrl+n", "command": "tab.new" },
              { "key": "ctrl+t", "command": "-tab.new", },
            ]
            """);

        Assert.AreEqual(2, entries.Count);
        Assert.AreEqual("-tab.new", entries[1].Command);
    }
}
