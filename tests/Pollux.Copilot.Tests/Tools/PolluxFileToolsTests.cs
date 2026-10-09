using System.Text.Json;
using Microsoft.Extensions.AI;
using Pollux.Copilot.Tools;
using Pollux.Core.Copilot.Guards;

namespace Pollux.Copilot.Tests.Tools;

/// <summary>The Pollux file operation and Bookmark tools: every call passes the gate (Guard Rules) before Pollux acts.</summary>
[TestClass]
public sealed class PolluxFileToolsTests
{
    private sealed class FakeFileActions : IPolluxFileActions
    {
        public List<string> Calls { get; } = [];

        public FileOperationResult Result { get; set; } = new(true, [], null);

        public Task<IReadOnlyList<PaneSnapshot>> ReadPanesAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<PaneSnapshot>>([]);

        public Task<NavigateResult> NavigateAsync(string pane, string location, bool newTab, CancellationToken cancellationToken) =>
            Task.FromResult(new NavigateResult(false, null, 0, "none"));

        public Task<int> SelectAsync(string pane, IReadOnlyList<string> names, CancellationToken cancellationToken) => Task.FromResult(0);

        public Task<FileOperationResult> RunAsync(FileOperation operation, CancellationToken cancellationToken)
        {
            Calls.Add(operation.Describe());
            return Task.FromResult(Result);
        }
    }

    private sealed class FakeGate : IPolluxToolGate
    {
        public List<PolluxToolCall> Calls { get; } = [];

        public string? Refusal { get; set; }

        public Task<string?> AuthorizeAsync(PolluxToolCall call, CancellationToken cancellationToken)
        {
            Calls.Add(call);
            return Task.FromResult(Refusal);
        }
    }

    private readonly FakeFileActions _actions = new();
    private readonly FakeGate _gate = new();

    private async Task<JsonElement> Call(string name, params (string Key, object? Value)[] args)
    {
        var tool = PolluxTools.Create(_actions, readOnly: false, _gate).Single(t => t.Name == name);
        var arguments = new AIFunctionArguments();
        foreach (var (k, v) in args) arguments[k] = v;
        var result = await tool.InvokeAsync(arguments, TestContext.CancellationToken);
        return JsonSerializer.SerializeToElement(result, tool.JsonSerializerOptions.GetTypeInfo(typeof(object)));
    }

