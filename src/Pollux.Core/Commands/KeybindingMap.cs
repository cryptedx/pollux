using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pollux.Core.Commands;

/// <summary>Resolves key chords to command ids: defaults from command definitions, then keybindings.json on top (VS Code semantics).</summary>
public sealed class KeybindingMap
{
    private readonly List<(KeyChord Chord, string Command, string? When)> _bindings;

    private KeybindingMap(List<(KeyChord, string, string?)> bindings) => _bindings = bindings;

    public IReadOnlyList<string> Errors { get; private init; } = [];

    public static KeybindingMap Build(IEnumerable<CommandDefinition> commands, IEnumerable<KeybindingEntry> user)
    {
        var bindings = new List<(KeyChord, string, string?)>();
        var errors = new List<string>();
        var whenByCommand = new Dictionary<string, string?>(StringComparer.Ordinal);

        foreach (var c in commands)
        {
            whenByCommand[c.Id] = c.When;
            if (c.DefaultKey is not null)
            {
                bindings.Add((KeyChord.Parse(c.DefaultKey), c.Id, c.When));
            }
        }

        foreach (var e in user)
        {
            if (!KeyChord.TryParse(e.Key, out var chord))
            {
                errors.Add($"Invalid key '{e.Key}' for command '{e.Command}'.");
                continue;
            }

            if (e.Command.StartsWith('-'))
            {
                var id = e.Command[1..];
                bindings.RemoveAll(b => b.Item1 == chord && b.Item2 == id);
            }
            else
            {
                bindings.Add((chord, e.Command, e.When ?? whenByCommand.GetValueOrDefault(e.Command)));
            }
        }

        return new KeybindingMap(bindings) { Errors = errors };
    }

    /// <summary>Later bindings win, like in VS Code.</summary>
    public string? Resolve(KeyChord chord, Context context)
    {
        for (var i = _bindings.Count - 1; i >= 0; i--)
        {
            var b = _bindings[i];
            if (b.Chord == chord && context.Matches(b.When))
            {
                return b.Command;
            }
        }

        return null;
    }

    public KeyChord? KeyFor(string commandId)
    {
        for (var i = _bindings.Count - 1; i >= 0; i--)
        {
            if (_bindings[i].Command == commandId)
            {
                return _bindings[i].Chord;
            }
        }

        return null;
    }

    public static List<KeybindingEntry> ParseUserFile(string json) =>
        JsonSerializer.Deserialize(json, KeybindingJsonContext.Default.ListKeybindingEntry) ?? [];

    public static string SerializeUserFile(IEnumerable<KeybindingEntry> entries) =>
        JsonSerializer.Serialize(entries.ToList(), KeybindingJsonContext.Default.ListKeybindingEntry);
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(List<KeybindingEntry>))]
internal sealed partial class KeybindingJsonContext : JsonSerializerContext;
