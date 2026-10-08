using Pollux.Core.Navigation;

namespace Pollux.Core.Tests.Navigation;

[TestClass]
public sealed class WinPathTests
{
    private static string? Env(string name) => name switch
    {
        "USERPROFILE" => @"C:\Users\marko",
        "TEMP" => @"C:\Users\marko\AppData\Local\Temp",
        _ => null,
    };

    [TestMethod]
    public void SegmentsOfDrivePath()
    {
        CollectionAssert.AreEqual(
            new[] { @"C:\", @"C:\Users", @"C:\Users\marko" },
            WinPath.Segments(@"C:\Users\marko").ToArray());
    }

    [TestMethod]
    public void SegmentsOfUncPath()
    {
        CollectionAssert.AreEqual(
            new[] { @"\\server\share", @"\\server\share\dir" },
            WinPath.Segments(@"\\server\share\dir\").ToArray());
    }

    [TestMethod]
    [DataRow(@"C:\Users\marko", @"C:\Users")]
    [DataRow(@"C:\Users", @"C:\")]
    [DataRow(@"C:\", null)]
    [DataRow(@"\\server\share\dir", @"\\server\share")]
    [DataRow(@"\\server\share", null)]
    public void Parent(string path, string? parent) => Assert.AreEqual(parent, WinPath.Parent(path));

    [TestMethod]
    [DataRow(@"C:\Users\marko", "marko")]
    [DataRow(@"C:\", @"C:")]
    [DataRow(@"\\server\share", "share")]
    public void Name(string path, string name) => Assert.AreEqual(name, WinPath.Name(path));

    [TestMethod]
    [DataRow("~", @"C:\Users\marko")]
    [DataRow(@"~\repos", @"C:\Users\marko\repos")]
    [DataRow("~/repos/pollux", @"C:\Users\marko\repos\pollux")]
    [DataRow(@"%TEMP%\x", @"C:\Users\marko\AppData\Local\Temp\x")]
    [DataRow(@"..\other", @"C:\work\other")]
    [DataRow("sub", @"C:\work\project\sub")]
    [DataRow(@"D:", @"D:\")]
    [DataRow(@"d:/data/", @"D:\data")]
    [DataRow("  \"C:\\Program Files\"  ", @"C:\Program Files")]
    public void ResolvesTypedInput(string input, string expected) =>
        Assert.AreEqual(expected, WinPath.Resolve(input, @"C:\work\project", Env));

    [TestMethod]
    public void UnknownVariableStaysLiteral() =>
        Assert.AreEqual(@"C:\%NOPE%", WinPath.Resolve("%NOPE%", @"C:\", Env));

    [TestMethod]
    [DataRow(@"C:\a\b", @"C:\A", true)]
    [DataRow(@"C:\ab", @"C:\a", false)]
    [DataRow(@"C:\a", @"C:\a", true)]
    public void IsUnder(string path, string root, bool expected) => Assert.AreEqual(expected, WinPath.IsSameOrUnder(path, root));
}
