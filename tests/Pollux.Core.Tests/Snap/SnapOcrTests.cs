using Pollux.Core.Snap;

namespace Pollux.Core.Tests.Snap;

[TestClass]
public sealed class SnapOcrTests
{
    private static readonly string[] Installed = ["en-US", "de-DE"];

    [TestMethod]
    public void TheConfiguredLanguageIsUsedWhenInstalled()
    {
        Assert.AreEqual("de-DE", SnapOcrLanguage.Choose("de-DE", ["en-US"], Installed));
        Assert.AreEqual("de-DE", SnapOcrLanguage.Choose("de", ["en-US"], Installed));
        Assert.AreEqual("de-DE", SnapOcrLanguage.Choose("DE-de", ["en-US"], Installed));
    }

    [TestMethod]
    public void AConfiguredLanguageThatIsNotInstalledGivesNone()
    {
        Assert.IsNull(SnapOcrLanguage.Choose("fr-FR", ["en-US"], Installed));
    }

    [TestMethod]
    public void WithoutSettingTheFirstInstalledProfileLanguageWins()
    {
        Assert.AreEqual("de-DE", SnapOcrLanguage.Choose("", ["fr-FR", "de-AT", "en-US"], Installed));
        Assert.AreEqual("en-US", SnapOcrLanguage.Choose("", ["en-GB"], Installed));
    }

    [TestMethod]
    public void WithoutAMatchAnyInstalledLanguageIsUsedAndWithoutAnyNone()
    {
        Assert.AreEqual("en-US", SnapOcrLanguage.Choose("", ["ja-JP"], Installed));
        Assert.IsNull(SnapOcrLanguage.Choose("", ["en-US"], []));
    }

    [TestMethod]
    public void LinesAreJoinedAndALargeGapStartsAParagraph()
    {
        var lines = new SnapOcrLine[]
        {
            new("Pollux Snap", 10, 20),
            new("takes screenshots", 32, 20),
            new("Second paragraph", 90, 20),
        };

        Assert.AreEqual("Pollux Snap\ntakes screenshots\n\nSecond paragraph", SnapOcrText.Join(lines));
    }

    [TestMethod]
    public void EmptyLinesAreDroppedAndNoLinesGiveEmptyText()
    {
        Assert.AreEqual("a\nb", SnapOcrText.Join([new("a", 0, 10), new("  ", 12, 10), new("b", 24, 10)]));
        Assert.AreEqual("", SnapOcrText.Join([]));
    }
}
