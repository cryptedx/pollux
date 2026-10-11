using System.Text.Json;
using Pollux.Core.Files;
using Pollux.Core.Navigation;
using Pollux.Core.Session;
using Pollux.Core.Settings;

namespace Pollux.Core.Tests.Files;

[TestClass]
public sealed class ViewModesTests
{
    [TestMethod]
    [DataRow("view.details", ViewMode.Tiles, ViewMode.Details)]
    [DataRow("view.tiles", ViewMode.Details, ViewMode.Tiles)]
    [DataRow("view.icons", ViewMode.Details, ViewMode.Icons)]
    [DataRow("view.cycleMode", ViewMode.Details, ViewMode.Tiles)]
    [DataRow("view.cycleMode", ViewMode.Tiles, ViewMode.Icons)]
    [DataRow("view.cycleMode", ViewMode.Icons, ViewMode.Details)]
    public void CommandPicksTheMode(string command, ViewMode current, ViewMode expected) =>
        Assert.AreEqual(expected, ViewModes.ForCommand(command, current));

    [TestMethod]
    public void OtherCommandsAreNoViewModeCommands()
    {
        Assert.IsNull(ViewModes.ForCommand("view.refresh", ViewMode.Tiles));
        Assert.IsTrue(ViewModes.IsCommand("view.icons"));
        Assert.IsFalse(ViewModes.IsCommand("view.toggleDual"));
    }

    [TestMethod]
    public void ATabWithoutItsOwnModeFollowsTheSetting()
    {
        Assert.AreEqual(ViewMode.Icons, ViewModes.Effective(null, ViewMode.Icons));
        Assert.AreEqual(ViewMode.Tiles, ViewModes.Effective(ViewMode.Tiles, ViewMode.Icons));
    }

    [TestMethod]
    public void OnlyTheIconModesShowThumbnails()
    {
        Assert.AreEqual(0, ViewModes.ThumbnailSize(ViewMode.Details));
        Assert.IsTrue(ViewModes.ThumbnailSize(ViewMode.Tiles) > 0);
        Assert.IsTrue(ViewModes.ThumbnailSize(ViewMode.Icons) > ViewModes.ThumbnailSize(ViewMode.Tiles));
    }

    [TestMethod]
    public void ThumbnailPixelsFollowTheDisplayScale()
    {
        Assert.AreEqual(ViewModes.ThumbnailSize(ViewMode.Icons), ViewModes.ThumbnailPixels(ViewMode.Icons, 1.0));
        Assert.AreEqual(ViewModes.ThumbnailSize(ViewMode.Icons) * 3 / 2, ViewModes.ThumbnailPixels(ViewMode.Icons, 1.5));
        Assert.AreEqual(0, ViewModes.ThumbnailPixels(ViewMode.Details, 2.0));
    }

    [TestMethod]
    public void LabelsAreEnglish()
    {
        Assert.AreEqual("Details", ViewModes.Label(ViewMode.Details));
        Assert.AreEqual("Tiles", ViewModes.Label(ViewMode.Tiles));
        Assert.AreEqual("Large Icons", ViewModes.Label(ViewMode.Icons));
    }

    [TestMethod]
    public void DefaultSettingIsDetailsAndReadsFromJson()
    {
        Assert.AreEqual(ViewMode.Details, SettingsStore.Parse("{}").Settings.Files.ViewMode);
        Assert.AreEqual(ViewMode.Icons, SettingsStore.Parse("""{ "files": { "viewMode": "icons" } }""").Settings.Files.ViewMode);
    }

    [TestMethod]
    public void TabsKeepTheirModeThroughTheSession()
    {
        var p = new PaneTabs(() => WinPath.Home, new PaneSession
        {
            Tabs = [new() { Location = @"C:\a", ViewMode = ViewMode.Icons }, new() { Location = @"C:\b" }],
        });

        Assert.AreEqual(ViewMode.Icons, p.Tabs[0].ViewMode);
        Assert.IsNull(p.Tabs[1].ViewMode);

        var json = JsonSerializer.Serialize(new SessionState { Windows = [new WindowSession { Panes = [p.ToSession()] }] }, StateJson.Default.SessionState);
        var again = JsonSerializer.Deserialize(json, StateJson.Default.SessionState)!.Windows[0].Panes[0];

        Assert.AreEqual(ViewMode.Icons, again.Tabs[0].ViewMode);
        Assert.IsNull(again.Tabs[1].ViewMode);
    }

    [TestMethod]
    public void AMovedTabKeepsItsMode()
    {
        var left = new PaneTabs(() => WinPath.Home, new PaneSession { Tabs = [new() { Location = @"C:\a", ViewMode = ViewMode.Tiles }, new() { Location = @"C:\b" }] });
        var right = new PaneTabs(() => WinPath.Home, new PaneSession { Tabs = [new() { Location = @"C:\c" }] });

        PaneTabs.Move(left, 0, right, 1);

        Assert.AreEqual(@"C:\a", right.Active.Location);
        Assert.AreEqual(ViewMode.Tiles, right.Active.ViewMode);
    }
}
