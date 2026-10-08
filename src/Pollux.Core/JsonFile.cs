using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Pollux.Core.Places;
using Pollux.Core.Session;

namespace Pollux.Core;

/// <summary>Load/save for Pollux's own state files (bookmarks.json, recent.json, session.json). Broken files fall back to empty state.</summary>
public static class JsonFile
{
    public static T Load<T>(string path, JsonTypeInfo<T> type) where T : new()
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize(File.ReadAllText(path), type) ?? new T() : new T();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new T();
        }
    }

    public static void Save<T>(string path, T value, JsonTypeInfo<T> type)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        AtomicFile.WriteAllText(path, JsonSerializer.Serialize(value, type));
    }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    WriteIndented = true)]
[JsonSerializable(typeof(BookmarkList))]
[JsonSerializable(typeof(RecentList))]
[JsonSerializable(typeof(SessionState))]
public sealed partial class StateJson : JsonSerializerContext;
