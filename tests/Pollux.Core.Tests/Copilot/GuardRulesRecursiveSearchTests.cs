using Pollux.Core.Copilot.Guards;
using Pollux.Core.Settings;

namespace Pollux.Core.Tests.Copilot;

/// <summary>
/// A recursive search inside the Working Folder reads every file below the searched folder: it is asked, in every
/// Permission Mode, when protected files actually lie below it; otherwise the Permission Mode decides.
/// </summary>
[TestClass]
public sealed class GuardRulesRecursiveSearchTests
{
    private const string WorkingFolder = @"C:\repos\app";

    private static GuardContext Context() => new(WorkingFolder, false, name => name switch
    {
        "USERPROFILE" => @"C:\Users\marko",
        "APPDATA" => @"C:\Users\marko\AppData\Roaming",
        "LOCALAPPDATA" => @"C:\Users\marko\AppData\Local",
        _ => null,
    });

    /// <summary>Folders and the files below them (relative paths), as the fake file system lists them.</summary>
    private sealed class FakeFolders(Dictionary<string, string[]> folders) : IFolderFiles
    {
        public List<string> Listed { get; } = [];

        public IEnumerable<FolderEntry> Below(string folder)
        {
            Listed.Add(folder);
            return folders.TryGetValue(folder, out var files) ? files.Select(f => f.EndsWith('\\') ? FolderEntry.Folder(f.TrimEnd('\\')) : FolderEntry.File(f)) : [];
        }

        public string? Canonical(string path) => path;
    }

    /// <summary>A clock that moves on by <paramref name="step"/> every time it is read (zero: time stands still).</summary>
    private sealed class SteppingClock(TimeSpan step) : TimeProvider
    {
        private long _now;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Interlocked.Add(ref _now, step.Ticks) - step.Ticks;
    }

    /// <summary>Time stands still, so only the entry count limits the check (deterministic on slow machines).</summary>
    private static readonly TimeProvider StoppedClock = new SteppingClock(TimeSpan.Zero);

    private static GuardVerdict Evaluate(ToolRequest request, FakeFolders folders, GuardRuleSettings? settings = null, TimeProvider? clock = null) =>
        new GuardRules(settings, folders, clock ?? StoppedClock).Evaluate(request, Context());

    [TestMethod]
    public void SearchOverAFolderWithProtectedFilesIsAsked()
    {
        var folders = new FakeFolders(new() { [@"C:\repos\app\src"] = [@"Program.cs", @"certs\prod.key"] });

        var verdict = Evaluate(ToolRequest.Shell("rg TODO src"), folders);

        Assert.AreEqual(GuardDecision.Ask, verdict.Decision);
        Assert.AreEqual(@"Recursive search over protected files (1): certs\prod.key", verdict.Reason);
    }

