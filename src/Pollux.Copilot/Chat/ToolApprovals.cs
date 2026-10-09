using System.Buffers;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using GitHub.Copilot;
using GitHub.Copilot.Rpc;
using Pollux.Copilot.Tools;
using Pollux.Core.Copilot.Guards;

namespace Pollux.Copilot.Chat;

/// <summary>A tool request shown to the user in the approval prompt.</summary>
/// <param name="Kind">The CLI's request kind: shell, write, read, url, mcp, custom-tool, memory, ...</param>
/// <param name="Detail">What exactly would happen: full command text, path, URL or arguments.</param>
/// <param name="Diff">For file writes: the change as a unified diff.</param>
/// <param name="Reason">Why the Guard Rules ask (always-ask command, write outside the Working Folder, URL access, ...); null when the Permission Mode asks.</param>
/// <param name="Paths">The files and folders the request touches, as far as known.</param>
/// <param name="CanAllowForChat">
/// Whether the prompt offers "Allow for this Chat": only when the Permission Mode asks. A question of the Guard Rules
/// (always-ask command, Tainted Chat, write outside the Working Folder, ...) is asked every time.
/// </param>
public sealed record ToolApprovalRequest(
    string Id, string Kind, string Title, string Detail, string? Diff = null, string? Reason = null, IReadOnlyList<string>? Paths = null, bool CanAllowForChat = false);

/// <summary>The user's answer to an approval prompt.</summary>
public enum ApprovalAnswer
{
    Deny,

    /// <summary>Allow this one call.</summary>
    AllowOnce,

    /// <summary>
    /// Allow this call and the same request again for the rest of the Chat (until it is closed). Only for prompts of the
    /// Permission Mode; never overrides a block or a question of the Guard Rules.
    /// </summary>
    AllowForChat,
}

