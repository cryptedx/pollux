using Pollux.Core.Files;

namespace Pollux.Core.Tests.Files;

[TestClass]
public sealed class IconKeysTests
{
    private static readonly DateTime Day = new(2026, 1, 1);

    private static FileEntry F(string path, FileAttributes attributes = FileAttributes.Normal, DateTime? modified = null) =>
        new(Path.GetFileName(path.Replace('\\', '/')), path, false, 1, modified ?? Day, attributes);

    private static FileEntry D(string path, FileAttributes attributes = FileAttributes.Directory) =>
        new(Path.GetFileName(path.Replace('\\', '/')), path, true, 0, Day, attributes);

    // ---------------- key ----------------

    [TestMethod]
    public void OrdinaryFilesShareTheKeyOfTheirExtension()
    {
        var a = IconKey.For(F(@"C:\x\a.TXT"), 16);
        var b = IconKey.For(F(@"D:\y\b.txt", modified: Day.AddDays(3)), 16);

        Assert.AreEqual(a, b);
        Assert.IsFalse(a.PerPath);
        Assert.AreEqual(".txt", a.Name);
        Assert.AreEqual("file.txt", a.TypeName);
        Assert.AreNotEqual(a, IconKey.For(F(@"C:\x\a.txt"), 32), "another size");
    }

    [TestMethod]
    public void FilesWithoutExtensionShareOneKey()
    {
        var a = IconKey.For(F(@"C:\x\Makefile"), 16);
        Assert.AreEqual(a, IconKey.For(F(@"C:\y\LICENSE"), 16));
        Assert.AreEqual("", a.Name);
        Assert.AreEqual("file", a.TypeName);
        Assert.AreEqual(a, IconKey.For(F(@"C:\x\.gitignore"), 16), "a dot file has no extension");
    }

