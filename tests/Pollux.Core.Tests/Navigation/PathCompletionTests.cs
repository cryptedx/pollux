using Pollux.Core.Navigation;

namespace Pollux.Core.Tests.Navigation;

internal sealed class FakeLister : IDirectoryLister
{
    private readonly Dictionary<string, DirectoryItem[]> _folders = new(StringComparer.OrdinalIgnoreCase);

    public List<string> Calls { get; } = [];

    public FakeLister Add(string directory, params DirectoryItem[] items)
    {
        _folders[directory] = items;
        return this;
    }

    public IReadOnlyList<DirectoryItem> List(string directory, CancellationToken cancellationToken)
    {
        Calls.Add(directory);
        return _folders.TryGetValue(directory, out var items) ? items : [];
    }

    public static DirectoryItem Dir(string name, bool hidden = false) => new(name, true, hidden);

    public static DirectoryItem File(string name, bool hidden = false) => new(name, false, hidden);
}

[TestClass]
public sealed class PathCompletionTests
{
    private const string Current = @"C:\Users\marko";

    private static string? Env(string name) => name switch
    {
        "USERPROFILE" => @"C:\Users\marko",
        "TEMP" => @"C:\Temp",
        _ => null,
    };

    private static FakeLister Disk() => new FakeLister()
        .Add(@"C:\", FakeLister.Dir("Users"), FakeLister.Dir("Windows"), FakeLister.Dir("Program Files"), FakeLister.Dir("Program Files (x86)"),
            FakeLister.Dir("$Recycle.Bin", hidden: true), FakeLister.File("pagefile.sys", hidden: true), FakeLister.File("readme.txt"))
        .Add(@"C:\Users", FakeLister.Dir("marko"), FakeLister.Dir("Public"))
        .Add(@"C:\Users\marko", FakeLister.Dir("Documents"), FakeLister.Dir("Downloads"), FakeLister.Dir(".ssh"), FakeLister.File("notes.txt"), FakeLister.File("Doc.md"))
        .Add(@"C:\Temp", FakeLister.Dir("a2"), FakeLister.Dir("a10"), FakeLister.Dir("a1"))
        .Add(@"\\srv\share", FakeLister.Dir("Team"), FakeLister.File("t.txt"))
        .Add(@"\\srv\share\Team", FakeLister.Dir("Sub"));

    private static PathCompletionResult Run(string text, FakeLister? lister = null, bool showHidden = false, int max = 8, string current = Current) =>
        PathCompletion.Complete(text, current, showHidden, lister ?? Disk(), Env, max);

    private static string[] Texts(PathCompletionResult r) => r.Candidates.Select(c => c.Text).ToArray();

