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

    /// <summary>How a checkpoint judged a tool call, before it becomes that checkpoint's own answer.</summary>
    private enum Ruling
    {
        /// <summary>Left to the Permission Mode (only the hook leaves a call to it).</summary>
        NoOpinion,
        Allow,
        Deny,
    }

    /// <param name="Reason">Allow: the Guard Rules' reason the user said yes to (shown by the hook); Deny: told to the model.</param>
    private readonly record struct Judgement(Ruling Ruling, string Reason)
    {
        public static Judgement NoOpinion => new(Ruling.NoOpinion, "");

        public static Judgement Allow(string reason = "") => new(Ruling.Allow, reason);

        public static Judgement Deny(string reason) => new(Ruling.Deny, reason);
    }

    private const string AllowedEarlier = "Allowed for this Chat earlier.";

    private readonly ConcurrentDictionary<string, OpenPrompt> _pending = new();
    private readonly ToolCallLedger _ledger = new();
    private readonly ChatGuard _guard = guard ?? new ChatGuard();
    private readonly TimeSpan _timeout = answerTimeout ?? DefaultAnswerTimeout;
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

    // ---- The checkpoints. Each climbs the same ladder: Plan mode and the Guard Rules' block (Screen), then its own rungs
    // (a yes the user gave earlier, Allow for this Chat, asking the user), writing the call to the Action Log on the way.
    // Each judges into a Judgement that it turns into its own answer. ----

    /// <summary>
    /// The pre-tool-use hook: Plan mode and the Guard Rules before every tool call, whatever the Permission Mode. Returns
    /// "deny" for a blocked call, asks for an always-ask call ("allow" or "deny" from the user's answer), and null (no
    /// opinion) for everything else so the Permission Mode decides.
    /// </summary>
    public async Task<PreToolUseHookOutput?> PreToolUseAsync(PreToolUseHookInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        _ledger.HookSaw(input.ToolArgs is { } args ? args.GetRawText() : null);
        var judgement = FileOperation.Tools.ContainsKey(input.ToolName)
            ? await PreFileToolUseAsync(input).ConfigureAwait(false)
            : await PreOtherToolUseAsync(input).ConfigureAwait(false);
        return judgement.Ruling switch
        {
            Ruling.Allow => new() { PermissionDecision = "allow", PermissionDecisionReason = "Approved by the user: " + judgement.Reason },
            Ruling.Deny => Deny(judgement.Reason),
            _ => null,
        };
    }

    private async Task<Judgement> PreOtherToolUseAsync(PreToolUseHookInput input)
    {
        var request = GuardRequests.FromHook(input.ToolName, input.ToolArgs);
        if (Screen(!PlanTools.Contains(input.ToolName), () => _guard.Evaluate(request),
                (decision, by, reason) => Record(Entry(input.ToolName, request), decision, by, reason), out var verdict) is { } denied)
        {
            return denied;
        }

        var key = Key(request);
        if (verdict.Decision == GuardDecision.Ask)
        {
            var reply = await AskForAsync(Prompt(input.ToolName, request, verdict.Reason), CancellationToken.None).ConfigureAwait(false);
            Record(Entry(input.ToolName, request), reply.Allowed ? ActionDecision.Asked : ActionDecision.Blocked, ActionDecider.User, reply.Logged(verdict.Reason));
            if (!reply.Allowed) return Judgement.Deny(reply.Refusal);

            _ledger.Approve(key, Checkpoint.Hook);
            _guard.Proceeds(request);
            return Judgement.Allow(verdict.Reason);
        }

        _guard.Proceeds(request); // the Permission Mode may still ask; a refused read leaves the Chat Tainted (errs on the safe side)
        _ledger.AwaitDecision(key, Record(Entry(input.ToolName, request), ActionDecision.Allowed, ActionDecider.Mode, ModeReason()));
        return Judgement.NoOpinion;
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
        if (_ledger.StartedWasSeen(arguments)) return true;

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

        Record(Entry(tool, GuardRequests.FromHook(tool, args)), ActionDecision.Allowed, ActionDecider.Mode, HookDidNotRun);
        return false;
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
        var judgement = request is PermissionRequestCustomTool custom && FileOperation.FromToolCall(custom.ToolName, custom.Args) is { } operation
            ? await DecideFileToolAsync(PolluxToolCall.Of(operation)).ConfigureAwait(false)
            : await DecideOtherAsync(request).ConfigureAwait(false);
        return judgement.Ruling == Ruling.Allow ? PermissionDecision.ApproveOnce() : PermissionDecision.Reject(judgement.Reason);
    }
