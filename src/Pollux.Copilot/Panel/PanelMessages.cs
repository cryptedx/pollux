using System.Text;
using System.Text.Json;
using Pollux.Copilot.Chat;
using Pollux.Copilot.Connection;
using Pollux.Copilot.Context;
using Pollux.Core.Settings;
using Pollux.Copilot.Models;

namespace Pollux.Copilot.Panel;

/// <summary>What the Copilot Panel header shows besides the mode switchers (those follow <see cref="PanelMessages.Modes"/>): Working Folder and the Tainted marker with what made the Chat Tainted.</summary>
public sealed record PanelHeader(string? WorkingFolder, bool Sandbox, bool Tainted, string? TaintedBy = null)
{
    public static PanelHeader Placeholder { get; } = new(null, false, false);
}

/// <summary>The slot the Copilot Panel shows instead of the Chat while Copilot is not ready (connection and sign-in states, ticket 05).</summary>
public sealed record PanelStateView(string Kind, string Title, string Message, CopilotPanelAction? Action = null)
{
    /// <summary>No state view: the Chat is shown.</summary>
    public static PanelStateView None { get; } = new("", "", "");
}

/// <summary>Messages sent from the Copilot Panel page to Pollux.</summary>
public abstract record PanelEvent
{
    /// <summary>The page loaded and can receive messages.</summary>
    public sealed record Ready : PanelEvent;

    /// <summary>The user asked to close the Copilot Panel (its close button).</summary>
    public sealed record Close : PanelEvent;

    /// <summary>The user clicked a link in rendered Markdown; <paramref name="Url"/> is always http(s) and must be opened by the shell, never in the page.</summary>
    public sealed record OpenLink(string Url) : PanelEvent;

    /// <summary>The user pressed the button of a state view.</summary>
    public sealed record Action(CopilotPanelAction Value) : PanelEvent;

    /// <summary>The user sent a message to the Chat (never empty).</summary>
    public sealed record Send(string Text, IReadOnlyList<string> ChipIds) : PanelEvent;

    /// <summary>The user typed @<paramref name="Query"/> in the composer and wants matching Panes, Tabs and files offered.</summary>
    public sealed record Mention(string Query) : PanelEvent;

    /// <summary>Entries were dropped on the page; their paths travel as additional WebView2 objects, not in the message.</summary>
    public sealed record Drop : PanelEvent;

    /// <summary>The page wants the current default chips (it got focus).</summary>
    public sealed record ContextRequest : PanelEvent;

    /// <summary>The user stopped the running answer.</summary>
    public sealed record Cancel : PanelEvent;

    /// <summary>The user answered the tool approval prompt <paramref name="Id"/>: Allow, Allow for this Chat or Deny.</summary>
    public sealed record Approve(string Id, ApprovalAnswer Answer) : PanelEvent;

    /// <summary>The Copilot Panel wants the open Chat's Action Log (it opened the log view, or a tool call changed it).</summary>
    public sealed record ActionLogRequest : PanelEvent;

    /// <summary>The user wants the active Pane's folder as the Chat's Working Folder.</summary>
    public sealed record UseActiveFolder : PanelEvent;

    /// <summary>The user wants to pick the Chat's Working Folder in a folder dialog.</summary>
    public sealed record ChooseWorkingFolder : PanelEvent;

    /// <summary>The user cleared the Tainted mark of the Chat (after confirming in the page).</summary>
    public sealed record ClearTainted : PanelEvent;

    /// <summary>The user switched the Chat's Agent Mode.</summary>
    public sealed record SetAgentMode(CopilotAgentMode Mode) : PanelEvent;

    /// <summary>The user switched the Chat's sandbox on or off.</summary>
    public sealed record SetSandbox(bool Enabled) : PanelEvent;

    /// <summary>The user switched the Chat's Permission Mode.</summary>
    public sealed record SetPermissionMode(CopilotPermissionMode Mode) : PanelEvent;

    /// <summary>The user wants an empty Chat.</summary>
    public sealed record NewChat : PanelEvent;

    /// <summary>The user picked the Chat <paramref name="Id"/> from the list.</summary>
    public sealed record OpenChat(string Id) : PanelEvent;