/// <summary>
/// Decides the tool requests of one Chat in three places (spec "Guard Rules"):
/// <list type="bullet">
/// <item>the pre-tool-use hook (<see cref="PreToolUseAsync"/>) runs before every tool call in every Permission Mode: in Plan
/// mode only read tools pass; a blocked request is denied, an always-ask request is asked in the Copilot Panel (Allow once /
/// Deny), anything else is left to the Permission Mode;</item>
/// <item>the permission requests of the CLI (<see cref="DecideAsync"/>, also the fallback should the hook not run): the same
/// rules, and in Manual mode every remaining request is asked (Allow once / Allow for this Chat / Deny);</item>
/// <item>the Pollux file tools themselves right before Pollux acts (<see cref="AuthorizeAsync"/>).</item>
/// </list>
/// Every tool call seen is written to the Action Log with its final decision. A prompt nobody answers is denied after
/// <see cref="DefaultAnswerTimeout"/>. The CLI's own per-Chat deny rules (<see cref="ChatGuard.CliDeniedCommands"/>,
/// <see cref="ChatGuard.CliDeniedPaths"/>) are a further net, set when the session opens. Under Allow All the CLI asks
/// nothing, so the hook is the only place the Guard Rules see a call: <see cref="ToolStarted"/> tells whether the hook saw
/// a call the CLI started (hook-liveness watchdog).
/// </summary>
/// <param name="ask">Shows the approval prompt; called on a background thread. The answer comes via <see cref="Answer(string, ApprovalAnswer)"/>.</param>
/// <param name="answered">Called once a prompt is answered (by the user, by <see cref="RejectPending"/> or by the timeout).</param>
/// <param name="guard">The Chat's Guard Rules; null applies the built-in rules with no Working Folder.</param>
/// <param name="log">The Chat's Action Log: every tool call seen here is recorded with its decision; null records nothing.</param>
/// <param name="permissionMode">The Chat's Permission Mode now (its name), given as the reason of a call the mode let through.</param>
/// <param name="readOnly">Whether the Chat is in Plan mode now (only read tools); null: never.</param>
/// <param name="answerTimeout">How long a prompt waits for the user before the call is denied; null: <see cref="DefaultAnswerTimeout"/>.</param>
public sealed class ToolApprovals(
    Action<ToolApprovalRequest> ask,
    Action<string, ApprovalAnswer>? answered = null,
    ChatGuard? guard = null,
    ActionLog? log = null,
    Func<string>? permissionMode = null,
    Func<bool>? readOnly = null,
    TimeSpan? answerTimeout = null)
    : IPolluxToolGate
{
    /// <summary>Told to the model when the user says no.</summary>
    public const string DeclinedByUser = "The user declined this tool call.";

    /// <summary>Told to the model (and logged) when nobody answered the prompt in time.</summary>
    public const string NoAnswer = "No answer to the approval prompt in time; the tool call was denied.";

    /// <summary>Told to the model (and logged) for a tool call that could change something while the Chat is in Plan mode.</summary>
    public const string PlanModeOnlyReads = "Plan mode: only read tools are available.";

    /// <summary>Logged for a tool the CLI started without the Guard Rules hook having seen the call.</summary>
    public const string HookDidNotRun = "The Guard Rules hook did not run for this tool call; the answer was stopped and the Chat switched to Manual.";

    /// <summary>How long a prompt waits for the user by default.</summary>
    public static TimeSpan DefaultAnswerTimeout { get; } = TimeSpan.FromMinutes(15);

    /// <summary>The tools Plan mode keeps: they read, or only steer the session itself.</summary>
    private static readonly HashSet<string> PlanTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "view", "read", "read_file", "show_file", "ls", "list_dir", "list_directory", "grep", "glob", "rg", "search", "find",
        "web_fetch", "web_search", "fetch", "pollux_read_panes",
        "ask_user", "task_complete", "exit_plan_mode", "report_intent", "task", "read_agent", "list_agents", "skill",
    };

    private sealed record OpenPrompt(TaskCompletionSource<ApprovalAnswer> Answer, bool CanAllowForChat);

    private readonly record struct Reply(ApprovalAnswer Answer, bool TimedOut)
    {
        public bool Allowed => Answer != ApprovalAnswer.Deny;

        public string Refusal => TimedOut ? NoAnswer : DeclinedByUser;

        public string Logged(string why) => (TimedOut ? NoAnswer + " " : Allowed ? "Allowed once. " : "Declined by the user. ") + why;
    }

    private readonly ConcurrentDictionary<string, OpenPrompt> _pending = new();
    private readonly ConcurrentDictionary<string, bool> _allowedForChat = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _approvedByHook = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _seenByHook = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _approvedFileCalls = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _loggedFileCalls = new(StringComparer.Ordinal);
    private readonly ChatGuard _guard = guard ?? new ChatGuard();
    private readonly TimeSpan _timeout = answerTimeout ?? DefaultAnswerTimeout;
    private readonly Dictionary<string, Queue<int>> _awaitingMode = new(StringComparer.Ordinal);
    private int _next;

    /// <summary>The Chat's Guard Rules.</summary>
    public ChatGuard Guard => _guard;

    /// <summary>The ids of the prompts still waiting for the user.</summary>
    public IReadOnlyCollection<string> Pending => [.. _pending.Keys];

    /// <summary>Why no tool may change anything now (Plan mode); null when tools may.</summary>
    public string? ReadOnlyReason => readOnly?.Invoke() == true ? PlanModeOnlyReads : null;

    /// <summary>Shows the prompt (under a fresh id) and waits for the user's answer: true when allowed.</summary>
    public async Task<bool> AskAsync(ToolApprovalRequest request) =>
        (await AskForAsync(request, CancellationToken.None).ConfigureAwait(false)).Allowed;

    private async Task<Reply> AskForAsync(ToolApprovalRequest request, CancellationToken cancellationToken)
    {
        var id = "t" + Interlocked.Increment(ref _next).ToString(CultureInfo.InvariantCulture);
        var answer = new TaskCompletionSource<ApprovalAnswer>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = new OpenPrompt(answer, request.CanAllowForChat);
        ask(request with { Id = id });
        try
        {
            return new Reply(await answer.Task.WaitAsync(_timeout, cancellationToken).ConfigureAwait(false), false);
        }
        catch (TimeoutException)
        {
            // Nobody answered: the prompt closes as denied (unless the answer came in just now).
            return Close(id, ApprovalAnswer.Deny) ? new Reply(ApprovalAnswer.Deny, true) : new Reply(await answer.Task.ConfigureAwait(false), false);
        }
        catch (OperationCanceledException)
        {
            Close(id, ApprovalAnswer.Deny);
            throw;
        }
    }

    /// <summary>The user answered the prompt <paramref name="id"/>; false when it is no longer open.</summary>
    public bool Answer(string id, ApprovalAnswer answer) => Close(id, answer);

    private bool Close(string id, ApprovalAnswer answer)
    {
        if (!_pending.TryRemove(id, out var prompt)) return false;
        if (answer == ApprovalAnswer.AllowForChat && !prompt.CanAllowForChat) answer = ApprovalAnswer.AllowOnce;
        if (!prompt.Answer.TrySetResult(answer)) return false;
        answered?.Invoke(id, answer);
        return true;
    }

    /// <summary>The user allowed once (<paramref name="approved"/>) or denied the prompt <paramref name="id"/>.</summary>
    public bool Answer(string id, bool approved) => Answer(id, approved ? ApprovalAnswer.AllowOnce : ApprovalAnswer.Deny);

    /// <summary>Rejects every open prompt (the answer was stopped or the Chat closed).</summary>
    public void RejectPending()
    {
        foreach (var id in _pending.Keys) Answer(id, ApprovalAnswer.Deny);
    }

    /// <summary>
    /// The pre-tool-use hook: Plan mode and the Guard Rules before every tool call, whatever the Permission Mode. Returns
    /// "deny" for a blocked call, asks for an always-ask call ("allow" or "deny" from the user's answer), and null (no
    /// opinion) for everything else so the Permission Mode decides.
    /// </summary>
    public async Task<PreToolUseHookOutput?> PreToolUseAsync(PreToolUseHookInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        Count(_seenByHook, CanonicalArguments(input.ToolArgs is { } args ? args.GetRawText() : null));
        if (FileOperation.Tools.ContainsKey(input.ToolName))
        {
            return await PreFileToolUseAsync(input).ConfigureAwait(false);
        }

        var request = GuardRequests.FromHook(input.ToolName, input.ToolArgs);
        if (ReadOnlyReason is { } plan && !PlanTools.Contains(input.ToolName))
        {
            Record(input.ToolName, request, ActionDecision.Blocked, ActionDecider.Mode, plan);
            return Deny(plan);
        }

        var verdict = _guard.Evaluate(request);
        switch (verdict.Decision)
        {
            case GuardDecision.Deny:
                Record(input.ToolName, request, ActionDecision.Blocked, ActionDecider.GuardRule, verdict.Reason);
                return Deny(Blocked(verdict.Reason));
            case GuardDecision.Ask:
                var reply = await AskForAsync(Prompt(input.ToolName, request, verdict.Reason), CancellationToken.None).ConfigureAwait(false);
                Record(input.ToolName, request, reply.Allowed ? ActionDecision.Asked : ActionDecision.Blocked, ActionDecider.User, reply.Logged(verdict.Reason));
                if (!reply.Allowed) return Deny(reply.Refusal);

                Count(_approvedByHook, Key(request));
                _guard.Proceeds(request);
                return Approved(verdict.Reason);
            default:
                _guard.Proceeds(request); // the Permission Mode may still ask; a refused read leaves the Chat Tainted (errs on the safe side)
                Await(Key(request), Record(input.ToolName, request, ActionDecision.Allowed, ActionDecider.Mode, ModeReason()));
                return null;
        }
    }

    /// <summary>
    /// The CLI started the tool <paramref name="tool"/> with <paramref name="arguments"/> (JSON): true when the Guard Rules
    /// hook saw that call first (each hook call vouches for one start). False when it did not: the Guard Rules never judged
    /// the call, which is written to the Action Log (<see cref="HookDidNotRun"/>); the Chat then stops the answer and
    /// falls back to Manual. Calls are matched by their arguments only (independent of how the CLI names the tool in the
    /// hook and in the event).
    /// </summary>
    public bool ToolStarted(string tool, string? arguments)
    {
        ArgumentNullException.ThrowIfNull(tool);
        if (Take(_seenByHook, CanonicalArguments(arguments))) return true;

        JsonElement? args = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(arguments))
            {
                using var doc = JsonDocument.Parse(arguments);
                args = doc.RootElement.Clone();
            }
        }
        catch (JsonException)
        {
        }

        Record(tool, GuardRequests.FromHook(tool, args), ActionDecision.Allowed, ActionDecider.Mode, HookDidNotRun);
        return false;
    }

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

