using Pollux.Copilot.Tools;

namespace Pollux.Copilot.Tests.Tools;

/// <summary>
/// A copy or move gives an entry whose name is taken in the destination a new name ("a (2).txt"); the tool reports
/// where each entry really went, from the shell's list of renamed entries.
/// </summary>
[TestClass]
public sealed class FileOperationTargetsTests
{
    private static FileOperation Copy(params string[] paths) =>
        FileOperation.Create(FileOperationKind.Copy, paths, @"D:\dest", null, null, out _)!;

    [TestMethod]
    public void WithoutRenamesEveryEntryKeepsItsName()
    {
        CollectionAssert.AreEqual(new[] { @"D:\dest\a.txt", @"D:\dest\b" }, Copy(@"C:\src\a.txt", @"C:\src\b").TargetsAfter([]).ToArray());
    }

    [TestMethod]
    public void ARenamedEntryIsReportedUnderItsNewName()
    {
        var renamed = new[] { new RenamedEntry(@"C:\src\a.txt", @"D:\dest\a (2).txt") };

        CollectionAssert.AreEqual(new[] { @"D:\dest\a (2).txt", @"D:\dest\b" }, Copy(@"C:\src\a.txt", @"C:\src\b").TargetsAfter(renamed).ToArray());
    }

    [TestMethod]
    public void TheShellMayNameTheIntendedTargetAsTheOldPath()
    {
        var renamed = new[] { new RenamedEntry(@"d:\DEST\a.txt", @"D:\dest\a - Copy.txt") };

        CollectionAssert.AreEqual(new[] { @"D:\dest\a - Copy.txt" }, Copy(@"C:\src\a.txt").TargetsAfter(renamed).ToArray());
    }
}