    [TestMethod]
    public void ProgramsIconsAndShortcutsAreKeyedByPathAndDate()
    {
        var exe = F(@"C:\Tools\App.exe");
        var key = IconKey.For(exe, 16);

        Assert.IsTrue(key.PerPath);
        Assert.AreEqual(@"c:\tools\app.exe", key.Name);
        Assert.AreEqual("file.exe", key.TypeName);
        Assert.AreNotEqual(key, IconKey.For(F(@"C:\Tools\Other.exe"), 16));
        Assert.AreNotEqual(key, IconKey.For(exe with { Modified = Day.AddSeconds(1) }, 16), "a rebuilt program");
        foreach (var name in new[] { "a.ico", "a.cur", "a.lnk", "a.url", "a.msi", "a.scr", "a.appref-ms" })
            Assert.IsTrue(IconKey.For(F(@"C:\x\" + name), 16).PerPath, name);
    }

    [TestMethod]
    public void ARegisteredPerFileTypeIsKeyedByPath()
    {
        Func<string, bool> own = ext => ext == ".sln";

        Assert.IsTrue(IconKey.For(F(@"C:\x\a.SLN"), 16, ownIconType: own).PerPath);
        Assert.IsFalse(IconKey.For(F(@"C:\x\a.txt"), 16, ownIconType: own).PerPath);
        Assert.IsFalse(IconKey.For(F(@"C:\x\Makefile"), 16, ownIconType: _ => true).PerPath, "no extension, nothing to look up");
    }

    [TestMethod]
    public void ShortcutsGetTheArrow()
    {
        Assert.IsTrue(IconKey.For(F(@"C:\x\a.lnk"), 16).LinkOverlay);
        Assert.IsTrue(IconKey.For(F(@"C:\x\a.url"), 16).LinkOverlay);
        Assert.IsFalse(IconKey.For(F(@"C:\x\a.exe"), 16).LinkOverlay);
        Assert.IsTrue(IconKey.For(F(@"\\srv\s\a.lnk"), 16).LinkOverlay, "also as the type icon on a share");
    }

    [TestMethod]
    public void RemoteEntriesGetTheirTypeIcon()
    {
        Assert.IsFalse(IconKey.For(F(@"\\srv\share\a.exe"), 16).PerPath);
        Assert.IsFalse(IconKey.For(F(@"Z:\a.exe"), 16, onNetworkDrive: true).PerPath);
        Assert.IsFalse(IconKey.For(F(@"C:\OneDrive\a.exe", (FileAttributes)0x00400000), 16).PerPath, "cloud-only placeholder");
        Assert.AreEqual(IconKey.For(F(@"C:\a\x.exe", FileAttributes.Offline), 16), IconKey.For(F(@"\\srv\s\y.exe"), 16));
        Assert.AreEqual("file.exe", IconKey.For(F(@"\\srv\s\y.exe"), 16).TypeName);
        Assert.IsFalse(IconKey.For(D(@"\\srv\share\Docs", FileAttributes.Directory | FileAttributes.ReadOnly), 16).PerPath);
    }

    [TestMethod]
    public void PlainFoldersShareOneKey()
    {
        var a = IconKey.For(D(@"C:\x\src"), 16);
        Assert.AreEqual(a, IconKey.For(D(@"D:\y\bin"), 16));
        Assert.IsTrue(a.IsDirectory);
        Assert.AreEqual("folder", a.TypeName);
        Assert.AreNotEqual(a, IconKey.For(F(@"C:\x\src"), 16), "a file without extension");
    }

    [TestMethod]
    public void CustomizedSpecialAndRootFoldersAreKeyedByPath()
    {
        Assert.IsTrue(IconKey.For(D(@"C:\Users\me\Documents", FileAttributes.Directory | FileAttributes.ReadOnly), 16).PerPath);
        Assert.IsTrue(IconKey.For(D(@"C:\x\Fonts", FileAttributes.Directory | FileAttributes.System), 16).PerPath);
        Assert.IsTrue(IconKey.For(D(@"E:\"), 16).PerPath, "a drive shows its own icon");
        Assert.AreEqual("folder", IconKey.For(D(@"E:\"), 16).TypeName);
    }

    [TestMethod]
    public void DriveRoots()
    {
        Assert.IsTrue(IconKeys.IsDriveRoot(@"C:\"));
        Assert.IsTrue(IconKeys.IsDriveRoot("c:"));
        Assert.IsFalse(IconKeys.IsDriveRoot(@"C:\x"));
        Assert.IsFalse(IconKeys.IsDriveRoot(@"\\srv"));
    }

    // ---------------- registration and locations ----------------

    [TestMethod]
    public void PerFileTypesAreThoseWhoseIconIsTheFileOrAHandler()
    {
        Assert.IsTrue(IconKeys.IsPerFileType("%1", false));
        Assert.IsTrue(IconKeys.IsPerFileType("\"%1\"", false));
        Assert.IsTrue(IconKeys.IsPerFileType(@"C:\x\app.exe,0", true));
        Assert.IsFalse(IconKeys.IsPerFileType(@"C:\x\app.exe,0", false));
        Assert.IsFalse(IconKeys.IsPerFileType(null, false));
    }

    [TestMethod]
    public void IniIconLocationReadsDesktopIniAndUrlFiles()
    {
        Assert.AreEqual(@"%SystemRoot%\system32\imageres.dll", IconKeys.IniIconLocation("[.ShellClassInfo]\r\nIconResource=%SystemRoot%\\system32\\imageres.dll,-112\r\n"));
        Assert.AreEqual(@"C:\a,b\x.ico", IconKeys.IniIconLocation("[.ShellClassInfo]\nIconFile=\"C:\\a,b\\x.ico\"\nIconIndex=0"));
        Assert.AreEqual(@"\\evil\s\i.ico", IconKeys.IniIconLocation("[InternetShortcut]\nURL=https://x\nIconFile=\\\\evil\\s\\i.ico\nIconIndex=1"));
        Assert.AreEqual("r.dll", IconKeys.IniIconLocation("IconFile=f.ico\nIconResource=r.dll,3"), "IconResource wins");
        Assert.IsNull(IconKeys.IniIconLocation("[.ShellClassInfo]\nLocalizedResourceName=@x.dll,-1\n"));
        Assert.IsNull(IconKeys.IniIconLocation(""));
    }

    [TestMethod]
    public void OnlyLocalIconLocationsAreRead()
    {
        Func<char, bool> zIsNetwork = d => d == 'Z';

        Assert.IsTrue(IconKeys.IsLocalLocation(@"C:\Windows\system32\shell32.dll", zIsNetwork));
        Assert.IsTrue(IconKeys.IsLocalLocation(@"\\?\C:\x.ico", zIsNetwork));
        Assert.IsTrue(IconKeys.IsLocalLocation("icon.ico", zIsNetwork), "next to the file");
        Assert.IsTrue(IconKeys.IsLocalLocation("", zIsNetwork));
        Assert.IsFalse(IconKeys.IsLocalLocation(@"\\evil\share\i.ico", zIsNetwork));
        Assert.IsFalse(IconKeys.IsLocalLocation(@"\\?\UNC\evil\share\i.ico", zIsNetwork));
        Assert.IsFalse(IconKeys.IsLocalLocation("//evil/share/i.ico", zIsNetwork));
        Assert.IsFalse(IconKeys.IsLocalLocation("https://example.com/favicon.ico", zIsNetwork));
        Assert.IsFalse(IconKeys.IsLocalLocation("file:///C:/x.ico", zIsNetwork));
        Assert.IsFalse(IconKeys.IsLocalLocation(@"z:\i.ico", zIsNetwork));
        Assert.IsFalse(IconKeys.IsLocalLocation("\"\\\\evil\\s\\i.ico\"", zIsNetwork));
    }

    // ---------------- sizes ----------------

    [TestMethod]
    public void PixelsFollowTheDisplayScale()
    {
        Assert.AreEqual(16, IconSizes.Pixels(16, 1.0));
        Assert.AreEqual(20, IconSizes.Pixels(16, 1.25));
        Assert.AreEqual(24, IconSizes.Pixels(16, 1.5));
        Assert.AreEqual(32, IconSizes.Pixels(16, 2.0));
        Assert.AreEqual(16, IconSizes.Pixels(16, 0.5), "never below 1:1");
    }

    [TestMethod]
    public void PickTakesTheSmallestListThatIsBigEnough()
    {
        Assert.AreEqual(ShellImageList.Small, IconSizes.Pick(16, 16, 32, 48));
        Assert.AreEqual(ShellImageList.Large, IconSizes.Pick(20, 16, 32, 48));
        Assert.AreEqual(ShellImageList.Large, IconSizes.Pick(32, 16, 32, 48));
        Assert.AreEqual(ShellImageList.ExtraLarge, IconSizes.Pick(40, 16, 32, 48));
        Assert.AreEqual(ShellImageList.ExtraLarge, IconSizes.Pick(96, 16, 32, 48), "nothing fits: the biggest");
        Assert.AreEqual(ShellImageList.Small, IconSizes.Pick(24, 24, 48, 48), "150 % system dpi");
        Assert.AreEqual(ShellImageList.Large, IconSizes.Pick(96, 32, 64, 48), "200 %: Large outgrows ExtraLarge");
    }

    // ---------------- pixels ----------------

    [TestMethod]
    public void ApplyMaskKeepsAnAlphaChannel()
    {
        byte[] color = [10, 20, 30, 128, 1, 2, 3, 0];
        IconPixels.ApplyMask(color, [255, 255, 255, 0, 255, 255, 255, 0]);
        CollectionAssert.AreEqual(new byte[] { 10, 20, 30, 128, 1, 2, 3, 0 }, color);
    }

    [TestMethod]
    public void ApplyMaskTakesAlphaFromTheMaskWhenThereIsNone()
    {
        byte[] color = [10, 20, 30, 0, 1, 2, 3, 0];
        IconPixels.ApplyMask(color, [0, 0, 0, 0, 255, 255, 255, 0]);
        CollectionAssert.AreEqual(new byte[] { 10, 20, 30, 255, 0, 0, 0, 0 }, color);
    }

    [TestMethod]
    public void PremultiplyScalesColorByAlpha()
    {
        byte[] bgra = [200, 100, 50, 255, 200, 100, 50, 128, 200, 100, 50, 0];
        IconPixels.Premultiply(bgra);
        CollectionAssert.AreEqual(new byte[] { 200, 100, 50, 255, 100, 50, 25, 128, 0, 0, 0, 0 }, bgra);
    }

    // ---------------- cache ----------------

    [TestMethod]
    public void CacheEvictsTheLeastRecentlyUsedAndRemembersMisses()
    {
        var cache = new IconCache<int, string>(2);
        cache.Add(1, "a");
        cache.Add(2, null);
        Assert.IsTrue(cache.TryGet(1, out _));
        cache.Add(3, "c");

        Assert.IsTrue(cache.TryGet(1, out var a));
        Assert.AreEqual("a", a);
        Assert.IsFalse(cache.TryGet(2, out _));
        Assert.IsTrue(cache.TryGet(3, out _));
        Assert.AreEqual(2, cache.Count);

        cache.Add(3, null);
        Assert.IsTrue(cache.TryGet(3, out var miss));
        Assert.IsNull(miss);
        Assert.AreEqual(2, cache.Count);
    }
}
