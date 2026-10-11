using Pollux.Core.Operations;

namespace Pollux.Core.Tests.Operations;

[TestClass]
public sealed class TransferBoardTests
{
    private static readonly string Parent = Path.Combine(Path.GetTempPath(), "board-src");
    private static readonly string Target = Path.Combine(Path.GetTempPath(), "board-dst");

    private static TransferJob Job(TransferState state = TransferState.Queued, TransferKind kind = TransferKind.Copy, TransferProgress? progress = null, string? error = null, int errors = 0)
    {
        var job = new TransferJob(new TransferRequest(kind, [Path.Combine(Parent, "a.txt")], Target));
        if (progress is not null) job.Report(progress);
        for (var i = 0; i < errors; i++) job.AddError("e" + i);
        if (state != TransferState.Queued) job.SetState(state, error);
        return job;
    }

    private static string Half => $"{0.5:P0}";

    private static TransferProgress Progress(long total = 1000, long done = 500, int files = 4, int doneFiles = 2, double speed = 2048) =>
        new(total, done, files, doneFiles, "x.bin", speed);

    [TestMethod]
    public void BadgeIsHiddenForAnEmptyOrCleanFinishedQueue()
    {
        Assert.IsFalse(TransferBoard.BadgeVisible([]));
        Assert.IsFalse(TransferBoard.BadgeVisible([Job(TransferState.Completed), Job(TransferState.Cancelled)]));
    }

    [TestMethod]
    public void BadgeShowsWhileActiveAndStaysAfterProblems()
    {
        Assert.IsTrue(TransferBoard.BadgeVisible([Job(TransferState.Running)]));
        Assert.IsTrue(TransferBoard.BadgeVisible([Job(TransferState.Queued)]));
        Assert.IsTrue(TransferBoard.BadgeVisible([Job(TransferState.Failed, error: "boom")]));
        Assert.IsTrue(TransferBoard.BadgeVisible([Job(TransferState.Completed, errors: 1)]));
    }

    [TestMethod]
    public void StatusTextShowsFirstActiveJobAndQueueLength()
    {
        var running = Job(TransferState.Running, TransferKind.Move, Progress());
        var queued = Job();
        var queued2 = Job();
        var pct = Half;

        Assert.AreEqual($"move {pct} · 2.0 K/s", TransferBoard.StatusText([running]));
        Assert.AreEqual($"move {pct} · 2.0 K/s +2", TransferBoard.StatusText([Job(TransferState.Completed), running, queued, queued2]));
        Assert.AreEqual($"copy {pct} · 2.0 K/s", TransferBoard.StatusText([Job(TransferState.Running, TransferKind.Copy, Progress())]));
    }

    [TestMethod]
    public void StatusTextForPausedAndProblemsOnly()
    {
        var paused = Job(TransferState.Paused, progress: Progress());
        Assert.AreEqual($"paused {Half} +1", TransferBoard.StatusText([paused, Job()]));
        Assert.AreEqual("⚠ problems", TransferBoard.StatusText([Job(TransferState.Failed, error: "x")]));
    }

    [TestMethod]
    public void RowStatusForEachState()
    {
        var p = Progress(total: 3 * 1024 * 1024, done: 1024, files: 3, doneFiles: 1, speed: 1536);
        Assert.AreEqual("waiting", TransferBoard.RowStatus(Job()));
        Assert.AreEqual("1/3 files · 1.0 K of 3.0 M · 1.5 K/s", TransferBoard.RowStatus(Job(TransferState.Running, progress: p)));
        Assert.AreEqual("paused · 1.0 K of 3.0 M", TransferBoard.RowStatus(Job(TransferState.Paused, progress: p)));
        Assert.AreEqual("done · 3 files · 3.0 M", TransferBoard.RowStatus(Job(TransferState.Completed, progress: p)));
        Assert.AreEqual("cancelled", TransferBoard.RowStatus(Job(TransferState.Cancelled)));
        Assert.AreEqual("failed: disk full", TransferBoard.RowStatus(Job(TransferState.Failed, error: "disk full")));
    }

    [TestMethod]
    public void FinishedToastTextAndDuration()
    {
        var clean = Job(TransferState.Completed);
        Assert.AreEqual(($"✓ {clean.Title}", 3), TransferBoard.FinishedToast(clean));

        var withProblems = Job(TransferState.Completed, errors: 2);
        Assert.AreEqual(($"⚠ {withProblems.Title}: 2 problem(s) — see transfers", 6), TransferBoard.FinishedToast(withProblems));

        var cancelled = Job(TransferState.Cancelled);
        Assert.AreEqual(($"Cancelled: {cancelled.Title}", 3), TransferBoard.FinishedToast(cancelled));

        var failed = Job(TransferState.Failed, error: "denied");
        Assert.AreEqual(($"✗ {failed.Title}: denied", 6), TransferBoard.FinishedToast(failed));
    }

