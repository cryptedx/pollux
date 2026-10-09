using GitHub.Copilot;
using Pollux.Copilot.Chat;

namespace Pollux.Copilot.Tests.Chat;

[TestClass]
public sealed class ToolApprovalsTests
{
    [TestMethod]
    public void FileWriteShowsThePathAndTheDiff()
    {
        var request = ToolApprovals.Describe("t1", new PermissionRequestWrite
        {
            CanOfferSessionApproval = false,
            Diff = "--- a/notes.txt\n+++ b/notes.txt\n-old\n+new",
            FileName = @"C:\work\notes.txt",
            Intention = "Update the notes",
        });

        Assert.AreEqual(new ToolApprovalRequest("t1", "write", "Update the notes", @"C:\work\notes.txt", "--- a/notes.txt\n+++ b/notes.txt\n-old\n+new"), request);
    }

    [TestMethod]
    public void UrlAccessShowsTheUrl()
    {
        var request = ToolApprovals.Describe("t2", new PermissionRequestUrl { Intention = "", Url = "https://example.com/x" });

        Assert.AreEqual(new ToolApprovalRequest("t2", "url", "Open a URL", "https://example.com/x"), request);
    }

    [TestMethod]
    public async Task PromptsAreAnsweredOnceEach()
    {
        var asked = new List<ToolApprovalRequest>();
        var approvals = new ToolApprovals(asked.Add);

        var first = approvals.AskAsync(new ToolApprovalRequest("", "shell", "Run", "dir"));
        var second = approvals.AskAsync(new ToolApprovalRequest("", "shell", "Run", "del x"));

        Assert.AreNotEqual(asked[0].Id, asked[1].Id);
        Assert.IsTrue(approvals.Answer(asked[1].Id, approved: true));
        Assert.IsFalse(approvals.Answer(asked[1].Id, approved: false), "An answered prompt cannot be answered again.");
        Assert.IsFalse(approvals.Answer("unknown", approved: true));
        Assert.IsTrue(await second);
        Assert.IsFalse(first.IsCompleted);
    }
}
