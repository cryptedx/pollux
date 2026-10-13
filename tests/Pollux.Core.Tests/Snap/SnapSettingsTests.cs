using Pollux.Core.Settings;

namespace Pollux.Core.Tests.Snap;

[TestClass]
public sealed class SnapSettingsTests
{
    [TestMethod]
    public void SnapIsOffByDefaultWithWinAltSAndAutoSave()
    {
        var snap = SettingsStore.Parse("{}").Settings.Snap;

        Assert.IsFalse(snap.Enabled);
        Assert.AreEqual("win+alt+s", snap.GlobalHotkey);
        Assert.IsTrue(snap.AutoSave);
        Assert.AreEqual("", snap.Folder);
        Assert.AreEqual(SnapAfterCapture.Board, snap.AfterCapture);
        Assert.AreEqual(3, snap.DelaySeconds);
        Assert.AreEqual("", snap.OcrLanguage);
        Assert.IsFalse(snap.TakeOverWinShiftS);
        Assert.IsFalse(snap.TakeOverPrintKey);
        Assert.IsTrue(SettingsStore.Parse("""{ "snap": { "takeOverPrintKey": true } }""").Settings.Snap.TakeOverPrintKey);
        Assert.AreEqual("", SettingsStore.Parse("""{ "snap": { "ocrLanguage": null } }""").Settings.Snap.OcrLanguage);
    }

    [TestMethod]
    public void AfterCaptureIsReadAsText()
    {
        var snap = SettingsStore.Parse("""{ "snap": { "afterCapture": "clipboard" } }""").Settings.Snap;

        Assert.AreEqual(SnapAfterCapture.Clipboard, snap.AfterCapture);
        Assert.AreEqual(SnapAfterCapture.AsFloat, SettingsStore.Parse("""{ "snap": { "afterCapture": "float" } }""").Settings.Snap.AfterCapture);
        StringAssert.Contains(SettingsStore.Serialize(new PolluxSettings { Snap = { AfterCapture = SnapAfterCapture.AsFloat } }), "\"float\"");
    }

    [TestMethod]
    public void NullSnapSectionYieldsDefaults()
    {
        var snap = SettingsStore.Parse("""{ "snap": null }""").Settings.Snap;

        Assert.IsNotNull(snap);
        Assert.AreEqual("win+alt+s", snap.GlobalHotkey);
        Assert.IsNotNull(SettingsStore.Parse("""{ "snap": { "folder": null, "globalHotkey": null } }""").Settings.Snap.Folder);
        Assert.AreEqual("win+alt+s", SettingsStore.Parse("""{ "snap": { "globalHotkey": null } }""").Settings.Snap.GlobalHotkey);
    }

    [TestMethod]
    public void SnapKeepsPolluxResidentEvenWithoutTheTraySetting()
    {
        var settings = SettingsStore.Parse("""{ "app": { "resident": false } }""").Settings;
        Assert.IsFalse(settings.KeepsResident);

        settings.Snap.Enabled = true;

        Assert.IsTrue(settings.KeepsResident);
    }

    [TestMethod]
    public void SnapsFolderDefaultsToPicturesSnaps()
    {
        var snap = SettingsStore.Parse("{}").Settings.Snap;

        Assert.AreEqual(Path.Combine("C:\\Users\\me\\Pictures", "Snaps"), snap.FolderOrDefault("C:\\Users\\me\\Pictures"));
        snap.Folder = "D:\\Shots";
        Assert.AreEqual("D:\\Shots", snap.FolderOrDefault("C:\\Users\\me\\Pictures"));
    }
}
