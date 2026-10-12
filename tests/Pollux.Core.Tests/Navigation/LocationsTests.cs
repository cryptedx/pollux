using Pollux.Core.Navigation;

namespace Pollux.Core.Tests.Navigation;

[TestClass]
public sealed class LocationsTests
{
    private static bool ZipExists(string path) => path.EndsWith(@"\a.zip", StringComparison.OrdinalIgnoreCase);

    private static string? Env(string name) => name == "USERPROFILE" ? @"C:\Users\marko" : null;

    [TestMethod]
    [DataRow(WinPath.Home, LocationKind.Home)]
    [DataRow(WinPath.RecycleBin, LocationKind.RecycleBin)]
    [DataRow(WinPath.ThisPc, LocationKind.Shell)]
    [DataRow(WinPath.Network, LocationKind.Shell)]
    [DataRow(WinPath.ControlPanel, LocationKind.Shell)]
    [DataRow(WinPath.Libraries, LocationKind.Shell)]
    [DataRow(@"::{20D04FE0-3AEA-1069-A2D8-08002B30309D}\\\?\usb#vid_04e8\SID-{10001}", LocationKind.Shell)]
    [DataRow("pollux:shell:uuid:1234", LocationKind.Shell)]
    [DataRow(@"\\server", LocationKind.Server)]
    [DataRow(@"\\server\", LocationKind.Server)]
    [DataRow(@"\\server\share", LocationKind.Folder)]
    [DataRow(@"\\?\C:\x", LocationKind.Folder)]
    [DataRow(@"C:\data\a.zip", LocationKind.Zip)]
    [DataRow(@"C:\data\a.zip\docs\sub", LocationKind.Zip)]
    [DataRow(@"C:\data\b.zip\docs", LocationKind.Folder)]
    [DataRow(@"C:\data", LocationKind.Folder)]
    public void Classifies(string location, LocationKind kind) => Assert.AreEqual(kind, Locations.Classify(location, ZipExists));

    [TestMethod]
    public void AccessPerKind()
    {
        Assert.AreEqual(new LocationAccess(true, true, true, true), Locations.Access(LocationKind.Folder));
        Assert.AreEqual(new LocationAccess(false, true, true, false), Locations.Access(LocationKind.Zip));
        Assert.AreEqual(new LocationAccess(false, true, true, false), Locations.Access(LocationKind.Shell));
        Assert.AreEqual(new LocationAccess(false, true, false, false), Locations.Access(LocationKind.Server));
        Assert.IsFalse(Locations.Access(LocationKind.Home).CanOpenItems);
        Assert.IsFalse(Locations.Access(LocationKind.RecycleBin).CanModify);
    }

    [TestMethod]
    [DataRow(WinPath.ThisPc, Locations.ThisPcParsingName)]
    [DataRow(WinPath.Network, Locations.NetworkParsingName)]
    [DataRow("pollux:shell:uuid:1234", "uuid:1234")]
    [DataRow(WinPath.Libraries, "shell:Libraries")]
    public void ParsingNames(string location, string parsing) => Assert.AreEqual(parsing, Locations.ParsingName(location));

    [TestMethod]
    [DataRow("::{20d04fe0-3aea-1069-a2d8-08002b30309d}", null, WinPath.ThisPc)]
    [DataRow(Locations.NetworkParsingName, null, WinPath.Network)]
    [DataRow("::{031e4825-7b94-4dc3-b131-e946b44c8dd5}", null, WinPath.Libraries)]
    [DataRow("::{26ee0668-a00a-44d7-9371-beb064c98683}", null, WinPath.ControlPanel)]
    [DataRow(@"::{20D04FE0-3AEA-1069-A2D8-08002B30309D}\C:\", @"C:\", @"C:\")]
    [DataRow(@"::{031E4825-7B94-4DC3-B131-E946B44C8DD5}\Documents.library-ms", null, @"::{031E4825-7B94-4DC3-B131-E946B44C8DD5}\Documents.library-ms")]
    [DataRow(@"\\NAS", null, @"\\NAS")]
    [DataRow("uuid:1234", null, "pollux:shell:uuid:1234")]
    public void FromShellItems(string parsing, string? fileSystemPath, string location) =>
        Assert.AreEqual(location, Locations.FromShell(parsing, fileSystemPath));

    [TestMethod]
    public void ShellLocationsRoundTripThroughTheirParsingName()
    {
        foreach (var l in new[] { WinPath.ThisPc, WinPath.Network, WinPath.ControlPanel, WinPath.Libraries, "pollux:shell:uuid:1", @"::{20D04FE0-3AEA-1069-A2D8-08002B30309D}\\\?\usb#x" })
        {
            Assert.AreEqual(l, Locations.FromShell(Locations.ParsingName(l)));
        }
    }

    [TestMethod]
    [DataRow(@"\\server", "server")]
    [DataRow(@"//server/", "server")]
    [DataRow(@"\\server\share", null)]
    [DataRow(@"\\?", null)]
    [DataRow(@"\\", null)]
    [DataRow(@"C:\", null)]
    public void ServerOf(string location, string? server) => Assert.AreEqual(server, Locations.ServerOf(location));

    [TestMethod]
    [DataRow(WinPath.Home, "Home")]
    [DataRow(WinPath.RecycleBin, "Recycle Bin")]
    [DataRow(WinPath.ThisPc, "This PC")]
    [DataRow(WinPath.Network, "Network")]
    [DataRow(WinPath.ControlPanel, "Control Panel")]
    [DataRow(WinPath.Libraries, "Libraries")]
    [DataRow(@"\\nas", "nas")]
    [DataRow(@"C:\data\a.zip\docs", "docs")]
    [DataRow(@"C:\", "C:")]
    [DataRow("pollux:shell:uuid:1", null)]
    public void Titles(string location, string? title) => Assert.AreEqual(title, Locations.Title(location));

    [TestMethod]
    [DataRow(@"C:\Users\marko", @"C:\Users")]
    [DataRow(@"C:\", WinPath.ThisPc)]
    [DataRow(@"\\nas\share\dir", @"\\nas\share")]
    [DataRow(@"\\nas\share", @"\\nas")]
    [DataRow(@"\\nas", WinPath.Network)]
    [DataRow(@"C:\data\a.zip\docs", @"C:\data\a.zip")]
    [DataRow(@"C:\data\a.zip", @"C:\data")]
    [DataRow(WinPath.ThisPc, WinPath.Home)]
    [DataRow(WinPath.Network, WinPath.Home)]
    [DataRow(WinPath.ControlPanel, WinPath.Home)]
    [DataRow(WinPath.RecycleBin, WinPath.Home)]
    [DataRow(WinPath.Home, null)]
    [DataRow(@"::{20D04FE0-3AEA-1069-A2D8-08002B30309D}\\\?\usb#x", null)]
    public void Parents(string location, string? parent) => Assert.AreEqual(parent, Locations.Parent(location, ZipExists));

    [TestMethod]
    public void SegmentsOfASharePutTheServerFirst()
    {
        var s = Locations.Segments(@"\\nas\share\dir");
        CollectionAssert.AreEqual(new[] { "nas", "share", "dir" }, s.Select(x => x.Label).ToArray());
        CollectionAssert.AreEqual(new[] { @"\\nas", @"\\nas\share", @"\\nas\share\dir" }, s.Select(x => x.Location).ToArray());
    }

    [TestMethod]
    public void SegmentsOfAZipPathAreItsPath()
    {
        var s = Locations.Segments(@"C:\data\a.zip\docs");
        CollectionAssert.AreEqual(new[] { "C:", "data", "a.zip", "docs" }, s.Select(x => x.Label).ToArray());
        Assert.AreEqual(@"C:\data\a.zip", s[2].Location);
    }

    [TestMethod]
    public void SegmentsOfVirtualLocationsAreOneCrumb()
    {
        Assert.AreEqual(new LocationSegment("This PC", WinPath.ThisPc), Locations.Segments(WinPath.ThisPc).Single());
        Assert.AreEqual(new LocationSegment("nas", @"\\nas"), Locations.Segments(@"\\nas").Single());
        Assert.AreEqual(new LocationSegment("home", WinPath.Home), Locations.Segments(WinPath.Home).Single());
        Assert.AreEqual("Galaxy", Locations.Segments("pollux:shell:uuid:1", "Galaxy").Single().Label);
    }

    [TestMethod]
    [DataRow("this pc", WinPath.ThisPc)]
    [DataRow("  Network ", WinPath.Network)]
    [DataRow("shell:Libraries", WinPath.Libraries)]
    [DataRow("::{20D04FE0-3AEA-1069-A2D8-08002B30309D}", WinPath.ThisPc)]
    [DataRow("shell:Downloads", "shell:Downloads")]
    [DataRow(@"\\nas", @"\\nas")]
    [DataRow(@"\\nas\share\", @"\\nas\share")]
    [DataRow(@"..\x", @"C:\work\x")]
    [DataRow(@"~\a.zip\docs", @"C:\Users\marko\a.zip\docs")]
    public void ResolvesTypedLocations(string typed, string location) =>
        Assert.AreEqual(location, Locations.ResolveTyped(typed, @"C:\work\project", Env));

    [TestMethod]
    public void NormalizeKeepsShellParsingNamesIntact()
    {
        const string phone = @"::{20D04FE0-3AEA-1069-A2D8-08002B30309D}\\\?\usb#vid";
        Assert.AreEqual(phone, Locations.Normalize(phone));
        Assert.AreEqual(WinPath.Home, Locations.Normalize(WinPath.Home));
        Assert.AreEqual(@"C:\data", Locations.Normalize("c:/data/"));
    }

    [TestMethod]
    [DataRow(@"\\nas", "nas")]
    [DataRow(@"\\nas\", "nas")]
    [DataRow(@"\\nas\share", "nas")]
    [DataRow(@"\\nas\share\dir\x.txt", "nas")]
    [DataRow("//nas/share", "nas")]
    [DataRow(@"\\?\C:\x", null)]
    [DataRow(@"\\.\pipe\x", null)]
    [DataRow(@"C:\data", null)]
    [DataRow(WinPath.Network, null)]
    public void ServerOfAnyUncPath(string location, string? server) => Assert.AreEqual(server, Locations.ServerOfPath(location));
}