#pragma warning disable GHCP001 // Experimental SDK API, pinned together with the CLI version (ADR 0005).
    /// <summary>
    /// A permission request of the CLI: in Plan mode anything but a read → rejected; blocked by the Guard Rules → rejected;
    /// already approved by the user in the hook → approved; a plain request of the Permission Mode allowed for this Chat →
    /// approved; otherwise asked (with the Guard Rules' reason when they are why).
    /// </summary>
    public async Task<PermissionDecision> DecideAsync(PermissionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request is PermissionRequestCustomTool custom && FileOperation.FromToolCall(custom.ToolName, custom.Args) is { } operation)
        {
            return await DecideFileToolAsync(PolluxToolCall.Of(operation)).ConfigureAwait(false);
        }

        var guarded = GuardRequests.FromPermission(request);
        var key = Key(guarded);
        if (ReadOnlyReason is { } plan && guarded.Kind is not (ToolKind.Read or ToolKind.Url))
        {
            Settle(request.Kind, guarded, TakeAwaiting(key), ActionDecision.Blocked, ActionDecider.Mode, plan);
            return PermissionDecision.Reject(plan);
        }

        var verdict = _guard.Evaluate(guarded);
        if (verdict.Decision == GuardDecision.Deny)
        {
            Settle(request.Kind, guarded, TakeAwaiting(key), ActionDecision.Blocked, ActionDecider.GuardRule, verdict.Reason);
            return PermissionDecision.Reject(Blocked(verdict.Reason));
        }

        if (Take(_approvedByHook, key))
        {
            _guard.Proceeds(guarded);
            return PermissionDecision.ApproveOnce();
        }

        var modeAsks = verdict.Decision == GuardDecision.Allow;
        if (modeAsks && _allowedForChat.ContainsKey(key))
        {
            Settle(request.Kind, guarded, TakeAwaiting(key), ActionDecision.Allowed, ActionDecider.User, "Allowed for this Chat earlier.");
            _guard.Proceeds(guarded);
            return PermissionDecision.ApproveOnce();
        }

        var prompt = Describe("", request) with
        {
            Reason = modeAsks ? null : verdict.Reason,
            Paths = guarded.Paths.Count > 0 ? guarded.Paths : null,
            CanAllowForChat = modeAsks,
        };
        var awaiting = TakeAwaiting(key);
        var why = modeAsks ? ModeReason() : verdict.Reason;
        var reply = await AskForAsync(prompt, CancellationToken.None).ConfigureAwait(false);
        Remember(key, reply);
        Settle(request.Kind, guarded, awaiting, reply.Allowed ? ActionDecision.Asked : ActionDecision.Blocked, ActionDecider.User, Logged(reply, why));
        if (!reply.Allowed) return PermissionDecision.Reject(reply.Refusal);

        _guard.Proceeds(guarded);
        return PermissionDecision.ApproveOnce();
    }