    /// <summary>The user deleted the Chat <paramref name="Id"/> from the list.</summary>
    public sealed record DeleteChat(string Id) : PanelEvent;

    /// <summary>The user picked a model for the next message; <paramref name="Id"/> is null for the CLI's default model.</summary>
    public sealed record SelectModel(string? Id) : PanelEvent;
}

/// <summary>
/// The JSON bridge between Pollux and the Copilot Panel page (ADR 0004): Pollux builds the messages it sends and parses
/// the ones it receives. Reflection-free, so it works under Native AOT.
/// </summary>
public static class PanelMessages
{
    public static string Theme(bool dark) => Build("theme", w => w.WriteBoolean("dark", dark));

    public static string Header(PanelHeader header) => Build("header", w =>
    {
        if (header.WorkingFolder is null) w.WriteNull("workingFolder"); else w.WriteString("workingFolder", header.WorkingFolder);
        w.WriteBoolean("sandbox", header.Sandbox);
        w.WriteBoolean("tainted", header.Tainted);
        if (header.TaintedBy is null) w.WriteNull("taintedBy"); else w.WriteString("taintedBy", header.TaintedBy);
    });

    /// <summary>
    /// The Chat's Agent Mode and Permission Mode for the header switchers, with every mode the switchers offer; a
    /// Permission Mode blocked by the organization's policy carries the reason (the page disables it).
    /// </summary>
    public static string Modes(ChatModes modes, ModePolicy policy) => Build("modes", w =>
    {
        w.WriteString("agentMode", ModeId(modes.Agent));
        w.WriteString("permissionMode", ModeId(modes.Permission));
        w.WriteStartArray("agentModes");
        foreach (var m in Enum.GetValues<CopilotAgentMode>())
        {
            w.WriteStartObject();
            w.WriteString("id", ModeId(m));
            w.WriteString("label", ChatModes.Label(m));
            w.WriteNull("blocked");
            w.WriteEndObject();
        }

        w.WriteEndArray();
        w.WriteStartArray("permissionModes");
        foreach (var m in Enum.GetValues<CopilotPermissionMode>())
        {
            w.WriteStartObject();
            w.WriteString("id", ModeId(m));
            w.WriteString("label", ChatModes.Label(m));
            if (policy.BlockedReason(m) is { } reason) w.WriteString("blocked", reason); else w.WriteNull("blocked");
            w.WriteEndObject();
        }

        w.WriteEndArray();
    });

    /// <summary>The Chat's sandbox toggle: whether the CLI's sandbox is on and, when the toggle cannot be switched, why (organization policy, unsupported build).</summary>
    public static string Sandbox(SandboxState state) => Build("sandbox", w =>
    {
        w.WriteBoolean("enabled", state.Enabled);
        if (state.LockedReason is { } reason) w.WriteString("locked", reason); else w.WriteNull("locked");
    });

    /// <summary>Shows a state view in place of the Chat; <see cref="PanelStateView.None"/> brings the Chat back.</summary>
    public static string State(PanelStateView view) => Build("state", w =>
    {
        w.WriteString("kind", view.Kind);
        w.WriteString("title", view.Title);
        w.WriteString("message", view.Message);
        if (view.Action is { } a) w.WriteString("action", ActionName(a)); else w.WriteNull("action");
        if (view.Action is { } b) w.WriteString("actionLabel", ActionLabel(b)); else w.WriteNull("actionLabel");
    });

    /// <summary>Renders <paramref name="markdown"/> as an assistant message.</summary>
    public static string Markdown(string markdown) => Build("markdown", w => w.WriteString("text", markdown));

    /// <summary>Shows the user's message in the Chat.</summary>
    public static string UserMessage(string id, string text, IReadOnlyList<ContextChip>? chips = null) => Build("user", w =>
    {
        w.WriteString("id", id);
        w.WriteString("text", text);
        WriteChips(w, chips ?? []);
    });

    /// <summary>The default chips of the next message (location and selection of the active Pane); they replace the earlier defaults.</summary>
    public static string Context(IReadOnlyList<ContextChip> chips) => Build("context", w => WriteChips(w, chips));

