using Pollux.Core.Clipboard;

namespace Pollux.Core.Tests.Clipboard;

[TestClass]
public sealed class WinVTakeoverTests
{
    private sealed class FakeRegistry(int? value) : IWinVRegistry
    {
        public int? Value { get; set; } = value;
        public int? Read() => Value;
        public void Write(int? value) => Value = value;
    }

    private static string StateFile() => Path.Combine(Directory.CreateTempSubdirectory("pollux-winv").FullName, "winv-restore.json");

    [TestMethod]
    public void Engage_DisablesWindowsClipboardHistory_AndRestoreBringsBackThePreviousValue()
    {
        var reg = new FakeRegistry(1);
        var takeover = new WinVTakeover(reg, StateFile());

        takeover.Engage();
        Assert.AreEqual(0, reg.Value);

        takeover.Restore();
        Assert.AreEqual(1, reg.Value);
    }

    [TestMethod]
    public void Restore_RemovesTheValue_WhenItWasAbsentBefore()
    {
        var reg = new FakeRegistry(null);
        var takeover = new WinVTakeover(reg, StateFile());

        takeover.Engage();
        takeover.Restore();

        Assert.IsNull(reg.Value);
    }

    [TestMethod]
    public void Engage_Twice_KeepsTheOriginalValue()
    {
        var reg = new FakeRegistry(1);
        var takeover = new WinVTakeover(reg, StateFile());

        takeover.Engage();
        takeover.Engage();
        takeover.Restore();

        Assert.AreEqual(1, reg.Value);
    }

    [TestMethod]
    public void Restore_WorksFromANewInstance_AfterACrash()
    {
        var file = StateFile();
        var reg = new FakeRegistry(1);
        new WinVTakeover(reg, file).Engage();

        new WinVTakeover(reg, file).Restore();

        Assert.AreEqual(1, reg.Value);
    }

    [TestMethod]
    public void Restore_WithoutEngage_LeavesTheRegistryAlone()
    {
        var reg = new FakeRegistry(0);
        new WinVTakeover(reg, StateFile()).Restore();
        Assert.AreEqual(0, reg.Value);
    }

    [TestMethod]
    public void Restore_Twice_DoesNotRestoreAgain()
    {
        var reg = new FakeRegistry(1);
        var takeover = new WinVTakeover(reg, StateFile());
        takeover.Engage();
        takeover.Restore();
        reg.Value = 0; // changed by the user afterwards

        takeover.Restore();

        Assert.AreEqual(0, reg.Value);
    }

    [TestMethod]
    public void Restore_WithAMalformedState_LeavesTheRegistryAlone_AndKeepsTheStateFile()
    {
        var file = StateFile();
        var reg = new FakeRegistry(0);
        File.WriteAllText(file, "garbage");
        var takeover = new WinVTakeover(reg, file);

        Assert.ThrowsExactly<InvalidDataException>(takeover.Restore);

        Assert.AreEqual(0, reg.Value);
        Assert.IsTrue(File.Exists(file));
        Assert.IsTrue(takeover.IsEngaged);
    }

    [TestMethod]
    public void IsEngaged_ReflectsTheSavedState()
    {
        var takeover = new WinVTakeover(new FakeRegistry(1), StateFile());
        Assert.IsFalse(takeover.IsEngaged);
        takeover.Engage();
        Assert.IsTrue(takeover.IsEngaged);
        takeover.Restore();
        Assert.IsFalse(takeover.IsEngaged);
    }
}
