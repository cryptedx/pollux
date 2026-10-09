using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;

namespace Pollux.Copilot.Tests;

public sealed record EntryInfo(string Name, long Size);

[JsonSerializable(typeof(EntryInfo))]
internal sealed partial class TestToolJson : JsonSerializerContext;

/// <summary>
/// This project runs with System.Text.Json reflection disabled (as Native AOT does), so these
/// tests fail if a tool's schema, arguments or result fall back to reflection-based JSON.
/// </summary>
[TestClass]
public sealed class CopilotToolsTests
{
    [TestMethod]
    public void ReflectionBasedJsonIsOffInThisTestProject() =>
        Assert.IsFalse(JsonSerializer.IsReflectionEnabledByDefault);

    [TestMethod]
    public async Task ToolWithOwnResultTypeRunsOnSourceGeneratedJson()
    {
        var tool = CopilotTools.Define(
            (string name, int size) => new EntryInfo(name, size),
            "describe_entry", "Describes an entry", TestToolJson.Default);

        var result = await tool.InvokeAsync(new AIFunctionArguments { ["name"] = "a.txt", ["size"] = 42 }, TestContext.CancellationToken);

        Assert.AreEqual("""{"name":"a.txt","size":42}""", SerializeLikeTheSdk(tool, result));
    }

    [TestMethod]
    public void ToolSchemaDescribesParameters()
    {
        var tool = CopilotTools.Define((string name, bool recursive) => name, "list", "Lists a folder");

        var schema = tool.JsonSchema.ToString();

        StringAssert.Contains(schema, "\"name\"");
        StringAssert.Contains(schema, "\"recursive\"");
        Assert.AreEqual("list", tool.Name);
        Assert.AreEqual("Lists a folder", tool.Description);
    }

    /// <summary>What the SDK does with a tool result before sending it to the CLI.</summary>
    private static string SerializeLikeTheSdk(AIFunction tool, object? result) =>
        JsonSerializer.Serialize(result, tool.JsonSerializerOptions.GetTypeInfo(typeof(object)));

    public TestContext TestContext { get; set; } = null!;
}
