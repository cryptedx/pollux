using Pollux.Core.Updates;

namespace Pollux.Core.Tests.Updates;

[TestClass]
public sealed class WingetParserTests
{
    [TestMethod]
    public void ParsesEnglishTable()
    {
        var output = "\r   - \r   \\ \r" + """
            Name                               Id                           Version      Available    Source
            ----------------------------------------------------------------------------------------------------
            Microsoft Visual Studio Code       Microsoft.VisualStudioCode   1.104.0      1.105.0      winget
            Git                                Git.Git                      2.50.0       2.51.0       winget
            Node.js LTS                        OpenJS.NodeJS.LTS            22.1.0       24.9.0       winget
            3 upgrades available.
            """;

        var list = WingetParser.ParseUpgrades(output);

        Assert.AreEqual(3, list.Count);
        Assert.AreEqual(new WingetPackage("Microsoft Visual Studio Code", "Microsoft.VisualStudioCode", "1.104.0", "1.105.0", "winget"), list[0]);
        Assert.AreEqual("OpenJS.NodeJS.LTS", list[2].Id);
    }

    [TestMethod]
    public void ParsesGermanTableAndIgnoresExplicitTargetingSection()
    {
        var output = """
            Name               ID                     Version   Verfügbar Quelle
            ------------------------------------------------------------------
            7-Zip 24.08 (x64)  7zip.7zip              24.08     25.01     winget
            1 Aktualisierungen verfügbar.
            Die folgenden Pakete haben ein Upgrade verfügbar, erfordern jedoch eine explizite Zielbestimmung für das Upgrade:
            Name   ID           Version Verfügbar Quelle
            --------------------------------------------
            Teams  Microsoft.Teams 1.0  2.0       winget
            """;

        var list = WingetParser.ParseUpgrades(output);

        Assert.AreEqual(1, list.Count, string.Join(" | ", list));
        Assert.AreEqual("7zip.7zip", list[0].Id);
        Assert.AreEqual("25.01", list[0].Available);
    }

    [TestMethod]
    public void NoUpgrades() => Assert.AreEqual(0, WingetParser.ParseUpgrades("No installed package found matching input criteria.").Count);

    [TestMethod]
    public void UnknownVersionsAreKept()
    {
        var output = """
            Name         Id              Version   Available Source
            -------------------------------------------------------
            Some App     Vendor.App      Unknown   2.0       winget
            """;

        Assert.AreEqual("Unknown", WingetParser.ParseUpgrades(output).Single().Version);
    }
}
