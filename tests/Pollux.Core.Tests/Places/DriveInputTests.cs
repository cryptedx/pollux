using Pollux.Core.Places;

namespace Pollux.Core.Tests.Places;

[TestClass]
public sealed class DriveInputTests
{
    [TestMethod]
    [DataRow("z", "Z:")]
    [DataRow("Z:", "Z:")]
    [DataRow(" y:\\ ", "Y:")]
    public void Letter_is_normalized(string input, string expected)
    {
        Assert.IsTrue(DriveInput.TryNormalizeLetter(input, out var letter));
        Assert.AreEqual(expected, letter);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow(null)]
    [DataRow("ZZ:")]
    [DataRow("1:")]
    [DataRow("::")]
    public void Letter_rejects_garbage(string? input) => Assert.IsFalse(DriveInput.TryNormalizeLetter(input, out _));

    [TestMethod]
    [DataRow(@"\\srv\share", @"\\srv\share")]
    [DataRow("//srv/share/", @"\\srv\share")]
    [DataRow("\"\\\\nas.local\\My Docs\\\"", @"\\nas.local\My Docs")]
    public void Share_is_normalized(string input, string expected)
    {
        Assert.IsTrue(DriveInput.TryNormalizeShare(input, out var share));
        Assert.AreEqual(expected, share);
    }

    [TestMethod]
    [DataRow(@"C:\folder")]
    [DataRow(@"\\srv")]
    [DataRow(@"\\srv\")]
    [DataRow(@"\\\share")]
    [DataRow(@"\\srv\share\sub")]
    [DataRow(@"\\srv\sh*re")]
    [DataRow(@"\\srv\ share")]
    [DataRow("")]
    [DataRow(null)]
    public void Share_rejects_garbage(string? input) => Assert.IsFalse(DriveInput.TryNormalizeShare(input, out _));

    [TestMethod]
    public void NextFreeLetter_counts_down_from_Z()
    {
        Assert.AreEqual("Z:", DriveInput.NextFreeLetter([@"C:\", @"D:\"]));
        Assert.AreEqual("X:", DriveInput.NextFreeLetter([@"z:\", "Y:"]));
    }

    [TestMethod]
    public void NextFreeLetter_is_null_when_all_taken()
    {
        var all = Enumerable.Range('D', 'Z' - 'D' + 1).Select(c => (char)c + @":\");
        Assert.IsNull(DriveInput.NextFreeLetter(all));
    }

    [TestMethod]
    public void Actions_depend_on_drive_type()
    {
        Assert.IsTrue(DriveInput.CanDisconnect(DriveType.Network));
        Assert.IsFalse(DriveInput.CanDisconnect(DriveType.Fixed));
        Assert.IsTrue(DriveInput.CanEject(DriveType.CDRom));
        Assert.IsTrue(DriveInput.CanEject(DriveType.Removable));
        Assert.IsFalse(DriveInput.CanEject(DriveType.Fixed));
    }
}
