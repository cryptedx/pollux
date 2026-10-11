using Pollux.Core.Launch;

namespace Pollux.Core.Tests.Launch;

[TestClass]
public sealed class PathLookupTests
{
    private const string AppDir = @"C:\Users\me\AppData\Local\Programs\Pollux";
    private const string CurrentDir = @"C:\Downloads\evil";

    private static string? Find(string name, string path, params string[] files) =>
        PathLookup.Find(name, path, ".COM;.EXE;.BAT;.CMD", [AppDir, CurrentDir],
            f => files.Contains(f, StringComparer.OrdinalIgnoreCase));

    [TestMethod]
    public void BareNameIsFoundInTheFirstPathEntryThatHasItWithPathExtOrder()
    {
        Assert.AreEqual(@"C:\VS Code\bin\code.cmd",
            Find("code", @"C:\Windows\System32;C:\VS Code\bin", @"C:\VS Code\bin\code.cmd"));
        Assert.AreEqual(@"C:\Tools\code.exe",
            Find("code", @"C:\Tools", @"C:\Tools\code.cmd", @"C:\Tools\code.exe"));
    }

    [TestMethod]
    public void NameWithExtensionIsLookedUpAsIs()
    {
        Assert.AreEqual(@"C:\Apps\wt.exe", Find("wt.exe", @"C:\Apps", @"C:\Apps\wt.exe", @"C:\Apps\wt.exe.cmd"));
    }

    [TestMethod]
    public void S08_PlantedExeInTheCurrentOrPolluxFolderIsNeverChosenEvenWhenOnPath()
    {
        var path = $@"{CurrentDir};.;{AppDir}\;C:\Program Files\PowerShell\7";
        Assert.AreEqual(@"C:\Program Files\PowerShell\7\pwsh.exe",
            Find("pwsh.exe", path, $@"{CurrentDir}\pwsh.exe", $@"{AppDir}\pwsh.exe", @"C:\Program Files\PowerShell\7\pwsh.exe"));
    }

    [TestMethod]
    public void RelativeAndEmptyPathEntriesAreSkipped()
    {
        Assert.IsNull(Find("code", @";.;bin;..\tools;%SystemRoot%\x", @"bin\code.cmd", @".\code.cmd", @"code.cmd", @"..\tools\code.cmd"));
    }

    [TestMethod]
    public void QuotedPathEntriesWork()
    {
        Assert.AreEqual(@"C:\A;B\code.cmd", Find("code", "\"C:\\A;B\"", @"C:\A;B\code.cmd"));
    }

    [TestMethod]
    public void MissingToolIsNull()
    {
        Assert.IsNull(Find("code", @"C:\Windows\System32"));
    }

    [TestMethod]
    public void AbsoluteCommandIsUsedWhenItExists()
    {
        Assert.AreEqual(@"D:\VS Code\bin\code.cmd", Find(@"D:\VS Code\bin\code.cmd", "", @"D:\VS Code\bin\code.cmd"));
        Assert.AreEqual(@"D:\VS Code\bin\code.cmd", Find(@"D:\VS Code\bin\code", "", @"D:\VS Code\bin\code.cmd"));
        Assert.IsNull(Find(@"D:\VS Code\bin\code.cmd", ""));
    }

    [TestMethod]
    public void S02_RelativeCommandWithAFolderIsNeverResolvedAgainstTheCurrentFolder()
    {
        Assert.IsNull(Find(@".\code.cmd", @"C:\VS Code\bin", $@"{CurrentDir}\code.cmd", @"C:\VS Code\bin\code.cmd"));
        Assert.IsNull(Find(@"bin\code", @"C:\VS Code", @"C:\VS Code\bin\code.cmd"));
    }

    [TestMethod]
    public void MissingPathExtFallsBackToTheWindowsDefault()
    {
        Assert.AreEqual(@"C:\Tools\code.cmd",
            PathLookup.Find("code", @"C:\Tools", null, [], f => f == @"C:\Tools\code.cmd"));
    }
}
