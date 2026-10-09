using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using GitHub.Copilot;
using Microsoft.Extensions.AI;

namespace Pollux.Copilot;

/// <summary>
/// Defines tools Pollux gives Copilot. Schemas, arguments and results go through source-generated
/// System.Text.Json metadata only, because Pollux is Native AOT with reflection-based JSON off (ADR 0002).
/// </summary>
public static class CopilotTools
{
    /// <param name="types">Source-generated metadata for the tool's own parameter and result types
    /// (a <see cref="JsonSerializerContext"/>); primitives and <see cref="JsonElement"/> are always covered.</param>
    public static AIFunction Define(
        Delegate method,
        string name,
        string description,
        IJsonTypeInfoResolver? types = null,
        CopilotToolOptions? options = null) =>
        CopilotTool.DefineTool(method, options, new AIFunctionFactoryOptions
        {
            Name = name,
            Description = description,
            SerializerOptions = SerializerOptions(types),
        });

    private static JsonSerializerOptions SerializerOptions(IJsonTypeInfoResolver? types)
    {
        var options = new JsonSerializerOptions(CopilotToolJson.Default.Options)
        {
            TypeInfoResolver = types is null
                ? CopilotToolJson.Default
                : JsonTypeInfoResolver.Combine(types, CopilotToolJson.Default),
        };
        options.MakeReadOnly();
        return options;
    }
}

/// <summary>
/// Types every tool needs. <see cref="JsonElement"/> is required: the SDK serializes the already
/// serialized tool result as <c>object</c>, which resolves to <see cref="JsonElement"/>.
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(long))]
[JsonSerializable(typeof(double))]
[JsonSerializable(typeof(string[]))]
internal sealed partial class CopilotToolJson : JsonSerializerContext;
