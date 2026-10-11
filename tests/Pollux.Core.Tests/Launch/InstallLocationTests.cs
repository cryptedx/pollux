using Pollux.Core.Launch;

namespace Pollux.Core.Tests.Launch;

/// <summary>
/// Whether the Admin Instance warns about its own executable (ADR 0008, SECURITY.md S-09): the per-user install lives
/// where any process of the user can replace Pollux.exe and its DLLs; Program Files is writable only by admins.
/// </summary>
[TestClass]
public sealed class InstallLocationTests
{
    private static readonly string[] AdminOnly = [@"C:\Program Files", @"C:\Program Files (x86)"];

    private static bool Warns(string exeFolder) => InstallLocation.IsUserWritable(exeFolder, AdminOnly);

    [TestMethod]
    public void S09_PerUserInstallWarns()
    {
        Assert.IsTrue(Warns(@"C:\Users\me\AppData\Local\Programs\Pollux"));
    }

    [TestMethod]
    public void PerMachineInstallDoesNotWarn()
    {
        Assert.IsFalse(Warns(@"C:\Program Files\Pollux"));
        Assert.IsFalse(Warns(@"c:\program files (x86)\Pollux\"));
    }

    [TestMethod]
    public void S09_LookalikeOrEscapingFoldersWarn()
    {
        Assert.IsTrue(Warns(@"C:\Program Files Evil\Pollux"));
        Assert.IsTrue(Warns(@"C:\Program Files\..\Users\me\Pollux"));
        Assert.IsTrue(Warns(@"\\server\share\Program Files\Pollux"));
        Assert.IsTrue(Warns(@"D:\Pollux"));
        Assert.IsTrue(Warns(""));
    }

    [TestMethod]
    public void AnUnknownProgramFilesFolderIsNoReasonToTrustEverything()
    {
        Assert.IsTrue(InstallLocation.IsUserWritable(@"C:\Users\me\Pollux", ["", " "]));
    }
}