#pragma warning restore GHCP001

    // ---- Pollux file tools (copy, move, rename, recycle bin, new folder, Bookmark) ----
    // Three places see such a call: the pre-tool-use hook, the CLI's permission request (when the Permission Mode asks),
    // and the tool itself right before Pollux acts (AuthorizeAsync, which runs whatever the CLI did). Whichever asks the
    // Guard Rules' question first records the user's yes for that one call; the later places use it instead of asking again,
    // and the tool consumes it. Likewise the call is logged once: by the hook, settled by the permission request, or by
    // the tool itself when neither saw it.

    /// <summary>
    /// The Guard Rules on a Pollux file tool call right before Pollux performs it: null when it may go ahead (allowed, or
    /// the user already said yes to this call in the hook or the permission request); otherwise why not.
    /// </summary>
    public async Task<string?> AuthorizeAsync(PolluxToolCall toolCall, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(toolCall);
        var key = FileKey(toolCall);
        if (ReadOnlyReason is { } plan)
        {
            RecordFile(toolCall, ActionDecision.Blocked, ActionDecider.Mode, plan);
            return plan;
        }

        var verdict = Evaluate(toolCall);
        if (verdict.Decision == GuardDecision.Deny)
        {
            RecordFile(toolCall, ActionDecision.Blocked, ActionDecider.GuardRule, verdict.Reason);
            return Blocked(verdict.Reason);
        }

        if (verdict.Decision == GuardDecision.Allow || Take(_approvedFileCalls, key))
        {
            if (Take(_loggedFileCalls, key)) TakeAwaiting(key);
            else RecordFile(toolCall, ActionDecision.Allowed, ActionDecider.Mode, ModeReason());
            return null;
        }

        var reply = await AskForAsync(FilePrompt(toolCall, verdict.Reason, false), cancellationToken).ConfigureAwait(false);
        RecordFile(toolCall, reply.Allowed ? ActionDecision.Asked : ActionDecision.Blocked, ActionDecider.User, reply.Logged(verdict.Reason));
        return reply.Allowed ? null : reply.Refusal;
    }

    private async Task<PreToolUseHookOutput?> PreFileToolUseAsync(PreToolUseHookInput input)
    {
        // Invalid arguments: the tool refuses the call itself and tells Copilot why.
        if (FileOperation.FromToolCall(input.ToolName, input.ToolArgs) is not { } operation) return null;
        var toolCall = PolluxToolCall.Of(operation);
        var key = FileKey(toolCall);
        if (ReadOnlyReason is { } plan)
        {
            RecordFile(toolCall, ActionDecision.Blocked, ActionDecider.Mode, plan);
            return Deny(plan);
        }

        var verdict = Evaluate(toolCall);
        if (verdict.Decision == GuardDecision.Deny)
        {
            RecordFile(toolCall, ActionDecision.Blocked, ActionDecider.GuardRule, verdict.Reason);
            return Deny(Blocked(verdict.Reason));
        }

        if (verdict.Decision == GuardDecision.Allow)
        {
            Await(key, RecordFile(toolCall, ActionDecision.Allowed, ActionDecider.Mode, ModeReason()));
            Count(_loggedFileCalls, key);
            return null;
        }

        var reply = await ApproveFileCallAsync(toolCall, verdict.Reason).ConfigureAwait(false);
        if (reply is not { } asked) return Approved(verdict.Reason); // already asked for this call
        RecordFile(toolCall, asked.Allowed ? ActionDecision.Asked : ActionDecision.Blocked, ActionDecider.User, asked.Logged(verdict.Reason));
        if (!asked.Allowed) return Deny(asked.Refusal);
        Count(_loggedFileCalls, key);
        return Approved(verdict.Reason);
    }

