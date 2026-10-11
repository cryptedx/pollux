using Pollux.Core.Navigation;

namespace Pollux.Core.Tests.Navigation;

/// <summary>
/// The one network-drive check for git trust (ADR 0006) and Chat taint (SECURITY.md S-12): a drive whose type can't be
/// read counts as a network drive in both (fail closed), so the same drive is never "network" for one and "local" for
/// the other.
/// </summary>
[TestClass]
public sealed class NetworkDriveTests
{
    [TestMethod]
    [DataRow(DriveType.Network, true)]
    [DataRow(DriveType.Unknown, true)]
    [DataRow(DriveType.Fixed, false)]
    [DataRow(DriveType.Removable, false)]
    [DataRow(DriveType.CDRom, false)]
    [DataRow(DriveType.Ram, false)]
    public void TheDriveTypeDecides(DriveType type, bool network) =>
        Assert.AreEqual(network, NetworkDrive.IsNetwork(@"Z:\x", _ => type));

    [TestMethod]
    public void TheRootIsAskedFor() =>
        Assert.IsTrue(NetworkDrive.IsNetwork(@"Z:\projects\app", root => root == @"Z:\" ? DriveType.Network : DriveType.Fixed));

    [TestMethod]
    public void AnUnreadableDriveCountsAsNetwork()
    {
        Assert.IsTrue(NetworkDrive.IsNetwork(@"Z:\x", _ => throw new IOException("gone")));
        Assert.IsTrue(NetworkDrive.IsNetwork(@"Z:\x", _ => throw new UnauthorizedAccessException()));
        Assert.IsTrue(NetworkDrive.IsNetwork(@"Z:\x", _ => throw new ArgumentException("bad")));
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("relative\\path")]
    public void APathWithoutADriveCountsAsNetwork(string path) =>
        Assert.IsTrue(NetworkDrive.IsNetwork(path, _ => DriveType.Fixed));
}
