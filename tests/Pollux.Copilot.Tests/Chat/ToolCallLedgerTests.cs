using Pollux.Copilot.Chat;

namespace Pollux.Copilot.Tests.Chat;

/// <summary>
/// The notes the checkpoints of <see cref="ToolApprovals"/> leave each other about a tool call: each note is used up by
/// one later checkpoint, identical calls are counted, and calls never mix.
/// </summary>
[TestClass]
public sealed class ToolCallLedgerTests
{
    [TestMethod]
    public void AStartIsSeenOncePerHookCall()
    {
        var ledger = new ToolCallLedger();
        ledger.HookSaw("""{"path":"a"}""");
        ledger.HookSaw("""{"path":"a"}""");

        Assert.IsTrue(ledger.StartedWasSeen("""{"path":"a"}"""));
        Assert.IsTrue(ledger.StartedWasSeen("""{"path":"a"}"""));
        Assert.IsFalse(ledger.StartedWasSeen("""{"path":"a"}"""));
    }

    [TestMethod]
    public void StartsAreMatchedByCanonicalArguments()
    {
        var ledger = new ToolCallLedger();
        ledger.HookSaw("""{"b":1,"a":{"y":2,"x":[3,{"q":1,"p":0}]}}""");

        Assert.IsFalse(ledger.StartedWasSeen("""{"b":2,"a":{"y":2,"x":[3,{"q":1,"p":0}]}}"""));
        Assert.IsTrue(ledger.StartedWasSeen("""  { "a": { "x": [3, {"p":0,"q":1}], "y": 2 }, "b": 1 } """));
    }

    [TestMethod]
    public void NoArgumentsMatchNullAndEmpty()
    {
        var ledger = new ToolCallLedger();
        ledger.HookSaw(null);
        ledger.HookSaw("null");

        Assert.IsTrue(ledger.StartedWasSeen(""));
        Assert.IsTrue(ledger.StartedWasSeen("  "));
        Assert.IsFalse(ledger.StartedWasSeen(null));
    }

    [TestMethod]
    public void InvalidJsonIsMatchedAsTrimmedText()
    {
        Assert.AreEqual("{not json", ToolCallLedger.CanonicalArguments("  {not json "));
        Assert.AreEqual("""{"a":1,"b":2}""", ToolCallLedger.CanonicalArguments("""{ "b": 2, "a": 1 }"""));
    }

    [TestMethod]
    public void AnApprovalIsUsedUpOncePerYes()
    {
        var ledger = new ToolCallLedger();
        Assert.IsFalse(ledger.TakeApproved("k", Checkpoint.Tool));

        ledger.Approve("k", Checkpoint.Hook);
        ledger.Approve("k", Checkpoint.Hook);
        Assert.IsTrue(ledger.TakeApproved("k", Checkpoint.Tool));
        Assert.IsTrue(ledger.TakeApproved("k", Checkpoint.Tool));
        Assert.IsFalse(ledger.TakeApproved("k", Checkpoint.Tool));
    }

    [TestMethod]
    public void AYesPassesEachLaterCheckpointOnce()
    {
        var ledger = new ToolCallLedger();
        ledger.Approve("k", Checkpoint.Hook);

        Assert.IsFalse(ledger.PassApproved("k", Checkpoint.Hook), "Nothing comes before the hook.");
        Assert.IsTrue(ledger.PassApproved("k", Checkpoint.Permission));
        Assert.IsFalse(ledger.PassApproved("k", Checkpoint.Permission), "One yes, one call: a second permission request asks.");
        Assert.IsTrue(ledger.TakeApproved("k", Checkpoint.Tool));
        Assert.IsFalse(ledger.TakeApproved("k", Checkpoint.Tool));
    }

    [TestMethod]
    public void AYesSkippingACheckpointStillReachesTheNext()
    {
        var ledger = new ToolCallLedger();
        ledger.Approve("k", Checkpoint.Hook);
        ledger.Approve("k", Checkpoint.Permission);

        Assert.IsTrue(ledger.TakeApproved("k", Checkpoint.Tool));
        Assert.IsTrue(ledger.TakeApproved("k", Checkpoint.Tool));
        Assert.IsFalse(ledger.PassApproved("k", Checkpoint.Permission));
    }

    [TestMethod]
    public void AllowForChatStays()
    {
        var ledger = new ToolCallLedger();
        Assert.IsFalse(ledger.IsAllowedForChat("k"));

        ledger.AllowForChat("k");
        Assert.IsTrue(ledger.IsAllowedForChat("k"));
        Assert.IsTrue(ledger.IsAllowedForChat("k"));
        Assert.IsFalse(ledger.IsAllowedForChat("other"));
    }

    [TestMethod]
    public void AwaitingEntriesAreSettledFirstInFirstOutPerCall()
    {
        var ledger = new ToolCallLedger();
        ledger.AwaitDecision("k", 1);
        ledger.AwaitDecision("other", 2);
        ledger.AwaitDecision("k", 3);
        ledger.AwaitDecision("k", null); // no Action Log: nothing to settle

        Assert.AreEqual(1, ledger.TakeAwaiting("k"));
        Assert.AreEqual(3, ledger.TakeAwaiting("k"));
        Assert.IsNull(ledger.TakeAwaiting("k"));
        Assert.AreEqual(2, ledger.TakeAwaiting("other"));
        Assert.IsNull(ledger.TakeAwaiting("other"));
    }

    [TestMethod]
    public void ALoggedNoteIsUsedUpOncePerLog()
    {
        var ledger = new ToolCallLedger();
        ledger.Logged("k");

        Assert.IsFalse(ledger.TakeLogged("other"));
        Assert.IsTrue(ledger.TakeLogged("k"));
        Assert.IsFalse(ledger.TakeLogged("k"));
    }

    [TestMethod]
    public void NotesOfOneKindDoNotCountForAnother()
    {
        var ledger = new ToolCallLedger();
        ledger.Approve("k", Checkpoint.Hook);
        ledger.Logged("k");
        ledger.HookSaw("k");

        Assert.IsFalse(ledger.IsAllowedForChat("k"));
        Assert.IsNull(ledger.TakeAwaiting("k"));
        Assert.IsTrue(ledger.TakeLogged("k"));
        Assert.IsTrue(ledger.TakeApproved("k", Checkpoint.Tool));
        Assert.IsTrue(ledger.StartedWasSeen("k"));
        Assert.IsFalse(ledger.TakeApproved("k", Checkpoint.Tool));
    }

    [TestMethod]
    public void EveryNoteIsUsedUpExactlyOnceUnderContention()
    {
        const int Notes = 2000;
        var ledger = new ToolCallLedger();
        Parallel.For(0, Notes, i =>
        {
            ledger.Approve("k", Checkpoint.Hook);
            ledger.Logged("k");
            ledger.AwaitDecision("k", i);
        });

        var approved = 0;
        var logged = 0;
        var ids = new System.Collections.Concurrent.ConcurrentBag<int>();
        Parallel.For(0, Notes * 2, _ =>
        {
            if (ledger.TakeApproved("k", Checkpoint.Tool)) Interlocked.Increment(ref approved);
            if (ledger.TakeLogged("k")) Interlocked.Increment(ref logged);
            if (ledger.TakeAwaiting("k") is { } id) ids.Add(id);
        });

        Assert.AreEqual(Notes, approved);
        Assert.AreEqual(Notes, logged);
        CollectionAssert.AreEquivalent(Enumerable.Range(0, Notes).ToList(), ids.ToList());
    }
}
