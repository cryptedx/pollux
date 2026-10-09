using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pollux.Copilot.Chat;

/// <summary>What became of a tool call.</summary>
public enum ActionDecision
{
    /// <summary>It went ahead without asking the user.</summary>
    Allowed,

    /// <summary>The user was asked and allowed it.</summary>
    Asked,

    /// <summary>It was refused: by the Guard Rules, or because the user said no.</summary>
    Blocked,
}

/// <summary>Who made the <see cref="ActionDecision"/>.</summary>
public enum ActionDecider
{
    /// <summary>The Chat's Permission Mode let it through.</summary>
    Mode,

    /// <summary>A Guard Rule blocked it or made it ask.</summary>
    GuardRule,

    /// <summary>The user, in the approval prompt (or earlier with "Allow for this Chat").</summary>
    User,
}

/// <summary>One tool call of a Chat.</summary>
public sealed class ActionLogEntry
{
    /// <summary>Position in the Chat's log, starting at 1.</summary>
    public int Id { get; set; }

    public DateTimeOffset Time { get; set; }

    /// <summary>The tool as the CLI names it (powershell, edit, view, web_fetch, ...).</summary>
    public string Tool { get; set; } = "";

    /// <summary>The full command text for shell tools; null otherwise.</summary>
    public string? Command { get; set; }

    public List<string> Paths { get; set; } = [];
    public List<string> Urls { get; set; } = [];
    public ActionDecision Decision { get; set; }
    public ActionDecider DecidedBy { get; set; }

    /// <summary>Why: the Guard Rules' reason, the Permission Mode, or the user's answer.</summary>
    public string Reason { get; set; } = "";
}

/// <summary>
/// The Action Log of one Chat (spec "Action log"): every tool call with tool, command, paths, URLs, decision, who decided
/// and why, blocked ones included. Kept in a file next to the Chat so it survives restart and resume.
/// </summary>
public sealed class ActionLog
{
    private readonly Lock _gate = new();
    private readonly string _path;
    private readonly List<ActionLogEntry> _entries;
    private int _next;

    internal ActionLog(string path)
    {
        _path = path;
        _entries = Read(path);
        _next = _entries.Count == 0 ? 1 : _entries.Max(e => e.Id) + 1;
    }

    /// <summary>A copy of the entries, oldest first.</summary>
    public IReadOnlyList<ActionLogEntry> Entries
    {
        get { lock (_gate) return [.. _entries.Select(Copy)]; }
    }

    /// <summary>Appends <paramref name="entry"/> (it gets the next <see cref="ActionLogEntry.Id"/>; the time is set when still empty) and stores it.</summary>
    public ActionLogEntry Add(ActionLogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_gate)
        {
            var added = Copy(entry);
            added.Id = _next++;
            if (added.Time == default) added.Time = DateTimeOffset.UtcNow;
            _entries.Add(added);
            Append(added);
            return Copy(added);
        }
    }

    /// <summary>Changes the entry <paramref name="id"/> (a decision that turned out different, e.g. the user was asked after all) and stores the log again.</summary>
    public void Update(int id, Action<ActionLogEntry> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        lock (_gate)
        {
            var entry = _entries.Find(e => e.Id == id);
            if (entry is null) return;
            change(entry);
            entry.Id = id;
            Rewrite();
        }
    }

    internal void Clear()
    {
        lock (_gate) _entries.Clear();
    }

    private static ActionLogEntry Copy(ActionLogEntry e) => new()
    {
        Id = e.Id, Time = e.Time, Tool = e.Tool, Command = e.Command, Paths = [.. e.Paths], Urls = [.. e.Urls],
        Decision = e.Decision, DecidedBy = e.DecidedBy, Reason = e.Reason,
    };

    private static List<ActionLogEntry> Read(string path)
    {
        List<ActionLogEntry> entries = [];
        try
        {
            if (!File.Exists(path)) return entries;
            foreach (var line in File.ReadLines(path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    if (JsonSerializer.Deserialize(line, ActionLogJson.Default.ActionLogEntry) is { } entry) entries.Add(entry);
                }
                catch (JsonException)
                {
                    // A torn or damaged line must not hide the rest of the log.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Unreadable log reads as what could be read.
        }

        return entries;
    }

    private void Append(ActionLogEntry entry)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.AppendAllText(_path, JsonSerializer.Serialize(entry, ActionLogJson.Default.ActionLogEntry) + "\n", Encoding.UTF8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The log is a record, not a gate: a disk error must not stop the Chat.
        }
    }

    private void Rewrite()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, string.Concat(_entries.Select(e => JsonSerializer.Serialize(e, ActionLogJson.Default.ActionLogEntry) + "\n")), Encoding.UTF8);
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // See Append.
        }
    }
}

/// <summary>The Action Logs of all Chats, one file per Chat in <c>actions</c> next to <c>chats.json</c>.</summary>
public sealed class ActionLogStore(string directory)
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, ActionLog> _open = new(StringComparer.Ordinal);

    /// <summary>The log of the Chat <paramref name="chatId"/> (empty when it has none yet); the same instance for the same id.</summary>
    public ActionLog Open(string chatId)
    {
        lock (_gate)
        {
            if (!_open.TryGetValue(chatId, out var log))
            {
                _open[chatId] = log = new ActionLog(FilePath(chatId));
            }

            return log;
        }
    }

    /// <summary>Removes the log of a deleted Chat.</summary>
    public void Delete(string chatId)
    {
        lock (_gate)
        {
            if (_open.Remove(chatId, out var log)) log.Clear();
            try
            {
                File.Delete(FilePath(chatId));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best effort, like the rest of Chat deletion.
            }
        }
    }

    private string FilePath(string chatId)
    {
        var name = new string([.. chatId.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '_')]);
        return Path.Combine(directory, "actions", (name.Length == 0 ? "_" : name) + ".jsonl");
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true)]
[JsonSerializable(typeof(ActionLogEntry))]
internal sealed partial class ActionLogJson : JsonSerializerContext;