#pragma warning disable GHCP001 // Experimental SDK API, pinned together with the CLI version (ADR 0005).
    private async Task<PermissionDecision> DecideFileToolAsync(PolluxToolCall toolCall)
    {
        var key = FileKey(toolCall);
        if (ReadOnlyReason is { } plan)
        {
            SettleFile(toolCall, key, ActionDecision.Blocked, ActionDecider.Mode, plan);
            return PermissionDecision.Reject(plan);
        }

        var verdict = Evaluate(toolCall);
        if (verdict.Decision == GuardDecision.Deny)
        {
            SettleFile(toolCall, key, ActionDecision.Blocked, ActionDecider.GuardRule, verdict.Reason);
            return PermissionDecision.Reject(Blocked(verdict.Reason));
        }

        if (verdict.Decision == GuardDecision.Ask)
        {
            var reply = await ApproveFileCallAsync(toolCall, verdict.Reason).ConfigureAwait(false);
            if (reply is not { } asked) return PermissionDecision.ApproveOnce(); // the hook asked already and logged it
            RecordFile(toolCall, asked.Allowed ? ActionDecision.Asked : ActionDecision.Blocked, ActionDecider.User, asked.Logged(verdict.Reason));
            if (!asked.Allowed) return PermissionDecision.Reject(asked.Refusal);
            Count(_loggedFileCalls, key);
            return PermissionDecision.ApproveOnce();
        }

        // The Permission Mode asks (Manual).
        if (_allowedForChat.ContainsKey(key))
        {
            SettleFile(toolCall, key, ActionDecision.Allowed, ActionDecider.User, "Allowed for this Chat earlier.");
            return PermissionDecision.ApproveOnce();
        }

        var modeReply = await AskForAsync(FilePrompt(toolCall, null, true), CancellationToken.None).ConfigureAwait(false);
        Remember(key, modeReply);
        SettleFile(toolCall, key, modeReply.Allowed ? ActionDecision.Asked : ActionDecision.Blocked, ActionDecider.User, Logged(modeReply, ModeReason()));
        return modeReply.Allowed ? PermissionDecision.ApproveOnce() : PermissionDecision.Reject(modeReply.Refusal);
    }