    [TestMethod]
    public void AffectsTargetAndSourceParentIgnoringCase()
    {
        var job = Job();
        Assert.IsTrue(TransferBoard.Affects(job, Target));
        Assert.IsTrue(TransferBoard.Affects(job, Parent));
        Assert.IsTrue(TransferBoard.Affects(job, Target.ToUpperInvariant()));
        Assert.IsFalse(TransferBoard.Affects(job, Path.Combine(Parent, "sub")));
        Assert.IsFalse(TransferBoard.Affects(job, Path.GetTempPath()));
    }

    [TestMethod]
    public void VisibleJobsAreTheNewestEightNewestFirst()
    {
        var jobs = Enumerable.Range(0, 10).Select(_ => Job()).ToList();

        var visible = TransferBoard.VisibleJobs(jobs);

        CollectionAssert.AreEqual(jobs.AsEnumerable().Reverse().Take(8).Select(j => j.Id).ToList(), visible.Select(j => j.Id).ToList());
        Assert.AreEqual(jobs[9].Id, visible[0].Id);
        Assert.AreEqual(8, visible.Count);
        Assert.AreEqual(2, TransferBoard.VisibleJobs(jobs.Take(2).ToList()).Count);
    }

    [TestMethod]
    public void RebuildOnlyWhenRowsStateOrErrorsChange()
    {
        var a = Job(TransferState.Running);
        var b = Job();
        var shown = new Dictionary<int, TransferBoard.RowSnapshot>
        {
            [a.Id] = TransferBoard.RowSnapshot.Of(a),
            [b.Id] = TransferBoard.RowSnapshot.Of(b),
        };
        List<TransferJob> visible = [b, a];

        Assert.IsFalse(TransferBoard.NeedsRebuild(shown, visible), "progress alone refills in place");

        a.Report(Progress());
        Assert.IsFalse(TransferBoard.NeedsRebuild(shown, visible));

        a.AddError("oops");
        Assert.IsTrue(TransferBoard.NeedsRebuild(shown, visible), "new error line");

        shown[a.Id] = TransferBoard.RowSnapshot.Of(a);
        b.SetState(TransferState.Running);
        Assert.IsTrue(TransferBoard.NeedsRebuild(shown, visible), "buttons depend on state");

        shown[b.Id] = TransferBoard.RowSnapshot.Of(b);
        Assert.IsFalse(TransferBoard.NeedsRebuild(shown, visible));
        Assert.IsTrue(TransferBoard.NeedsRebuild(shown, [a]), "different row count");
        Assert.IsTrue(TransferBoard.NeedsRebuild(shown, [a, Job()]), "unknown job id");
    }

    private static TransferConflict Conflict(DateTime source, DateTime target, bool isDirectory = false) =>
        new(Path.Combine(Parent, "a.txt"), Path.Combine(Target, "a.txt"), isDirectory, 2048, source, 1024, target);

    [TestMethod]
    public void ConflictTextMarksTheNewerSide()
    {
        var older = new DateTime(2024, 1, 2, 3, 4, 0);
        var newer = new DateTime(2025, 6, 7, 8, 9, 0);

        var sourceNewer = TransferBoard.ConflictText(Conflict(newer, older));
        Assert.AreEqual(
            $"a.txt already exists in {Target}\n\n" +
            "new:           2.0 K  2025-06-07 08:09  (newer)\n" +
            "existing:      1.0 K  2024-01-02 03:04",
            sourceNewer);

        var targetNewer = TransferBoard.ConflictText(Conflict(older, newer));
        StringAssert.Contains(targetNewer, "new:           2.0 K  2024-01-02 03:04\n");
        StringAssert.EndsWith(targetNewer, "2025-06-07 08:09  (newer)");

        var same = TransferBoard.ConflictText(Conflict(older, older));
        Assert.DoesNotContain("(newer)", same);
    }

    [TestMethod]
    public void ConflictTextForFileInTheWayOfAFolder()
    {
        var text = TransferBoard.ConflictText(Conflict(DateTime.Now, DateTime.Now, isDirectory: true));

        Assert.AreEqual("A file named \"a.txt\" is in the way of a folder.", text);
    }
}