    /// <summary>The answer to a mention query: chips the user can pick.</summary>
    public static string Mentions(string query, IReadOnlyList<ContextChip> chips) => Build("mentions", w =>
    {
        w.WriteString("query", query);
        WriteChips(w, chips);
    });

    /// <summary>Chips added to the next message from outside the page (entries dropped from a Pane).</summary>
    public static string ChipsAdded(IReadOnlyList<ContextChip> chips) => Build("chipsAdded", w => WriteChips(w, chips));

    private static void WriteChips(Utf8JsonWriter w, IReadOnlyList<ContextChip> chips)
    {
        w.WriteStartArray("chips");
        foreach (var c in chips)
        {
            w.WriteStartObject();
            w.WriteString("id", c.Id);
            w.WriteString("kind", ContextChip.KindName(c.Kind));
            w.WriteString("label", c.Label);
            w.WriteString("title", string.Join('\n', c.Paths));
            w.WriteEndObject();
        }

        w.WriteEndArray();
    }

    /// <summary>Appends a streamed piece to the answer <paramref name="id"/>.</summary>
    public static string AnswerDelta(string id, string text) => Build("answerDelta", w =>
    {
        w.WriteString("id", id);
        w.WriteString("text", text);
    });

    /// <summary>The complete answer <paramref name="id"/> as Markdown; replaces what was streamed.</summary>
    public static string Answer(string id, string markdown) => Build("answer", w =>
    {
        w.WriteString("id", id);
        w.WriteString("text", markdown);
    });

    /// <summary>A tool line in the Chat; <paramref name="status"/> is running, done or failed.</summary>
    public static string Tool(string id, string? name, string status, string? error = null) => Build("tool", w =>
    {
        w.WriteString("id", id);
        if (name is null) w.WriteNull("name"); else w.WriteString("name", name);
        w.WriteString("status", status);
        if (error is null) w.WriteNull("error"); else w.WriteString("error", error);
    });

    /// <summary>Whether Copilot is answering: the page shows Stop instead of Send.</summary>
    public static string Busy(bool busy) => Build("busy", w => w.WriteBoolean("busy", busy));

    /// <summary>An error of the Chat, shown inline.</summary>
    public static string ChatError(string message) => Build("chatError", w => w.WriteString("message", message));

    /// <summary>A warning about the Chat's safety that stays visible (e.g. the Guard Rules hook did not run).</summary>
    public static string Warning(string message) => Build("warning", w => w.WriteString("message", message));

    /// <summary>Empties the Chat view (a new, resumed or deleted Chat follows).</summary>
    public static string Clear() => Build("clear", _ => { });

    /// <summary>The Chat list (global, most recent first) and the id of the open Chat ("" when it is a new one).</summary>
    public static string Chats(IEnumerable<ChatRecord> chats, string? currentId) => Build("chats", w =>
    {
        w.WriteString("current", currentId ?? "");
        w.WriteStartArray("chats");
        foreach (var c in chats)
        {
            w.WriteStartObject();
            w.WriteString("id", c.Id);
            w.WriteString("title", c.Title);
            w.WriteString("modified", c.Modified);
            if (c.WorkingFolder is null) w.WriteNull("workingFolder"); else w.WriteString("workingFolder", c.WorkingFolder);
            w.WriteEndObject();
        }

        w.WriteEndArray();
    });

    /// <summary>The Action Log of the open Chat: every tool call, oldest first.</summary>
    public static string ActionLog(IEnumerable<ActionLogEntry> entries) => Build("actionLog", w =>
    {
        w.WriteStartArray("entries");
        foreach (var e in entries)
        {
            w.WriteStartObject();
            w.WriteNumber("id", e.Id);
            w.WriteString("time", e.Time);
            w.WriteString("tool", e.Tool);
            if (e.Command is null) w.WriteNull("command"); else w.WriteString("command", e.Command);
            w.WriteStartArray("paths");
            foreach (var p in e.Paths) w.WriteStringValue(p);
            w.WriteEndArray();
            w.WriteStartArray("urls");
            foreach (var u in e.Urls) w.WriteStringValue(u);
            w.WriteEndArray();
            w.WriteString("decision", e.Decision == ActionDecision.Allowed ? "allowed" : e.Decision == ActionDecision.Asked ? "asked" : "blocked");
            w.WriteString("decidedBy", e.DecidedBy == ActionDecider.Mode ? "mode" : e.DecidedBy == ActionDecider.GuardRule ? "guardRule" : "user");
            w.WriteString("reason", e.Reason);
            w.WriteEndObject();
        }

        w.WriteEndArray();
    });

