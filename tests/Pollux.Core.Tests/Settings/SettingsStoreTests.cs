using Pollux.Core.Settings;

namespace Pollux.Core.Tests.Settings;

[TestClass]
public sealed class SettingsStoreTests
{
    [TestMethod]
    public void EmptyFileYieldsDefaults()
    {
        var result = SettingsStore.Parse("{}");

        Assert.IsTrue(result.IsValid);
        Assert.AreEqual(ThemeMode.System, result.Settings.Appearance.Theme);
        Assert.IsTrue(result.Settings.App.Resident);
        Assert.AreEqual(2, result.Settings.StatusBar.RefreshSeconds);
    }

    [TestMethod]
    public void CopilotQuotaStatusBarItemIsOnByDefaultAndSwitchable()
    {
        Assert.IsTrue(SettingsStore.Parse("{}").Settings.StatusBar.CopilotQuota);
        Assert.IsFalse(SettingsStore.Parse("""{ "statusBar": { "copilotQuota": false } }""").Settings.StatusBar.CopilotQuota);
    }

    [TestMethod]
    public void ClipboardHotkeyDefaultsToWinAltVAndWinVTakeoverIsOptIn()
    {
        var c = SettingsStore.Parse("{}").Settings.Clipboard;
        Assert.AreEqual("win+alt+v", c.GlobalHotkey);
        Assert.IsFalse(c.TakeOverWinV);
        Assert.IsTrue(SettingsStore.Parse("""{ "clipboard": { "takeOverWinV": true } }""").Settings.Clipboard.TakeOverWinV);
    }

    [TestMethod]
    public void ClipboardPasteAsFileIsOnByDefaultAndCanBeTurnedOff()
    {
        Assert.IsTrue(SettingsStore.Parse("{}").Settings.Clipboard.PasteAsFile);
        Assert.IsFalse(SettingsStore.Parse("""{ "clipboard": { "pasteAsFile": false } }""").Settings.Clipboard.PasteAsFile);
        Assert.IsTrue(SettingsStore.Parse("""{ "clipboard": null }""").Settings.Clipboard.PasteAsFile);
    }

    [TestMethod]
    public void PartialOverrideKeepsOtherDefaults()
    {
        var result = SettingsStore.Parse("""{ "appearance": { "theme": "dark" } }""");

        Assert.AreEqual(ThemeMode.Dark, result.Settings.Appearance.Theme);
        Assert.AreEqual(Density.Compact, result.Settings.Appearance.Density);
        Assert.IsTrue(result.Settings.Files.FoldersFirst);
    }

    [TestMethod]
    public void CommentsAndTrailingCommasAreAllowed()
    {
        var result = SettingsStore.Parse("""
            {
              // show dotfiles
              "files": { "showHidden": true, },
              /* block */ "sidebar": { "visible": false },
            }
            """);

        Assert.IsTrue(result.IsValid, result.Error);
        Assert.IsTrue(result.Settings.Files.ShowHidden);
        Assert.IsFalse(result.Settings.Sidebar.Visible);
    }

    [TestMethod]
    public void InvalidJsonReportsErrorAndFallsBackToGivenSettings()
    {
        var previous = SettingsStore.Parse("""{ "files": { "showHidden": true } }""").Settings;

        var result = SettingsStore.Parse("{ not json", previous);

        Assert.IsFalse(result.IsValid);
        Assert.IsNotNull(result.Error);
        Assert.AreSame(previous, result.Settings);
    }

    [TestMethod]
    public void NullSectionsFallBackToDefaults()
    {
        var result = SettingsStore.Parse("""{ "appearance": null, "recent": { "exclude": null }, "search": { "excludeNames": null } }""");

        Assert.IsTrue(result.IsValid);
        Assert.AreEqual(ThemeMode.System, result.Settings.Appearance.Theme);
        Assert.AreEqual(0, result.Settings.Recent.Exclude.Count);
        Assert.IsNotNull(result.Settings.Search.ExcludeNames);
    }