    [TestMethod]
    [DataRow("rg TODO")]
    [DataRow("grep -rn TODO .")]
    [DataRow("findstr /s TODO *.cs")]
    [DataRow("Get-ChildItem -Recurse | Get-Content")]
    public void SearchOverAFolderWithoutProtectedFilesIsLeftToThePermissionMode(string command)
    {
        var folders = new FakeFolders(new() { [WorkingFolder] = [@"src\", @"src\Program.cs", "README.md", "keys.txt"] });

        Assert.AreEqual(GuardDecision.Allow, Evaluate(ToolRequest.Shell(command), folders).Decision, command);
    }

    [TestMethod]
    [DataRow("rg TODO")]
    [DataRow("grep -rn TODO .")]
    [DataRow("findstr /s TODO *.cs")]
    [DataRow("Get-ChildItem -Recurse | Get-Content")]
    [DataRow("ls -R")]
    public void EverySearchFormOverTheWorkingFolderSeesItsProtectedFiles(string command)
    {
        var folders = new FakeFolders(new() { [WorkingFolder] = [".env"] });

        Assert.AreEqual(GuardDecision.Ask, Evaluate(ToolRequest.Shell(command), folders).Decision, command);
    }

    [TestMethod]
    public void TheReasonNamesFiveFilesAndCountsAll()
    {
        var folders = new FakeFolders(new() { [WorkingFolder] = [".env", ".env.local", @"a\1.key", @"a\2.key", @"b\c.pfx", @"b\d.pfx", "ok.cs"] });

        var verdict = Evaluate(ToolRequest.Shell("rg TODO ."), folders);

        Assert.AreEqual(@"Recursive search over protected files (6): .env, .env.local, a\1.key, a\2.key, b\c.pfx", verdict.Reason);
    }

    [TestMethod]
    public void ProtectedPatternsFromSettingsCount()
    {
        var folders = new FakeFolders(new() { [WorkingFolder] = [@"config\secrets.json"] });
        var settings = new GuardRuleSettings { ProtectedPaths = ["secrets.json"] };

        Assert.AreEqual(GuardDecision.Ask, Evaluate(ToolRequest.Shell("rg TODO"), folders, settings).Decision);
        Assert.AreEqual(GuardDecision.Allow, Evaluate(ToolRequest.Shell("rg TODO"), folders).Decision);
    }

    [TestMethod]
    public void AFolderTooLargeToCheckIsAsked()
    {
        var many = Enumerable.Range(0, GuardRules.MaxFolderEntries + 1).Select(i => $"f{i}.cs").ToArray();
        var folders = new FakeFolders(new() { [WorkingFolder] = many });

        var verdict = Evaluate(ToolRequest.Shell("rg TODO"), folders);

        Assert.AreEqual(GuardDecision.Ask, verdict.Decision);
        Assert.AreEqual(GuardRules.FolderTooLarge, verdict.Reason);
    }

    [TestMethod]
    public void AFolderJustWithinTheBudgetIsChecked()
    {
        var many = Enumerable.Range(0, GuardRules.MaxFolderEntries).Select(i => $"f{i}.cs").ToArray();

        Assert.AreEqual(GuardDecision.Allow, Evaluate(ToolRequest.Shell("rg TODO"), new FakeFolders(new() { [WorkingFolder] = many })).Decision);
    }

    [TestMethod]
    public void AFolderTakingLongerThanTheTimeBudgetIsAsked()
    {
        var files = Enumerable.Range(0, 10).Select(i => $"f{i}.cs").ToArray();
        var slow = new SteppingClock(GuardRules.MaxFolderCheckTime / 4);

        var verdict = Evaluate(ToolRequest.Shell("rg TODO"), new FakeFolders(new() { [WorkingFolder] = files }), clock: slow);

        Assert.AreEqual(GuardRules.FolderTooLarge, verdict.Reason);
    }

    [TestMethod]
    public void TheBuiltInSearchToolOverAFolderWithProtectedFilesIsAsked()
    {
        var folders = new FakeFolders(new() { [@"C:\repos\app\deploy"] = ["site.pfx"] });

        var verdict = Evaluate(ToolRequest.Read(@"C:\repos\app\deploy") with { Recursive = true }, folders);

        Assert.AreEqual(GuardDecision.Ask, verdict.Decision);
        Assert.AreEqual("Recursive search over protected files (1): site.pfx", verdict.Reason);
    }

    [TestMethod]
    public void ANonRecursiveReadDoesNotLookBelowTheFolder()
    {
        var folders = new FakeFolders(new() { [WorkingFolder] = ["site.pfx"] });

        Assert.AreEqual(GuardDecision.Allow, Evaluate(ToolRequest.Read(WorkingFolder), folders).Decision);
        Assert.AreEqual(0, folders.Listed.Count);
    }

    [TestMethod]
    public void ASearchOutsideTheWorkingFolderIsAskedWithoutListingIt()
    {
        var folders = new FakeFolders(new() { [@"D:\other"] = ["site.pfx"] });

        Assert.AreEqual("Recursive search outside the Working Folder", Evaluate(ToolRequest.Shell(@"rg TODO D:\other"), folders).Reason);
        Assert.AreEqual(0, folders.Listed.Count);
    }
}