    [TestMethod]
    public void PrefixMatchIsCaseInsensitiveAndFoldersComeBeforeFiles()
    {
        var r = Run(@"C:\Users\marko\do");
        CollectionAssert.AreEqual(new[] { @"C:\Users\marko\Documents\", @"C:\Users\marko\Downloads\", @"C:\Users\marko\Doc.md" }, Texts(r));
        Assert.IsTrue(r.Candidates[0].IsDirectory);
        Assert.IsFalse(r.Candidates[2].IsDirectory);
    }

    [TestMethod]
    public void TypedDirectoryPartKeepsItsSpelling()
    {
        var r = Run(@"c:\users\ma");
        CollectionAssert.AreEqual(new[] { @"c:\users\marko\" }, Texts(r));
    }

    [TestMethod]
    public void DriveLetterWithColonCompletesToRoot()
    {
        CollectionAssert.AreEqual(new[] { @"c:\" }, Texts(Run("c:")));
    }

    [TestMethod]
    public void DriveLetterWithoutSeparatorListsDriveRoot()
    {
        CollectionAssert.AreEqual(new[] { @"C:\Windows\" }, Texts(Run("C:W")));
    }

    [TestMethod]
    public void RootListingFollowsShowHidden()
    {
        CollectionAssert.DoesNotContain(Texts(Run(@"C:\", max: 50)), @"C:\$Recycle.Bin\");
        var shown = Texts(Run(@"C:\", showHidden: true, max: 50));
        CollectionAssert.Contains(shown, @"C:\$Recycle.Bin\");
        CollectionAssert.Contains(shown, @"C:\pagefile.sys");
    }

    [TestMethod]
    public void RelativeNamesUseCurrentFolder()
    {
        CollectionAssert.AreEqual(new[] { @"Documents\", @"Downloads\", "Doc.md" }, Texts(Run("do")));
    }

    [TestMethod]
    public void RelativeSubfolderKeepsTypedPrefix()
    {
        var lister = Disk().Add(@"C:\Users\marko\Documents", FakeLister.Dir("Work"));
        CollectionAssert.AreEqual(new[] { @"Documents\Work\" }, Texts(Run(@"Documents\w", lister)));
    }

    [TestMethod]
    public void DotDotIsResolved()
    {
        CollectionAssert.AreEqual(new[] { @"..\Public\" }, Texts(Run(@"..\pu")));
    }

    [TestMethod]
    public void ForwardSlashesAreKept()
    {
        CollectionAssert.AreEqual(new[] { "C:/Users/marko/" }, Texts(Run("C:/Users/ma")));
    }

    [TestMethod]
    public void RootRelativePathUsesCurrentDrive()
    {
        CollectionAssert.AreEqual(new[] { @"\Users\" }, Texts(Run(@"\us")));
    }

    [TestMethod]
    public void TildeAndEnvironmentVariables()
    {
        CollectionAssert.AreEqual(new[] { @"~\" }, Texts(Run("~")));
        CollectionAssert.AreEqual(new[] { @"~\Documents\", @"~\Downloads\", @"~\Doc.md" }, Texts(Run(@"~\do")));
        CollectionAssert.AreEqual(new[] { @"%TEMP%\a1\", @"%TEMP%\a2\", @"%TEMP%\a10\" }, Texts(Run(@"%TEMP%\A")));
        CollectionAssert.AreEqual(new[] { @"%TEMP%\" }, Texts(Run("%TEMP%")));
        Assert.AreEqual(0, Run("%NOPE").Candidates.Count);
        Assert.AreEqual(0, Run("%NOPE%").Candidates.Count);
    }

    [TestMethod]
    public void NamesSortNaturally()
    {
        var names = Run(@"C:\Temp\a").Candidates.Select(c => c.Display).ToArray();
        CollectionAssert.AreEqual(new[] { "a1", "a2", "a10" }, names);
    }

    [TestMethod]
    public void QuotedPathKeepsLeadingQuote()
    {
        CollectionAssert.AreEqual(new[] { "\"C:\\Program Files\\", "\"C:\\Program Files (x86)\\" }, Texts(Run("\"C:\\Program")));
    }

    [TestMethod]
    public void ClosedQuoteOrBlankGivesNothing()
    {
        Assert.AreEqual(0, Run("\"C:\\Users\"").Candidates.Count);
        Assert.AreEqual(0, Run("   ").Candidates.Count);
        Assert.AreEqual(0, Run("").Candidates.Count);
    }

    [TestMethod]
    public void UncPathsComplete()
    {
        CollectionAssert.AreEqual(new[] { @"\\srv\share\Team\", @"\\srv\share\t.txt" }, Texts(Run(@"\\srv\share\t")));
        CollectionAssert.AreEqual(new[] { @"\\srv\share\Team\Sub\" }, Texts(Run(@"\\srv\share\Team\s")));
    }

    [TestMethod]
    public void UncWithoutShareListsNothing()
    {
        var lister = Disk();
        Assert.AreEqual(0, Run(@"\\srv\s", lister).Candidates.Count);
        Assert.AreEqual(0, Run(@"\\sr", lister).Candidates.Count);
        Assert.AreEqual(0, lister.Calls.Count);
    }

    [TestMethod]
    public void WellKnownNamesAreOffered()
    {
        var r = Run("ho");
        Assert.AreEqual("Home", r.Candidates.Single().Text);
        Assert.IsTrue(r.Candidates.Single().IsWellKnown);
        var rb = Run("rec");
        Assert.AreEqual("Recycle Bin", rb.Candidates.Single().Text);
        Assert.AreEqual(0, Run(@"Documents\ho").Candidates.Count);
    }

    [TestMethod]
    public void WellKnownComesAfterFoldersBeforeFiles()
    {
        var lister = new FakeLister().Add(Current, FakeLister.File("homework.txt"), FakeLister.Dir("homes"));
        CollectionAssert.AreEqual(new[] { @"homes\", "Home", "homework.txt" }, Texts(Run("home", lister)));
    }

    [TestMethod]
    public void VirtualCurrentFolderOffersOnlyWellKnown()
    {
        var lister = Disk();
        var r = Run("h", lister, current: WinPath.Home);
        CollectionAssert.AreEqual(new[] { "Home" }, Texts(r));
        Assert.AreEqual(0, lister.Calls.Count);
    }

    [TestMethod]
    public void CapAndMoreCount()
    {
        var r = Run(@"C:\", max: 2);
        Assert.AreEqual(2, r.Candidates.Count);
        Assert.AreEqual(3, r.More);
    }

    [TestMethod]
    public void CommonPrefixCoversAllMatches()
    {
        var r = Run(@"C:\Program");
        Assert.AreEqual(@"C:\Program Files", r.CommonPrefix);
        Assert.AreEqual(@"C:\Users\marko\Do", Run(@"C:\Users\marko\d").CommonPrefix);
        Assert.AreEqual(@"C:\Users\marko\Documents\", Run(@"C:\Users\marko\docu").CommonPrefix);
    }

    [TestMethod]
    public void NoMatchReturnsTypedTextAsPrefix()
    {
        var r = Run(@"C:\zzz");
        Assert.AreEqual(0, r.Candidates.Count);
        Assert.AreEqual(@"C:\zzz", r.CommonPrefix);
    }

    [TestMethod]
    public void ListerFailuresAreSwallowed()
    {
        var r = PathCompletion.Complete(@"C:\x\", Current, false, new ThrowingLister(), Env);
        Assert.AreEqual(0, r.Candidates.Count);
    }

    [TestMethod]
    public void CancellationPropagates()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(() =>
            PathCompletion.Complete(@"C:\", Current, false, new CancellingLister(), Env, cancellationToken: cts.Token));
    }

    [TestMethod]
    public void TabInsertsCommonPrefixThenCycles()
    {
        var typed = @"C:\Program";
        var session = new CompletionSession(typed, Run(typed));
        Assert.AreEqual(@"C:\Program Files", session.Tab());
        Assert.AreEqual(@"C:\Program Files\", session.Tab());
        Assert.AreEqual(@"C:\Program Files (x86)\", session.Tab());
        Assert.AreEqual(@"C:\Program Files\", session.Tab());
        Assert.AreEqual(@"C:\Program Files (x86)\", session.Tab(backwards: true));
    }

    [TestMethod]
    public void TabWithSingleCandidateAndNone()
    {
        var typed = @"C:\Users\ma";
        var session = new CompletionSession(typed, Run(typed));
        Assert.AreEqual(@"C:\Users\marko\", session.Tab());
        Assert.IsNull(new CompletionSession("zz", Run(@"C:\zzz")).Tab());
    }

    [TestMethod]
    public void TabCyclesWhenPrefixAddsNothing()
    {
        var typed = @"C:\Users\marko\Do";
        var session = new CompletionSession(typed, Run(typed));
        Assert.AreEqual(@"C:\Users\marko\Documents\", session.Tab());
        Assert.AreEqual(@"C:\Users\marko\Downloads\", session.Tab());
    }

    private sealed class ThrowingLister : IDirectoryLister
    {
        public IReadOnlyList<DirectoryItem> List(string directory, CancellationToken cancellationToken) =>
            throw new UnauthorizedAccessException();
    }

    private sealed class CancellingLister : IDirectoryLister
    {
        public IReadOnlyList<DirectoryItem> List(string directory, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return [];
        }
    }
}
