using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;

[assembly: InternalsVisibleTo("Pollux.Copilot.Tests")]

namespace Pollux.Copilot.Chat;

/// <summary>Where a tool call is checked, in the order the CLI and Pollux reach them.</summary>
internal enum Checkpoint
{
    Hook,
    Permission,
    Tool,
}

/// <summary>
/// What the checkpoints of one Chat's <see cref="ToolApprovals"/> tell each other about a tool call. The pre-tool-use
/// hook, the CLI's permission request and a Pollux file tool itself see the same call one after another; they match it
/// by its key (<see cref="ToolApprovals"/> builds it from kind and command, paths and URLs, or from the Pollux tool and
/// its detail). Identical calls are counted, not merged: each note is taken by one later checkpoint. Thread-safe.
/// </summary>
internal sealed class ToolCallLedger
{
    private readonly ConcurrentDictionary<string, int> _seenByHook = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int[]> _approved = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, bool> _allowedForChat = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _logged = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Queue<int>> _awaiting = new(StringComparer.Ordinal);

    // ---- Hook liveness: the call was seen by the hook (matched by its arguments only) ----

    /// <summary>The hook saw a call with <paramref name="arguments"/> (JSON); it vouches for one start of that call.</summary>
    public void HookSaw(string? arguments) => Count(_seenByHook, CanonicalArguments(arguments));

    /// <summary>The CLI started a call with <paramref name="arguments"/> (JSON): true (and used up) when the hook saw it first.</summary>
    public bool StartedWasSeen(string? arguments) => Take(_seenByHook, CanonicalArguments(arguments));

    // ---- The user's yes to the Guard Rules' question, for one call ----

    // Each yes travels with its one call through the checkpoints, and passes each of them at most once: a yes given in the
    // hook passes the permission request of one call, never of two identical calls running side by side.

    /// <summary>The user said yes to the call <paramref name="key"/> once, asked at <paramref name="at"/>.</summary>
    public void Approve(string key, Checkpoint at)
    {
        lock (_approved)
        {
            if (!_approved.TryGetValue(key, out var counts)) _approved[key] = counts = new int[3];
            counts[(int)at]++;
        }
    }

    /// <summary>A yes to the call <paramref name="key"/> from before <paramref name="at"/> passes <paramref name="at"/> and moves on to it; false when there is none.</summary>
    public bool PassApproved(string key, Checkpoint at) => MoveApproved(key, at, keep: true);

    /// <summary>Uses up a yes to the call <paramref name="key"/> from before <paramref name="at"/>; false when there is none.</summary>
    public bool TakeApproved(string key, Checkpoint at) => MoveApproved(key, at, keep: false);

    private bool MoveApproved(string key, Checkpoint at, bool keep)
    {
        lock (_approved)
        {
            if (!_approved.TryGetValue(key, out var counts)) return false;

            // The furthest yes first: it belongs to the call that is furthest along.
            var from = (int)at - 1;
            while (from >= 0 && counts[from] == 0) from--;
            if (from < 0) return false;

            counts[from]--;
            if (keep) counts[(int)at]++;
            else if (counts.All(n => n == 0)) _approved.Remove(key);
            return true;
        }
    }

    // ---- Allow for this Chat (until it is closed) ----

    /// <summary>The user allowed the request <paramref name="key"/> for the rest of the Chat.</summary>
    public void AllowForChat(string key) => _allowedForChat[key] = true;

    public bool IsAllowedForChat(string key) => _allowedForChat.ContainsKey(key);

    // ---- Action Log correlation ----

    /// <summary>
    /// The hook's Action Log entry <paramref name="entryId"/> for the call <paramref name="key"/> waits for the decision
    /// of a later checkpoint (first in, first out per key); a null id (no log) is ignored.
    /// </summary>
    public void AwaitDecision(string key, int? entryId)
    {
        if (entryId is null) return;
        lock (_awaiting)
        {
            if (!_awaiting.TryGetValue(key, out var queue)) _awaiting[key] = queue = new Queue<int>();
            queue.Enqueue(entryId.Value);
        }
    }

    /// <summary>The oldest Action Log entry still waiting for a decision on the call <paramref name="key"/>; null when none.</summary>
    public int? TakeAwaiting(string key)
    {
        lock (_awaiting)
        {
            if (!_awaiting.TryGetValue(key, out var queue) || queue.Count == 0) return null;
            var id = queue.Dequeue();
            if (queue.Count == 0) _awaiting.Remove(key);
            return id;
        }
    }

    /// <summary>The call <paramref name="key"/> is in the Action Log already; the Pollux tool does not log it again.</summary>
    public void Logged(string key) => Count(_logged, key);

    /// <summary>Uses up one "already logged" note for the call <paramref name="key"/>; false when there is none.</summary>
    public bool TakeLogged(string key) => Take(_logged, key);

    /// <summary>The arguments as compact JSON with the properties of every object in name order; "" for none.</summary>
    internal static string CanonicalArguments(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return "";
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Null) return "";
            var buffer = new ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                WriteCanonical(writer, doc.RootElement);
            }

            return System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan);
        }
        catch (JsonException)
        {
            return json.Trim();
        }
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray()) WriteCanonical(writer, item);
                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }

    private static void Count(ConcurrentDictionary<string, int> counts, string key) => counts.AddOrUpdate(key, 1, (_, n) => n + 1);

    private static bool Take(ConcurrentDictionary<string, int> counts, string key)
    {
        while (counts.TryGetValue(key, out var n))
        {
            if (n <= 1 ? counts.TryRemove(new KeyValuePair<string, int>(key, n)) : counts.TryUpdate(key, n - 1, n)) return true;
        }

        return false;
    }
}