    [TestMethod]
    public void FileToolsAreOfferedWithAGateAndNeverAPermanentDelete()
    {
        var names = PolluxTools.Create(_actions, readOnly: false, _gate).Select(t => t.Name).ToArray();

        CollectionAssert.IsSubsetOf(
            new[] { "pollux_copy", "pollux_move", "pollux_rename", "pollux_recycle", "pollux_new_folder", "pollux_add_bookmark" }, names);
        Assert.IsFalse(names.Any(n => n.Contains("delete", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void PlanModeAndAMissingGateOfferNoFileTools()
    {
        Assert.IsFalse(PolluxTools.Create(_actions, readOnly: true, _gate).Any(t => t.Name == "pollux_copy"));
        Assert.IsFalse(PolluxTools.Create(_actions, readOnly: false).Any(t => t.Name == "pollux_copy"));
    }

    [TestMethod]
    public async Task CopyReadsTheSourcesAndWritesIntoTheDestination()
    {
        var result = await Call("pollux_copy", ("sources", new[] { @"C:\in\a.pdf", @"C:\in\sub" }), ("destination", @"C:\work"));

        Assert.IsTrue(result.GetProperty("ok").GetBoolean());
        var call = _gate.Calls.Single();
        Assert.AreEqual("pollux_copy", call.Tool);
        Assert.HasCount(2, call.Requests);
        Assert.AreEqual(ToolKind.Read, call.Requests[0].Kind);
        CollectionAssert.AreEqual(new[] { @"C:\in\a.pdf", @"C:\in\sub" }, call.Requests[0].Paths.ToArray());
        Assert.AreEqual(ToolKind.Write, call.Requests[1].Kind);
        CollectionAssert.AreEqual(new[] { @"C:\work\a.pdf", @"C:\work\sub" }, call.Requests[1].Paths.ToArray());
        CollectionAssert.AreEqual(new[] { @"copy C:\in\a.pdf, C:\in\sub -> C:\work" }, _actions.Calls);
    }

    [TestMethod]
    public async Task MoveWritesSourcesAndTargets()
    {
        await Call("pollux_move", ("sources", new[] { @"C:\in\a.pdf" }), ("destination", @"C:\work\docs\"));

        var request = _gate.Calls.Single().Requests.Single();
        Assert.AreEqual(ToolKind.Write, request.Kind);
        CollectionAssert.AreEqual(new[] { @"C:\in\a.pdf", @"C:\work\docs\a.pdf" }, request.Paths.ToArray());
        CollectionAssert.AreEqual(new[] { @"move C:\in\a.pdf -> C:\work\docs" }, _actions.Calls);
    }

    [TestMethod]
    public async Task RenameWritesTheOldAndTheNewPath()
    {
        await Call("pollux_rename", ("path", @"C:\work\scan1.pdf"), ("newName", "Invoice 2026-03.pdf"));

        CollectionAssert.AreEqual(new[] { @"C:\work\scan1.pdf", @"C:\work\Invoice 2026-03.pdf" }, _gate.Calls.Single().Requests.Single().Paths.ToArray());
        CollectionAssert.AreEqual(new[] { @"rename C:\work\scan1.pdf -> Invoice 2026-03.pdf" }, _actions.Calls);
    }

    [TestMethod]
    public async Task RecycleIsAWriteOfEveryPathAndSaysRecycleBin()
    {
        await Call("pollux_recycle", ("paths", new[] { @"C:\work\old.tmp", @"C:\work\older.tmp" }));

        var call = _gate.Calls.Single();
        Assert.AreEqual(ToolKind.Write, call.Requests.Single().Kind);
        CollectionAssert.AreEqual(new[] { @"C:\work\old.tmp", @"C:\work\older.tmp" }, call.Requests.Single().Paths.ToArray());
        StringAssert.Contains(call.Title, "recycle bin");
        CollectionAssert.AreEqual(new[] { @"recycle C:\work\old.tmp, C:\work\older.tmp" }, _actions.Calls);
    }

    [TestMethod]
    public async Task NewFolderWritesTheNewFolder()
    {
        await Call("pollux_new_folder", ("parent", @"C:\work"), ("name", "Invoices"));

        CollectionAssert.AreEqual(new[] { @"C:\work\Invoices" }, _gate.Calls.Single().Requests.Single().Paths.ToArray());
        CollectionAssert.AreEqual(new[] { @"new folder C:\work\Invoices" }, _actions.Calls);
    }

    [TestMethod]
    public async Task AddBookmarkChangesNoFileButItsPathIsStillChecked()
    {
        await Call("pollux_add_bookmark", ("path", @"D:\projects"), ("name", "Projects"), ("group", "Work"));

        var request = _gate.Calls.Single().Requests.Single();
        Assert.AreEqual(ToolKind.Other, request.Kind);
        CollectionAssert.AreEqual(new[] { @"D:\projects" }, request.Paths.ToArray());
        CollectionAssert.AreEqual(new[] { @"bookmark D:\projects as Projects in Work" }, _actions.Calls);
    }

    [TestMethod]
    public async Task ARefusedCallChangesNothingAndTellsCopilotWhy()
    {
        _gate.Refusal = "Guard Rules: Protected secret (~\\.ssh)";

        var result = await Call("pollux_copy", ("sources", new[] { @"C:\Users\me\.ssh\id_rsa" }), ("destination", @"C:\work"));

        Assert.IsFalse(result.GetProperty("ok").GetBoolean());
        Assert.AreEqual("Guard Rules: Protected secret (~\\.ssh)", result.GetProperty("error").GetString());
        Assert.IsEmpty(_actions.Calls);
    }

    [TestMethod]
    public async Task PolluxFailuresAreReportedToCopilot()
    {
        _actions.Result = new(false, [], "Access denied.");

        var result = await Call("pollux_recycle", ("paths", new[] { @"C:\work\x" }));

        Assert.AreEqual("Access denied.", result.GetProperty("error").GetString());
    }

    [TestMethod]
    [DataRow("pollux_copy", "sources", "a.pdf", "destination", @"C:\work", "not a full Windows path")]
    [DataRow("pollux_copy", "sources", @"C:\work\..\Windows\x.dll", "destination", @"C:\temp", "not a full Windows path")]
    [DataRow("pollux_copy", "sources", @"C:\in\a", "destination", "work", "not a full Windows folder path")]
    [DataRow("pollux_copy", "sources", @"C:\in\sub", "destination", @"C:\in\sub\deeper", "into itself")]
    [DataRow("pollux_move", "sources", @"C:\work\a.pdf", "destination", @"C:\work", "already in")]
    [DataRow("pollux_move", "sources", @"C:\", "destination", @"D:\x", "root")]
    public async Task InvalidCopyOrMoveIsRefusedBeforeTheGate(string tool, string k1, string source, string k2, string destination, string error)
    {
        var result = await Call(tool, (k1, new[] { source }), (k2, destination));

        Assert.IsFalse(result.GetProperty("ok").GetBoolean());
        StringAssert.Contains(result.GetProperty("error").GetString(), error);
        Assert.IsEmpty(_gate.Calls);
        Assert.IsEmpty(_actions.Calls);
    }

    [TestMethod]
    [DataRow(@"..\x")]
    [DataRow(@"sub\x")]
    [DataRow("a:b")]
    [DataRow("..")]
    [DataRow("  ")]
    [DataRow("name.")]
    public async Task RenameTakesAPlainNameOnly(string newName)
    {
        var result = await Call("pollux_rename", ("path", @"C:\work\a.pdf"), ("newName", newName));

        Assert.IsFalse(result.GetProperty("ok").GetBoolean());
        Assert.IsEmpty(_actions.Calls);
    }

    [TestMethod]
    public void TheOperationOfAToolCallIsReadFromItsArguments()
    {
        var args = JsonDocument.Parse("""{"sources":["C:\\in\\a.pdf"],"destination":"C:\\work"}""").RootElement;

        var operation = FileOperation.FromToolCall("pollux_move", args);

        Assert.AreEqual(@"move C:\in\a.pdf -> C:\work", operation!.Describe());
        Assert.IsNull(FileOperation.FromToolCall("powershell", args));
        Assert.IsNull(FileOperation.FromToolCall("pollux_move", JsonDocument.Parse("""{"sources":["a.pdf"],"destination":"C:\\work"}""").RootElement));
        Assert.AreEqual(@"rename C:\w\a -> b", FileOperation.FromToolCall("pollux_rename", JsonDocument.Parse("""{"path":"C:\\w\\a","newName":"b"}""").RootElement)!.Describe());
    }

    public TestContext TestContext { get; set; } = null!;
}
