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
}