    /// <summary>Asks the user to approve a tool request.</summary>
    public static string Approval(ToolApprovalRequest request) => Build("approval", w =>
    {
        w.WriteString("id", request.Id);
        w.WriteString("kind", request.Kind);
        w.WriteString("title", request.Title);
        w.WriteString("detail", request.Detail);
        if (request.Diff is null) w.WriteNull("diff"); else w.WriteString("diff", request.Diff);
        if (request.Reason is null) w.WriteNull("reason"); else w.WriteString("reason", request.Reason);
        w.WriteStartArray("paths");
        foreach (var path in request.Paths ?? []) w.WriteStringValue(path);
        w.WriteEndArray();
        w.WriteBoolean("allowForChat", request.CanAllowForChat);
    });

    /// <summary>The prompt <paramref name="id"/> was answered (by the user or by stopping).</summary>
    public static string ApprovalDone(string id, bool approved) => ApprovalDone(id, approved ? ApprovalAnswer.AllowOnce : ApprovalAnswer.Deny);

    /// <summary>The prompt <paramref name="id"/> was answered (by the user or by stopping) with <paramref name="answer"/>.</summary>
    public static string ApprovalDone(string id, ApprovalAnswer answer) => Build("approvalDone", w =>
    {
        w.WriteString("id", id);
        w.WriteBoolean("approved", answer != ApprovalAnswer.Deny);
        w.WriteString("answer", ModeId(answer));
    });

    /// <summary>The models the picker offers.</summary>
    public static string Models(IReadOnlyList<ChatModel> models) => Build("models", w =>
    {
        w.WriteStartArray("models");
        foreach (var m in models)
        {
            w.WriteStartObject();
            w.WriteString("id", m.Id);
            w.WriteString("name", m.Name);
            w.WriteEndObject();
        }

        w.WriteEndArray();
    });

    /// <summary>The model the next message uses; null is the CLI's default model.</summary>
    public static string Model(string? id) => Build("model", w =>
    {
        if (id is null) w.WriteNull("id"); else w.WriteString("id", id);
    });

    /// <summary>The remaining premium requests; null hides the quota.</summary>
    public static string Quota(CopilotQuota? quota) => Build("quota", w =>
    {
        if (quota is null) { w.WriteNull("text"); w.WriteNull("level"); return; }
        w.WriteString("text", quota.Text);
        w.WriteString("level", quota.Level.ToString().ToLowerInvariant());
    });

