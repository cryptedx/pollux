using Pollux.Core.Files;

namespace Pollux.Core.Tests.Files;

[TestClass]
public sealed class LinksTests
{
    [TestMethod]
    public void ShortcutNameAppendsSuffixAndLnk()
    {
        Assert.AreEqual("notes.txt - Shortcut.lnk", Links.SuggestName(LinkKind.Shortcut, "notes.txt", false, _ => false));
        Assert.AreEqual("src - Shortcut.lnk", Links.SuggestName(LinkKind.Shortcut, "src", true, _ => false));
    }

    [TestMethod]
    public void SymlinkKeepsExtensionOfFiles()
    {
        Assert.AreEqual("notes - Link.txt", Links.SuggestName(LinkKind.Symlink, "notes.txt", false, _ => false));
        Assert.AreEqual("my.dir - Link", Links.SuggestName(LinkKind.Symlink, "my.dir", true, _ => false));
        Assert.AreEqual(".env - Link", Links.SuggestName(LinkKind.Symlink, ".env", false, _ => false));
    }

    [TestMethod]
    public void NameIsMadeUniqueCaseInsensitive()
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "src - Link", "SRC - Link (2)" };

        Assert.AreEqual("src - Link (3)", Links.SuggestName(LinkKind.Junction, "src", true, taken.Contains));
    }

    [TestMethod]
    public void JunctionNeedsFolder()
    {
        Assert.IsNotNull(Links.Validate(LinkKind.Junction, @"C:\a\file.txt", false, @"C:\b"));
        Assert.IsNull(Links.Validate(LinkKind.Junction, @"C:\a\dir", true, @"C:\b"));
        Assert.IsNull(Links.Validate(LinkKind.Symlink, @"C:\a\file.txt", false, @"C:\b"));
    }

    [TestMethod]
    public void JunctionRejectsNetworkPaths()
    {
        Assert.IsNotNull(Links.Validate(LinkKind.Junction, @"\\server\share\dir", true, @"C:\b"));
        Assert.IsNotNull(Links.Validate(LinkKind.Junction, @"C:\a\dir", true, @"\\server\share"));
    }

    [TestMethod]
    public void RejectsEmptyAndInsideSelf()
    {
        Assert.IsNotNull(Links.Validate(LinkKind.Symlink, "", false, @"C:\b"));
        Assert.IsNotNull(Links.Validate(LinkKind.Symlink, @"C:\a\dir", true, ""));
        Assert.IsNotNull(Links.Validate(LinkKind.Symlink, @"C:\a\dir", true, @"C:\a\dir\sub"));
        Assert.IsNotNull(Links.Validate(LinkKind.Junction, @"C:\a\DIR", true, @"c:\a\dir"));
        Assert.IsNull(Links.Validate(LinkKind.Symlink, @"C:\a\dir", true, @"C:\a\dir2"));
        Assert.IsNull(Links.Validate(LinkKind.Shortcut, @"C:\a\dir", true, @"C:\a\dir"));
    }

    [TestMethod]
    public void ValidatesFileNames()
    {
        Assert.IsNull(Links.ValidateName("ok - Link"));
        Assert.IsNotNull(Links.ValidateName(""));
        Assert.IsNotNull(Links.ValidateName("a\\b"));
        Assert.IsNotNull(Links.ValidateName("a\"b"));
        Assert.IsNotNull(Links.ValidateName("con"));
        Assert.IsNotNull(Links.ValidateName("trailing."));
    }

    [TestMethod]
    public void ExplainsMissingSymlinkPrivilege()
    {
        StringAssert.Contains(Links.ExplainError(LinkKind.Symlink, 1314, "x"), "Developer Mode");
        Assert.AreEqual("boom", Links.ExplainError(LinkKind.Shortcut, 5, "boom"));
    }

    [TestMethod]
    public void DescribesTargets()
    {
        Assert.AreEqual("", Links.Describe(null, null));
        Assert.AreEqual(@"Symlink → C:\x", Links.Describe(LinkKind.Symlink, @"C:\x"));
        Assert.AreEqual(@"Junction → C:\x", Links.Describe(LinkKind.Junction, @"\??\C:\x"));
    }
}
