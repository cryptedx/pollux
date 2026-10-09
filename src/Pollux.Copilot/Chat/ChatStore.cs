using System.Text.Json;
using System.Text.Json.Serialization;
using Pollux.Core.Settings;

namespace Pollux.Copilot.Chat;

/// <summary>A Chat in the list: Pollux's own record next to the SDK session of the same <see cref="Id"/> (the CLI keeps the history).</summary>
public sealed class ChatRecord
{
    /// <summary>Own SDK session id; reused verbatim to resume.</summary>
    public string Id { get; set; } = "";

    public string Title { get; set; } = "";
    public DateTimeOffset Created { get; set; }
    public DateTimeOffset Modified { get; set; }

    /// <summary>Model of the Chat; null means the CLI's default.</summary>
    public string? Model { get; set; }

    /// <summary>Working Folder of the Chat; null means none.</summary>
    public string? WorkingFolder { get; set; }

    /// <summary>Agent Mode of the Chat, restored when it is resumed.</summary>
    public CopilotAgentMode AgentMode { get; set; }

    /// <summary>Permission Mode of the Chat, restored when it is resumed.</summary>
    public CopilotPermissionMode PermissionMode { get; set; }

    /// <summary>The user's choice for the sandbox toggle; null means not chosen (on for Autopilot or Allow All, off otherwise).</summary>
    public bool? Sandbox { get; set; }

    /// <summary>Tainted Chat: it has read untrusted content and asks before shell, network and write actions until the user clears it.</summary>
    public bool Tainted { get; set; }

    /// <summary>What made the Chat Tainted (shown with the marker); null when it is not.</summary>
    public string? TaintedBy { get; set; }
}

/// <summary>The list of Chats, global (not per Workspace), kept in <c>chats.json</c> in <c>%LocalAppData%\Pollux\copilot</c>. Broken files read as empty.</summary>
public sealed class ChatStore(string directory)
{
    private readonly Lock _gate = new();
    private List<ChatRecord>? _chats;

    private string FilePath => Path.Combine(directory, "chats.json");

    /// <summary>All Chats, most recently used first.</summary>
    public IReadOnlyList<ChatRecord> List()
    {
        lock (_gate) return [.. Load().OrderByDescending(c => c.Modified)];
    }

    public ChatRecord? Get(string id)
    {
        lock (_gate) return Load().FirstOrDefault(c => c.Id == id);
    }

    /// <summary>Adds the Chat or replaces the one with the same id.</summary>
    public void Save(ChatRecord record)
    {
        lock (_gate)
        {
            var chats = Load();
            var i = chats.FindIndex(c => c.Id == record.Id);
            if (i >= 0) chats[i] = record; else chats.Add(record);
            Write(chats);
        }
    }

    public void Delete(string id)
    {
        lock (_gate)
        {
            var chats = Load();
            if (chats.RemoveAll(c => c.Id == id) > 0) Write(chats);
        }
    }

    private List<ChatRecord> Load()
    {
        if (_chats is not null) return _chats;
        try
        {
            _chats = File.Exists(FilePath)
                ? JsonSerializer.Deserialize(File.ReadAllText(FilePath), ChatStoreJson.Default.ChatFile)?.Chats ?? []
                : [];
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _chats = [];
        }

        return _chats;
    }

    private void Write(List<ChatRecord> chats)
    {
        Directory.CreateDirectory(directory);
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(new ChatFile { Chats = chats }, ChatStoreJson.Default.ChatFile));
        File.Move(temp, FilePath, overwrite: true);
    }
}

internal sealed class ChatFile
{
    public List<ChatRecord> Chats { get; set; } = [];
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(ChatFile))]
internal sealed partial class ChatStoreJson : JsonSerializerContext;
