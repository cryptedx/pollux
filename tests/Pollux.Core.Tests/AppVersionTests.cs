namespace Pollux.Core.Tests;

[TestClass]
public sealed class AppVersionTests
{
    [TestMethod]
    public void ShortensTheCommitHash()
    {
        var v = AppVersion.Parse("1.4.0+f69a251bf8937dcca7fc2b32fbdb5a2ec788a98e");
        Assert.AreEqual("1.4.0", v.Number);
        Assert.AreEqual("f69a251", v.Commit);
        Assert.AreEqual("1.4.0 · f69a251", v.Label);
        Assert.AreEqual("1.4.0+f69a251", v.ToString());
    }

    [TestMethod]
    public void WithoutACommitShowsOnlyTheNumber()
    {
        var v = AppVersion.Parse("1.4.0");
        Assert.IsNull(v.Commit);
        Assert.AreEqual("1.4.0", v.Label);
        Assert.AreEqual("1.4.0", v.ToString());
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("+abc")]
    public void FallsBackToZeroWhenTheNumberIsMissing(string? text)
    {
        Assert.AreEqual("0.0.0", AppVersion.Parse(text).Number);
    }

    [TestMethod]
    public void KeepsShortHashesAsTheyAre()
    {
        Assert.AreEqual("abc", AppVersion.Parse("1.0.0+abc").Commit);
    }
}
