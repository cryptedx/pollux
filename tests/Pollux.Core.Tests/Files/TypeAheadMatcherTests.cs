using Pollux.Core.Files;

namespace Pollux.Core.Tests.Files;

[TestClass]
public sealed class TypeAheadMatcherTests
{
    private static readonly string[] Names = ["alpha", "beta", "Bravo", "charlie"];
    private static readonly DateTime T0 = new(2026, 1, 1);

    [TestMethod]
    public void SelectsTheFirstMatchIgnoringCase()
    {
        var m = new TypeAheadMatcher();

        Assert.AreEqual(3, m.Next('C', T0, -1, Names));
    }

    [TestMethod]
    public void RepeatingOneKeyCyclesAndWrapsAround()
    {
        var m = new TypeAheadMatcher();

        var i = m.Next('b', T0, 0, Names);
        Assert.AreEqual(1, i);
        i = m.Next('b', T0.AddSeconds(2), i, Names);
        Assert.AreEqual(2, i);
        i = m.Next('b', T0.AddSeconds(4), i, Names);
        Assert.AreEqual(1, i);
    }

    [TestMethod]
    public void QuickKeysAccumulateFromTheCurrentRow()
    {
        var m = new TypeAheadMatcher();

        var i = m.Next('b', T0, -1, Names);
        Assert.AreEqual(1, i);
        i = m.Next('r', T0.AddMilliseconds(500), i, Names);

        Assert.AreEqual("br", m.Text);
        Assert.AreEqual(2, i);
    }

    [TestMethod]
    public void APauseStartsOver()
    {
        var m = new TypeAheadMatcher();

        m.Next('b', T0, -1, Names);
        var i = m.Next('c', T0 + TypeAheadMatcher.Window, 1, Names);

        Assert.AreEqual("c", m.Text);
        Assert.AreEqual(3, i);
    }

    [TestMethod]
    public void NoMatchReturnsMinusOne()
    {
        var m = new TypeAheadMatcher();

        Assert.AreEqual(-1, m.Next('x', T0, 0, Names));
        Assert.AreEqual(-1, m.Next('a', T0.AddSeconds(5), -1, []));
    }
}
