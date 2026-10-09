using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pollux.Core.Settings;

public sealed record SettingsResult(PolluxSettings Settings, string? Error)
{
    public bool IsValid => Error is null;
}

/// <summary>Reads and writes settings.json (JSONC: comments and trailing commas allowed).</summary>
public static class SettingsStore
{
    public static SettingsResult Parse(string json, PolluxSettings? fallback = null)
    {
        try
        {
            var settings = JsonSerializer.Deserialize(json, SettingsJsonContext.Default.PolluxSettings) ?? new PolluxSettings();
            return new SettingsResult(Normalize(settings), null);
        }
        catch (JsonException ex)
        {
            return new SettingsResult(fallback ?? new PolluxSettings(), ex.Message);
        }
    }

    /// <summary>Replaces sections or lists written as null in the JSON with their defaults.</summary>
    private static PolluxSettings Normalize(PolluxSettings s)
    {
        s.Appearance ??= new();
        s.App ??= new();
        s.Files ??= new();
        s.Tabs ??= new();
        s.Sidebar ??= new();
        s.Recent ??= new();
        s.Recent.Exclude ??= [];
        s.StatusBar ??= new();
        s.Terminal ??= new();
        s.Integration ??= new();
        s.Filter ??= new();
        s.Search ??= new();
        s.Search.IndexRoots ??= [];
        s.Search.ExcludeNames ??= new SearchSettings().ExcludeNames;
        s.Notes ??= new();
        s.Downloads ??= new();
        s.Updates ??= new();
        s.Updates.IgnoredPackages ??= [];
        s.Inbox ??= new();
        s.Preview ??= new();
        s.Copilot ??= new();
        s.Copilot.PromptTemplates ??= [];
        s.Copilot.GuardRules ??= new();
        s.Copilot.GuardRules.BlockedCommands ??= [];
        s.Copilot.GuardRules.AskCommands ??= [];
        s.Copilot.GuardRules.ProtectedPaths ??= [];
        s.Copilot.GuardRules.UntrustedPaths ??= [];
        s.Clipboard ??= new();
        s.Clipboard.ExcludeProcesses ??= [];
        s.Files.OperationEngine ??= "pollux";
        s.Appearance.UiFont ??= "Segoe UI Variable Text";
        s.Appearance.MonoFont ??= "Cascadia Mono";
        s.Files.DateFormat ??= "yyyy-MM-dd HH:mm";
        return s;
    }

    public static string Serialize(PolluxSettings settings) =>
        JsonSerializer.Serialize(settings, SettingsJsonContext.Default.PolluxSettings);

    public static SettingsResult Load(string file, PolluxSettings? fallback = null)
    {
        if (!File.Exists(file))
        {
            var defaults = new PolluxSettings();
            Save(file, defaults);
            return new SettingsResult(defaults, null);
        }

        try
        {
            return Parse(File.ReadAllText(file), fallback);
        }
        catch (IOException ex)
        {
            return new SettingsResult(fallback ?? new PolluxSettings(), ex.Message);
        }
    }

    public static void Save(string file, PolluxSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        AtomicFile.WriteAllText(file, Serialize(settings));
    }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    WriteIndented = true,
    UseStringEnumConverter = true,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(PolluxSettings))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;
