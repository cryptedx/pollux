using Pollux.Core.Operations;

namespace Pollux.Core.Tests.Operations;

[TestClass]
public sealed class TransfersTests
{
    [TestMethod]
    public async Task RunsJobsInOrderAndCancelsQueuedOnes()
    {
        var root = Directory.CreateTempSubdirectory("pollux-queue-");
        try
        {
            var src = Directory.CreateDirectory(Path.Combine(root.FullName, "src")).FullName;
            var dst = Directory.CreateDirectory(Path.Combine(root.FullName, "dst")).FullName;
            for (var i = 0; i < 3; i++) File.WriteAllText(Path.Combine(src, $"f{i}.txt"), new string('x', 1000));

            var queue = new Transfers();
            Task<ConflictDecision> Skip(TransferConflict _) => Task.FromResult(new ConflictDecision(ConflictChoice.Skip, true));
            var first = queue.Enqueue(new TransferRequest(TransferKind.Copy, [Path.Combine(src, "f0.txt")], dst), Skip);
            var second = queue.Enqueue(new TransferRequest(TransferKind.Copy, [Path.Combine(src, "f1.txt")], dst), Skip);
            var third = queue.Enqueue(new TransferRequest(TransferKind.Copy, [Path.Combine(src, "f2.txt")], dst), Skip);
            third.Cancel();

            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (queue.IsBusy && DateTime.UtcNow < deadline) await Task.Delay(20);

            Assert.AreEqual(TransferState.Completed, first.State);
            Assert.AreEqual(TransferState.Completed, second.State);
            Assert.AreEqual(TransferState.Cancelled, third.State);
            Assert.IsFalse(File.Exists(Path.Combine(dst, "f2.txt")));

            queue.ClearFinished();
            Assert.AreEqual(0, queue.Jobs.Count);
        }
        finally { root.Delete(true); }
    }
}