    [TestMethod]
    public void SerializeRoundTrips()
    {
        var original = SettingsStore.Parse("""{ "appearance": { "theme": "light", "accentColor": "#ff00aa" }, "recent": { "exclude": ["C:\\Temp"] } }""").Settings;

        var again = SettingsStore.Parse(SettingsStore.Serialize(original)).Settings;

        Assert.AreEqual(ThemeMode.Light, again.Appearance.Theme);
        Assert.AreEqual("#ff00aa", again.Appearance.AccentColor);
        CollectionAssert.AreEqual(new[] { @"C:\Temp" }, again.Recent.Exclude);
    }

    [TestMethod]
    public void LoadCreatesFileWithDefaultsWhenMissing()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var file = Path.Combine(dir.FullName, "settings.json");

            var result = SettingsStore.Load(file);

            Assert.IsTrue(result.IsValid);
            Assert.IsTrue(File.Exists(file));
            StringAssert.Contains(File.ReadAllText(file), "\"appearance\"");
        }
        finally { dir.Delete(true); }
    }
    [TestMethod]
    public void ClipboardHistoryIsOffByDefault()
    {
        var clipboard = SettingsStore.Parse("{}").Settings.Clipboard;

        Assert.IsFalse(clipboard.Enabled);
        Assert.IsFalse(clipboard.Paused);
        Assert.AreEqual(1024, clipboard.MaxTextKilobytes);
        Assert.AreEqual(20, clipboard.MaxImageMegabytes);
        Assert.AreEqual(500, clipboard.MaxClips);
        Assert.AreEqual(30, clipboard.MaxAgeDays);
        Assert.AreEqual(0, clipboard.ExcludeProcesses.Count);
    }

    [TestMethod]
    public void NullClipboardSectionYieldsDefaults()
    {
        var clipboard = SettingsStore.Parse("""{ "clipboard": { "excludeProcesses": null } }""").Settings.Clipboard;

        Assert.IsNotNull(clipboard.ExcludeProcesses);
        Assert.IsNotNull(SettingsStore.Parse("""{ "clipboard": null }""").Settings.Clipboard);
    }

    [TestMethod]
    public void ClipboardRetentionComesFromSettings()
    {
        var clipboard = SettingsStore.Parse("""{ "clipboard": { "maxClips": 100, "maxAgeDays": 0 } }""").Settings.Clipboard;

        Assert.AreEqual(new Pollux.Core.Clipboard.ClipRetention(100, 0), clipboard.Retention);
    }
    [TestMethod]
    public void ClipboardHistoryKeepsPolluxResidentEvenWithoutTheTraySetting()
    {
        var settings = SettingsStore.Parse("""{ "app": { "resident": false } }""").Settings;
        Assert.IsFalse(settings.KeepsResident);

        settings.Clipboard.Enabled = true;
        Assert.IsTrue(settings.KeepsResident);

        Assert.IsTrue(SettingsStore.Parse("{}").Settings.KeepsResident);
        Assert.IsFalse(SettingsStore.Serialize(settings).Contains("keepsResident", StringComparison.Ordinal));
    }

    [TestMethod]
    public void TrustedFoldersAreAListInFilesSettingsAndEmptyByDefault()
    {
        Assert.AreEqual(0, SettingsStore.Parse("{}").Settings.Files.TrustedFolders.Count);
        Assert.AreEqual(0, SettingsStore.Parse("""{ "files": { "trustedFolders": null } }""").Settings.Files.TrustedFolders.Count);

        var parsed = SettingsStore.Parse("""{ "files": { "trustedFolders": [ "C:\\dev" ] } }""").Settings;
        CollectionAssert.AreEqual(new[] { @"C:\dev" }, parsed.Files.TrustedFolders);
        CollectionAssert.AreEqual(new[] { @"C:\dev" }, SettingsStore.Parse(SettingsStore.Serialize(parsed)).Settings.Files.TrustedFolders);
    }
}