#pragma warning restore GHCP001

    private async Task<Judgement> DecideOtherAsync(PermissionRequest request)
    {
        var guarded = GuardRequests.FromPermission(request);
        var key = Key(guarded);
        if (Screen(guarded.Kind is not (ToolKind.Read or ToolKind.Url), () => _guard.Evaluate(guarded),
                (decision, by, reason) => Settle(_ledger.TakeAwaiting(key), Entry(request.Kind, guarded), decision, by, reason), out var verdict) is { } denied)
        {
            return denied;
        }

        if (_ledger.TakeApproved(key, Checkpoint.Permission))
        {
            _guard.Proceeds(guarded);
            return Judgement.Allow();
        }

        var modeAsks = verdict.Decision == GuardDecision.Allow;
        if (modeAsks && _ledger.IsAllowedForChat(key))
        {
            Settle(_ledger.TakeAwaiting(key), Entry(request.Kind, guarded), ActionDecision.Allowed, ActionDecider.User, AllowedEarlier);
            _guard.Proceeds(guarded);
            return Judgement.Allow();
        }

        // Asked by the Permission Mode (Manual), or by the Guard Rules when the hook did not ask already.
        var prompt = Describe("", request) with
        {
            Reason = modeAsks ? null : verdict.Reason,
            Paths = guarded.Paths.Count > 0 ? guarded.Paths : null,
            CanAllowForChat = modeAsks,
        };
        var awaiting = _ledger.TakeAwaiting(key);
        var why = modeAsks ? ModeReason() : verdict.Reason;
        var reply = await AskForAsync(prompt, CancellationToken.None).ConfigureAwait(false);
        Remember(key, reply);
        Settle(awaiting, Entry(request.Kind, guarded), reply.Allowed ? ActionDecision.Asked : ActionDecision.Blocked, ActionDecider.User, Logged(reply, why));
        if (!reply.Allowed) return Judgement.Deny(reply.Refusal);

        _guard.Proceeds(guarded);
        return Judgement.Allow();
    }

    // ---- Pollux file tools (copy, move, rename, recycle bin, new folder, Bookmark) ----
    // Three places see such a call: the pre-tool-use hook, the CLI's permission request (when the Permission Mode asks),
    // and the tool itself right before Pollux acts (AuthorizeAsync, which runs whatever the CLI did). Whichever asks the
    // Guard Rules' question first records the user's yes for that one call; each later place lets it pass once instead of
    // asking again, and the tool consumes it. So two identical calls side by side need two yeses, one each. Likewise the
    // call is logged once: by the hook, settled by the permission request, or by the tool itself when neither saw it.

    /// <summary>
    /// The Guard Rules on a Pollux file tool call right before Pollux performs it: null when it may go ahead (allowed, or
    /// the user already said yes to this call in the hook or the permission request); otherwise why not.
    /// </summary>
    public async Task<string?> AuthorizeAsync(PolluxToolCall toolCall, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(toolCall);
        var judgement = await AuthorizeFileToolAsync(toolCall, cancellationToken).ConfigureAwait(false);
        return judgement.Ruling == Ruling.Allow ? null : judgement.Reason;
    }

    private async Task<Judgement> AuthorizeFileToolAsync(PolluxToolCall toolCall, CancellationToken cancellationToken)
    {
        var key = Key(toolCall);
        if (Screen(true, () => Evaluate(toolCall), (decision, by, reason) => Record(Entry(toolCall), decision, by, reason), out var verdict) is { } denied)
        {
            return denied;
        }

        if (verdict.Decision == GuardDecision.Allow || _ledger.TakeApproved(key, Checkpoint.Tool))
        {
            if (_ledger.TakeLogged(key)) _ledger.TakeAwaiting(key);
            else Record(Entry(toolCall), ActionDecision.Allowed, ActionDecider.Mode, ModeReason());
            return Judgement.Allow();
        }

        var reply = await AskForAsync(FilePrompt(toolCall, verdict.Reason, false), cancellationToken).ConfigureAwait(false);
        Record(Entry(toolCall), reply.Allowed ? ActionDecision.Asked : ActionDecision.Blocked, ActionDecider.User, reply.Logged(verdict.Reason));
        return reply.Allowed ? Judgement.Allow() : Judgement.Deny(reply.Refusal);
    }

    private async Task<Judgement> PreFileToolUseAsync(PreToolUseHookInput input)
    {
        // Invalid arguments: the tool refuses the call itself and tells Copilot why.
        if (FileOperation.FromToolCall(input.ToolName, input.ToolArgs) is not { } operation) return Judgement.NoOpinion;
        var toolCall = PolluxToolCall.Of(operation);
        var key = Key(toolCall);
        if (Screen(true, () => Evaluate(toolCall), (decision, by, reason) => Record(Entry(toolCall), decision, by, reason), out var verdict) is { } denied)
        {
            return denied;
        }

        if (verdict.Decision == GuardDecision.Allow)
        {
            _ledger.AwaitDecision(key, Record(Entry(toolCall), ActionDecision.Allowed, ActionDecider.Mode, ModeReason()));
            _ledger.Logged(key);
            return Judgement.NoOpinion;
        }

        return await AskGuardRulesOnceAsync(toolCall, key, verdict.Reason, Checkpoint.Hook).ConfigureAwait(false);
    }

    private async Task<Judgement> DecideFileToolAsync(PolluxToolCall toolCall)
    {
        var key = Key(toolCall);
        if (Screen(true, () => Evaluate(toolCall), (decision, by, reason) => SettleFile(toolCall, key, decision, by, reason), out var verdict) is { } denied)
        {
            return denied;
        }

        if (verdict.Decision == GuardDecision.Ask) return await AskGuardRulesOnceAsync(toolCall, key, verdict.Reason, Checkpoint.Permission).ConfigureAwait(false);

        // The Permission Mode asks (Manual). Unlike DecideOtherAsync, the hook's entry is taken only once the user answered.
        if (_ledger.IsAllowedForChat(key))
        {
            SettleFile(toolCall, key, ActionDecision.Allowed, ActionDecider.User, AllowedEarlier);
            return Judgement.Allow();
        }

        var reply = await AskForAsync(FilePrompt(toolCall, null, true), CancellationToken.None).ConfigureAwait(false);
        Remember(key, reply);
        SettleFile(toolCall, key, reply.Allowed ? ActionDecision.Asked : ActionDecision.Blocked, ActionDecider.User, Logged(reply, ModeReason()));
        return reply.Allowed ? Judgement.Allow() : Judgement.Deny(reply.Refusal);
    }

    /// <summary>
    /// The Guard Rules' question on a Pollux file tool call, from the hook or the permission request: asked once per call
    /// (never remembered for the Chat) and logged; the user's yes waits for the tool (<see cref="AuthorizeAsync"/>).
    /// Allowed without asking when the user already said yes to this call at an earlier checkpoint (a yes passes
    /// <paramref name="at"/> for one call only).
    /// </summary>
    private async Task<Judgement> AskGuardRulesOnceAsync(PolluxToolCall toolCall, string key, string reason, Checkpoint at)
    {
        if (_ledger.PassApproved(key, at)) return Judgement.Allow(reason); // already asked for this call, and logged
        var reply = await AskForAsync(FilePrompt(toolCall, reason, false), CancellationToken.None).ConfigureAwait(false);
        if (reply.Allowed) _ledger.Approve(key, at);
        Record(Entry(toolCall), reply.Allowed ? ActionDecision.Asked : ActionDecision.Blocked, ActionDecider.User, reply.Logged(reason));
        if (!reply.Allowed) return Judgement.Deny(reply.Refusal);
        _ledger.Logged(key);
        return Judgement.Allow(reason);
    }

    /// <summary>The permission request settles the hook's entry for the file tool call, or logs the call itself; the tool then does not log it again.</summary>
    private void SettleFile(PolluxToolCall toolCall, string key, ActionDecision decision, ActionDecider by, string reason)
    {
        if (Settle(_ledger.TakeAwaiting(key), Entry(toolCall), decision, by, reason))
        {
            if (decision == ActionDecision.Blocked) _ledger.TakeLogged(key);
        }
        else if (decision != ActionDecision.Blocked)
        {
            _ledger.Logged(key);
        }
    }

    /// <summary>The strictest verdict over everything the call reads and writes, with every reason.</summary>
    private GuardVerdict Evaluate(PolluxToolCall toolCall)
    {
        var verdicts = toolCall.Requests.Select(_guard.Evaluate).ToList();
        var worst = verdicts.Count == 0 ? GuardDecision.Allow : verdicts.Max(v => v.Decision);
        if (worst == GuardDecision.Allow) return GuardVerdict.Allowed;
        return GuardVerdict.Of(worst, [.. verdicts.Where(v => v.Decision == worst).SelectMany(v => v.Reasons).Distinct(StringComparer.Ordinal)]);
    }

    private static ToolApprovalRequest FilePrompt(PolluxToolCall toolCall, string? reason, bool canAllowForChat)
    {
        var paths = toolCall.Requests.SelectMany(r => r.Paths).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var kind = toolCall.Requests.Any(r => r.Kind == ToolKind.Write) ? "write" : "bookmark";
        return new("", kind, toolCall.Title, toolCall.Detail, null, reason, paths.Count > 0 ? paths : null, canAllowForChat);
    }

    // ---- The shared rungs ----

    /// <summary>
    /// The first rungs at every checkpoint: Plan mode (when the call <paramref name="changes"/> something), then a block
    /// of the Guard Rules. Either denies, written to the Action Log through <paramref name="log"/>. Null when neither does;
    /// <paramref name="verdict"/> is then the Guard Rules' verdict (Ask or Allow) for the checkpoint's own rungs.
    /// </summary>
    private Judgement? Screen(bool changes, Func<GuardVerdict> evaluate, Action<ActionDecision, ActionDecider, string> log, out GuardVerdict verdict)
    {
        verdict = GuardVerdict.Allowed;
        if (ReadOnlyReason is { } plan && changes)
        {
            log(ActionDecision.Blocked, ActionDecider.Mode, plan);
            return Judgement.Deny(plan);
        }

        verdict = evaluate();
        if (verdict.Decision != GuardDecision.Deny) return null;
        log(ActionDecision.Blocked, ActionDecider.GuardRule, verdict.Reason);
        return Judgement.Deny(Blocked(verdict.Reason));
    }

    private string ModeReason() => permissionMode?.Invoke() is { Length: > 0 } mode ? "Permission Mode " + mode : "Permission Mode";

    private static string Blocked(string reason) => "Guard Rules: " + reason;

    /// <summary>How a reply to a prompt of the Permission Mode is logged (it may be "for this Chat").</summary>
    private static string Logged(Reply reply, string why) =>
        reply.Answer == ApprovalAnswer.AllowForChat ? "Allowed for this Chat. " + why : reply.Logged(why);

    private void Remember(string key, Reply reply)
    {
        if (reply.Answer == ApprovalAnswer.AllowForChat) _ledger.AllowForChat(key);
    }

    /// <summary>The Action Log entry for a tool call, still without its decision.</summary>
    private static ActionLogEntry Entry(string tool, ToolRequest request) => new()
    {
        Tool = tool,
        Command = request.Kind == ToolKind.Shell ? request.Command : null,
        Paths = [.. request.Paths],
        Urls = [.. request.Urls],
    };

    /// <summary>The Action Log entry for a Pollux file tool call, still without its decision.</summary>
    private static ActionLogEntry Entry(PolluxToolCall toolCall) => new()
    {
        Tool = toolCall.Tool,
        Paths = [.. toolCall.Requests.SelectMany(r => r.Paths).Distinct(StringComparer.OrdinalIgnoreCase)],
    };

    /// <summary>Adds <paramref name="entry"/> to the Action Log with its decision; returns its id, or null when there is no log.</summary>
    private int? Record(ActionLogEntry entry, ActionDecision decision, ActionDecider by, string reason)
    {
        if (log is null) return null;
        entry.Decision = decision;
        entry.DecidedBy = by;
        entry.Reason = reason;
        return log.Add(entry).Id;
    }

    /// <summary>
    /// A later checkpoint settles the hook's entry <paramref name="awaiting"/> for the same call (the mode left the
    /// decision to it), or logs <paramref name="entry"/> itself when the hook logged none: true when it settled.
    /// </summary>
    private bool Settle(int? awaiting, ActionLogEntry entry, ActionDecision decision, ActionDecider by, string reason)
    {
        if (awaiting is not { } id)
        {
            Record(entry, decision, by, reason);
            return false;
        }

        log?.Update(id, e =>
        {
            e.Decision = decision;
            e.DecidedBy = by;
            e.Reason = reason;
        });
        return true;
    }

    private static PreToolUseHookOutput Deny(string reason) => new() { PermissionDecision = "deny", PermissionDecisionReason = reason };

    /// <summary>What makes two requests "the same" for Allow for this Chat: kind and command text, or kind, paths and URLs.</summary>
    private static string Key(ToolRequest r) => r.Kind == ToolKind.Shell
        ? "shell\n" + r.Command
        : r.Kind + "\n" + string.Join("\n", r.Paths) + "\n" + string.Join("\n", r.Urls);

    /// <summary>
    /// What makes two Pollux file tool calls "the same": the tool and its detail. Never equal to a key of a
    /// <see cref="ToolRequest"/> (those start with the request kind, these with a pollux_ tool name).
    /// </summary>
    private static string Key(PolluxToolCall toolCall) => toolCall.Tool + "\n" + toolCall.Detail;

    private static ToolApprovalRequest Prompt(string toolName, ToolRequest r, string reason)
    {
        var paths = r.Paths.Count > 0 ? r.Paths : null;
        return r.Kind switch
        {
            ToolKind.Shell => new("", "shell", "Run a command", r.Command ?? "", null, reason, paths),
            ToolKind.Write => new("", "write", "Change a file", string.Join("\n", r.Paths), null, reason, paths),
            ToolKind.Read when r.Navigates => new("", "read", "Open a folder in Pollux", string.Join("\n", r.Paths), null, reason, paths),
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