#pragma warning restore GHCP001

    /// <summary>
    /// Asks the Guard Rules' question once per call (never remembered for the Chat): the user's reply, or null when the
    /// user already said yes to this call (in the hook or the permission request).
    /// </summary>
    private async Task<Reply?> ApproveFileCallAsync(PolluxToolCall toolCall, string reason)
    {
        var key = FileKey(toolCall);
        if (_approvedFileCalls.ContainsKey(key)) return null;
        var reply = await AskForAsync(FilePrompt(toolCall, reason, false), CancellationToken.None).ConfigureAwait(false);
        if (reply.Allowed) Count(_approvedFileCalls, key);
        return reply;
    }

    /// <summary>The permission request settles the hook's entry for the file tool call, or logs the call itself; the tool then does not log it again.</summary>
    private void SettleFile(PolluxToolCall toolCall, string key, ActionDecision decision, ActionDecider by, string reason)
    {
        if (TakeAwaiting(key) is { } id)
        {
            log?.Update(id, e =>
            {
                e.Decision = decision;
                e.DecidedBy = by;
                e.Reason = reason;
            });
            if (decision == ActionDecision.Blocked) Take(_loggedFileCalls, key);
        }
        else
        {
            RecordFile(toolCall, decision, by, reason);
            if (decision != ActionDecision.Blocked) Count(_loggedFileCalls, key);
        }
    }

    private int? RecordFile(PolluxToolCall toolCall, ActionDecision decision, ActionDecider by, string reason) =>
        log?.Add(new ActionLogEntry
        {
            Tool = toolCall.Tool,
            Paths = [.. toolCall.Requests.SelectMany(r => r.Paths).Distinct(StringComparer.OrdinalIgnoreCase)],
            Decision = decision,
            DecidedBy = by,
            Reason = reason,
        }).Id;

    /// <summary>The strictest verdict over everything the call reads and writes, with every reason.</summary>
    private GuardVerdict Evaluate(PolluxToolCall toolCall)
    {
        var verdicts = toolCall.Requests.Select(_guard.Evaluate).ToList();
        var worst = verdicts.Count == 0 ? GuardDecision.Allow : verdicts.Max(v => v.Decision);
        if (worst == GuardDecision.Allow) return GuardVerdict.Allowed;
        return GuardVerdict.Of(worst, [.. verdicts.Where(v => v.Decision == worst).SelectMany(v => v.Reasons).Distinct(StringComparer.Ordinal)]);
    }

    private static string FileKey(PolluxToolCall toolCall) => toolCall.Tool + "\n" + toolCall.Detail;

    private static ToolApprovalRequest FilePrompt(PolluxToolCall toolCall, string? reason, bool canAllowForChat)
    {
        var paths = toolCall.Requests.SelectMany(r => r.Paths).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var kind = toolCall.Requests.Any(r => r.Kind == ToolKind.Write) ? "write" : "bookmark";
        return new("", kind, toolCall.Title, toolCall.Detail, null, reason, paths.Count > 0 ? paths : null, canAllowForChat);
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

    private string ModeReason() => permissionMode?.Invoke() is { Length: > 0 } mode ? "Permission Mode " + mode : "Permission Mode";

    private static string Blocked(string reason) => "Guard Rules: " + reason;

    private static PreToolUseHookOutput Approved(string reason) =>
        new() { PermissionDecision = "allow", PermissionDecisionReason = "Approved by the user: " + reason };

    /// <summary>How a reply to a prompt of the Permission Mode is logged (it may be "for this Chat").</summary>
    private static string Logged(Reply reply, string why) =>
        reply.Answer == ApprovalAnswer.AllowForChat ? "Allowed for this Chat. " + why : reply.Logged(why);

    private void Remember(string key, Reply reply)
    {
        if (reply.Answer == ApprovalAnswer.AllowForChat) _allowedForChat[key] = true;
    }

    /// <summary>Adds an entry to the Action Log; returns its id, or null when there is no log.</summary>
    private int? Record(string tool, ToolRequest request, ActionDecision decision, ActionDecider by, string reason) =>
        log?.Add(new ActionLogEntry
        {
            Tool = tool,
            Command = request.Kind == ToolKind.Shell ? request.Command : null,
            Paths = [.. request.Paths],
            Urls = [.. request.Urls],
            Decision = decision,
            DecidedBy = by,
            Reason = reason,
        }).Id;

    /// <summary>The permission request settles the hook's entry for the same call (the mode left the decision to it), or is logged itself when the hook did not run.</summary>
    private void Settle(string tool, ToolRequest request, int? awaiting, ActionDecision decision, ActionDecider by, string reason)
    {
        if (log is null) return;
        if (awaiting is { } id)
        {
            log.Update(id, e =>
            {
                e.Decision = decision;
                e.DecidedBy = by;
                e.Reason = reason;
            });
        }
        else
        {
            Record(tool, request, decision, by, reason);
        }
    }

    /// <summary>The hook's entry <paramref name="id"/> waits for the permission request that may follow for the same call.</summary>
    private void Await(string key, int? id)
    {
        if (id is null) return;
        lock (_awaitingMode)
        {
            if (!_awaitingMode.TryGetValue(key, out var queue)) _awaitingMode[key] = queue = new Queue<int>();
            queue.Enqueue(id.Value);
        }
    }

    private int? TakeAwaiting(string key)
    {
        lock (_awaitingMode)
        {
            if (!_awaitingMode.TryGetValue(key, out var queue) || queue.Count == 0) return null;
            var id = queue.Dequeue();
            if (queue.Count == 0) _awaitingMode.Remove(key);
            return id;
        }
    }

    private static PreToolUseHookOutput Deny(string reason) => new() { PermissionDecision = "deny", PermissionDecisionReason = reason };

    /// <summary>What makes two requests "the same" for Allow for this Chat: kind and command text, or kind, paths and URLs.</summary>
    private static string Key(ToolRequest r) => r.Kind == ToolKind.Shell
        ? "shell\n" + r.Command
        : r.Kind + "\n" + string.Join("\n", r.Paths) + "\n" + string.Join("\n", r.Urls);

    private static ToolApprovalRequest Prompt(string toolName, ToolRequest r, string reason)
    {
        var paths = r.Paths.Count > 0 ? r.Paths : null;
        return r.Kind switch
        {
            ToolKind.Shell => new("", "shell", "Run a command", r.Command ?? "", null, reason, paths),
            ToolKind.Write => new("", "write", "Change a file", string.Join("\n", r.Paths), null, reason, paths),
            ToolKind.Read => new("", "read", "Read a file", string.Join("\n", r.Paths), null, reason, paths),
            ToolKind.Url => new("", "url", "Open a URL", string.Join("\n", r.Urls), null, reason, paths),
            _ => new("", toolName, $"Use {toolName}", string.Join("\n", r.Urls), null, reason, paths),
        };
    }

    /// <summary>Turns a CLI permission request into the prompt text; shows the full command, path or URL.</summary>
    public static ToolApprovalRequest Describe(string id, PermissionRequest request) => request switch
    {
        PermissionRequestShell s => new(id, "shell", Intention(s.Intention, "Run a command"), s.FullCommandText),
        PermissionRequestWrite w => new(id, "write", Intention(w.Intention, "Change a file"), w.FileName, string.IsNullOrEmpty(w.Diff) ? null : w.Diff),
        PermissionRequestRead r => new(id, "read", Intention(r.Intention, "Read a file"), r.Path),
        PermissionRequestUrl u => new(id, "url", Intention(u.Intention, "Open a URL"), u.Url),
        PermissionRequestMcp m => new(id, "mcp", $"Use {m.ToolTitle} ({m.ServerName})", Args(m.Args)),
        PermissionRequestCustomTool c => new(id, "custom-tool", $"Use {c.ToolName}", Args(c.Args)),
        PermissionRequestHook h => new(id, "hook", $"Use {h.ToolName}", h.HookMessage ?? Args(h.ToolArgs)),
        PermissionRequestMemory mem => new(id, "memory", "Remember a fact", mem.Fact),
        _ => new(id, request.Kind, $"Allow a {request.Kind} action", ""),
    };

    private static string Intention(string? intention, string fallback) =>
        string.IsNullOrWhiteSpace(intention) ? fallback : intention;

    private static string Args(JsonElement? args) => args is { ValueKind: not JsonValueKind.Undefined } a ? a.GetRawText() : "";
}
