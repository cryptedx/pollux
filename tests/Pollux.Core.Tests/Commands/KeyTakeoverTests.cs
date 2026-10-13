using Pollux.Core.Commands;

namespace Pollux.Core.Tests.Commands;

[TestClass]
public sealed class KeyTakeoverTests
{
    private const uint E = 0x45, V = 0x56, S = 0x53, Print = 0x2C;
    private static readonly HeldModifiers None = new(false, false, false, false);
    private static readonly HeldModifiers Win = new(true, false, false, false);
    private static readonly HeldModifiers WinShift = new(true, true, false, false);
    private const Takeovers All = Takeovers.WinE | Takeovers.WinV | Takeovers.WinShiftS | Takeovers.PrintKey;

    [TestMethod]
    public void WinShiftSStartsAnAreaSnapOnlyWithExactlyWinAndShift()
    {
        Assert.AreEqual(TakenKey.SnapArea, KeyTakeover.Decide(S, keyUp: false, WinShift, All));
        Assert.AreEqual(TakenKey.None, KeyTakeover.Decide(S, keyUp: false, WinShift with { Ctrl = true }, All));
        Assert.AreEqual(TakenKey.None, KeyTakeover.Decide(S, keyUp: false, WinShift with { Alt = true }, All));
        Assert.AreEqual(TakenKey.None, KeyTakeover.Decide(S, keyUp: false, Win, All));
        Assert.AreEqual(TakenKey.None, KeyTakeover.Decide(S, keyUp: false, WinShift, All & ~Takeovers.WinShiftS));
    }

    [TestMethod]
    public void PrintTakesAnAreaAndAltPrintTheActiveWindow()
    {
        Assert.AreEqual(TakenKey.SnapArea, KeyTakeover.Decide(Print, keyUp: false, None, All));
        Assert.AreEqual(TakenKey.SnapWindow, KeyTakeover.Decide(Print, keyUp: false, None with { Alt = true }, All));
        Assert.AreEqual(TakenKey.None, KeyTakeover.Decide(Print, keyUp: false, None with { Ctrl = true }, All));
        Assert.AreEqual(TakenKey.None, KeyTakeover.Decide(Print, keyUp: false, None with { Shift = true }, All));
        Assert.AreEqual(TakenKey.None, KeyTakeover.Decide(Print, keyUp: false, None, Takeovers.WinE));
    }

    [TestMethod]
    public void ReleasingATakenPrintKeyIsSwallowedToo()
    {
        Assert.AreEqual(TakenKey.SwallowOnly, KeyTakeover.Decide(Print, keyUp: true, None, All));
        Assert.AreEqual(TakenKey.SwallowOnly, KeyTakeover.Decide(Print, keyUp: true, None with { Alt = true }, All));
        Assert.AreEqual(TakenKey.None, KeyTakeover.Decide(Print, keyUp: true, None, Takeovers.None));
        Assert.AreEqual(TakenKey.None, KeyTakeover.Decide(S, keyUp: true, WinShift, All));
    }

    [TestMethod]
    public void WinEAndWinVBehaveAsBefore()
    {
        Assert.AreEqual(TakenKey.WinE, KeyTakeover.Decide(E, keyUp: false, Win, All));
        Assert.AreEqual(TakenKey.WinV, KeyTakeover.Decide(V, keyUp: false, Win, All));
        Assert.AreEqual(TakenKey.None, KeyTakeover.Decide(E, keyUp: false, WinShift, All));
        Assert.AreEqual(TakenKey.None, KeyTakeover.Decide(V, keyUp: false, Win, Takeovers.WinE));
        Assert.AreEqual(TakenKey.None, KeyTakeover.Decide(E, keyUp: false, None, All));
    }
}