    /// <summary>Parses a message from the page; null when it is malformed or unknown (the page is not trusted).</summary>
    public static PanelEvent? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            if (!doc.RootElement.TryGetProperty("type", out var t) || t.ValueKind != JsonValueKind.String) return null;
            switch (t.GetString())
            {
                case "ready": return new PanelEvent.Ready();
                case "close": return new PanelEvent.Close();
                case "openLink":
                    if (doc.RootElement.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String
                        && Uri.TryCreate(u.GetString(), UriKind.Absolute, out var uri)
                        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
                        return new PanelEvent.OpenLink(uri.AbsoluteUri);
                    return null;
                case "action":
                    if (doc.RootElement.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                    {
                        foreach (var a in Enum.GetValues<CopilotPanelAction>())
                            if (a != CopilotPanelAction.None && ActionName(a) == id.GetString()) return new PanelEvent.Action(a);
                    }

                    return null;
                case "send":
                    if (doc.RootElement.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String
                        && !string.IsNullOrWhiteSpace(text.GetString()))
                    {
                        var ids = new List<string>();
                        if (doc.RootElement.TryGetProperty("chips", out var chips) && chips.ValueKind == JsonValueKind.Array)
                            foreach (var c in chips.EnumerateArray())
                                if (c.ValueKind == JsonValueKind.String && c.GetString() is { Length: > 0 } cid) ids.Add(cid);
                        return new PanelEvent.Send(text.GetString()!, ids);
                    }

                    return null;
                case "mention":
                    if (doc.RootElement.TryGetProperty("query", out var q) && q.ValueKind == JsonValueKind.String)
                        return new PanelEvent.Mention(q.GetString()!);
                    return null;
                case "drop": return new PanelEvent.Drop();
                case "contextRequest": return new PanelEvent.ContextRequest();
                case "cancel": return new PanelEvent.Cancel();
                case "actionLog": return new PanelEvent.ActionLogRequest();
                case "approve":
                    if (doc.RootElement.TryGetProperty("id", out var aid) && aid.ValueKind == JsonValueKind.String
                        && doc.RootElement.TryGetProperty("approved", out var ok) && ok.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    {
                        var forChat = doc.RootElement.TryGetProperty("forChat", out var fc) && fc.ValueKind == JsonValueKind.True;
                        return new PanelEvent.Approve(aid.GetString()!,
                            !ok.GetBoolean() ? ApprovalAnswer.Deny : forChat ? ApprovalAnswer.AllowForChat : ApprovalAnswer.AllowOnce);
                    }

                    return null;
                case "useActiveFolder": return new PanelEvent.UseActiveFolder();
                case "chooseWorkingFolder": return new PanelEvent.ChooseWorkingFolder();
                case "clearTainted": return new PanelEvent.ClearTainted();
                case "setAgentMode":
                    return Mode<CopilotAgentMode>(doc.RootElement) is { } agent ? new PanelEvent.SetAgentMode(agent) : null;
                case "setPermissionMode":
                    return Mode<CopilotPermissionMode>(doc.RootElement) is { } permission ? new PanelEvent.SetPermissionMode(permission) : null;
                case "setSandbox":
                    return doc.RootElement.TryGetProperty("enabled", out var sb) && sb.ValueKind is JsonValueKind.True or JsonValueKind.False
                        ? new PanelEvent.SetSandbox(sb.GetBoolean()) : null;
                case "newChat": return new PanelEvent.NewChat();
                case "openChat":
                    return ChatId(doc.RootElement) is { } open ? new PanelEvent.OpenChat(open) : null;
                case "deleteChat":
                    return ChatId(doc.RootElement) is { } del ? new PanelEvent.DeleteChat(del) : null;

                case "selectModel":
                    if (!doc.RootElement.TryGetProperty("id", out var mid)) return null;
                    if (mid.ValueKind == JsonValueKind.Null) return new PanelEvent.SelectModel(null);
                    if (mid.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(mid.GetString()))
                        return new PanelEvent.SelectModel(mid.GetString()!);
                    return null;
                default: return null;
            }
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The page's id of a mode: the enum name in camelCase (interactive, allowAll).</summary>
    private static string ModeId<T>(T mode) where T : struct, Enum
    {
        var name = mode.ToString();
        return char.ToLowerInvariant(name[0]) + name[1..];
    }

    /// <summary>A mode named by its id; only the ids <see cref="ModeId"/> produces are accepted (no numbers, no unknown names).</summary>
    private static T? Mode<T>(JsonElement root) where T : struct, Enum
    {
        if (!root.TryGetProperty("mode", out var m) || m.ValueKind != JsonValueKind.String) return null;
        foreach (var mode in Enum.GetValues<T>())
            if (ModeId(mode) == m.GetString()) return mode;
        return null;
    }

    private static string? ChatId(JsonElement root) =>
        root.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(id.GetString()) ? id.GetString() : null;

    private static string ActionName(CopilotPanelAction a) => a switch
    {
        CopilotPanelAction.SignIn => "signIn",
        CopilotPanelAction.Retry => "retry",
        CopilotPanelAction.Recheck => "recheck",
        CopilotPanelAction.OpenSettings => "openSettings",
        _ => "",
    };

    private static string ActionLabel(CopilotPanelAction a) => a switch
    {
        CopilotPanelAction.SignIn => "Sign in",
        CopilotPanelAction.Retry => "Try again",
        CopilotPanelAction.Recheck => "Check again",
        CopilotPanelAction.OpenSettings => "Open settings",
        _ => "",
    };

    private static string Build(string type, Action<Utf8JsonWriter> fields)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("type", type);
            fields(w);
            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(ms.ToArray());
    }
}
