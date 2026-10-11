namespace Pollux.Copilot.Tests;

/// <summary>
/// A fake Copilot CLI: a Node script speaking the SDK's JSON-RPC over stdio (the SDK launches <c>.js</c>
/// CLI paths with <c>node</c>), so the real child process + stdio path is exercised. Reports version 9.9.9-fake.
/// Without node on PATH the test is inconclusive locally but fails on CI (env <c>CI</c> set), where node must exist.
/// Chats: <c>session.create</c> echoes the id; <c>session.send</c> answers by prompt (see <see cref="Script"/>):
/// "hello" streams "Hel" + "lo"; "run a tool" asks for a shell permission and answers with the decision kind it got;
/// "think long" streams one delta and waits for <c>session.abort</c>; "hook &lt;tool&gt; &lt;json args&gt;" calls the client's
/// pre-tool-use hook (<c>hooks.invoke</c>) and answers "hook: " + the client's JSON result; "autopilot" goes idle at an Autopilot continuation
/// boundary before its answer and the final idle. Agent Mode changes (<c>session.mode.set</c>) are applied and announced
/// with <c>session.mode_changed</c>; Permission Modes are applied, except that a session whose id contains "policy" is under
/// an organization policy that disables bypass permissions: Allow All is refused (with <c>session.managed_settings_enforced</c>). a session id containing "forcedsandbox" is under an organization that requires the sandbox (<c>session.sandbox.getEnforcementStatus</c>). <c>session.getMessages</c> returns one earlier question and answer. "tool start" reports two tool starts (the second by a sub-agent). A session id containing "norules" refuses the CLI deny rules (<c>session.permissions.configure</c>). A session id containing "unreadablesettings" answers <c>session.managedSettings.get</c> with a JSON-RPC error; one containing "sandboxrefused" answers <c>session.options.update</c> (the sandbox switch) with one. Every request is appended to
/// <c>requests.jsonl</c> next to the script (<see cref="Requests"/>).
/// </summary>
internal static class FakeCopilotCli
{
    /// <param name="exitAfterConnectMs">When set, the fake CLI dies (exit code 3) this long after the handshake.</param>
    public static string Write(string directory, int protocolVersion = 3, int? exitAfterConnectMs = null)
    {
        if (!NodeOnPath())
        {
            if (Environment.GetEnvironmentVariable("CI") is { Length: > 0 })
            {
                Assert.Fail("The fake Copilot CLI needs node on PATH.");
            }

            Assert.Inconclusive("The fake Copilot CLI needs node on PATH.");
        }

        var script = Path.Combine(directory, "fake-cli.js");
        var invariant = System.Globalization.CultureInfo.InvariantCulture;
        File.WriteAllText(script, Script
            .Replace("%PROTOCOL%", protocolVersion.ToString(invariant), StringComparison.Ordinal)
            .Replace("%EXIT_AFTER_CONNECT%", (exitAfterConnectMs ?? -1).ToString(invariant), StringComparison.Ordinal));
        return script;
    }

    /// <summary>The JSON-RPC requests the fake CLI received so far, in order.</summary>
    public static IReadOnlyList<System.Text.Json.JsonElement> Requests(string directory)
    {
        var log = Path.Combine(directory, "requests.jsonl");
        if (!File.Exists(log)) return [];
        return [.. File.ReadAllLines(log).Where(l => l.Length > 0).Select(l => System.Text.Json.JsonDocument.Parse(l).RootElement.Clone())];
    }

    private static bool NodeOnPath() =>
        (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Any(dir => File.Exists(Path.Combine(dir, OperatingSystem.IsWindows() ? "node.exe" : "node")));

    private const string Script = """
        const fs = require('fs');
        const path = require('path');
        const logFile = path.join(__dirname, 'requests.jsonl');
        let buf = Buffer.alloc(0);
        let seq = 0;
        const modes = {};
        const hookCalls = {};
        const event = (sessionId, type, data) => send({ jsonrpc: '2.0', method: 'session.event', params: { sessionId,
          event: { id: '00000000-0000-4000-8000-' + String(++seq).padStart(12, '0'), timestamp: new Date().toISOString(), parentId: null, type, data } } });
        const shell = { kind: 'shell', canOfferSessionApproval: false, commands: [{ identifier: 'rm', readOnly: false }],
          fullCommandText: 'rm -rf build', hasWriteFileRedirection: false, intention: 'Clean the build folder',
          possiblePaths: ['build'], possibleUrls: [] };
        const onSend = (p) => {
          const sid = p.sessionId;
          if (p.prompt === 'hello') {
            event(sid, 'assistant.message_delta', { messageId: 'a1', deltaContent: 'Hel' });
            event(sid, 'assistant.message_delta', { messageId: 'a1', deltaContent: 'lo' });
            event(sid, 'assistant.message', { messageId: 'a1', content: 'Hello' });
            event(sid, 'session.idle', {});
          } else if (p.prompt === 'run a tool') {
            event(sid, 'permission.requested', { requestId: 'p1', permissionRequest: shell });
          } else if (p.prompt === 'autopilot') {
            event(sid, 'session.idle', { mode: 'autopilot' });
            event(sid, 'assistant.message', { messageId: 'a4', content: 'done' });
            event(sid, 'session.idle', {});
          } else if (p.prompt.startsWith('hook ')) {
            const rest = p.prompt.slice(5);
            const space = rest.indexOf(' ');
            const id = 'srv-' + (++seq);
            hookCalls[id] = sid;
            send({ jsonrpc: '2.0', id, method: 'hooks.invoke', params: { sessionId: sid, hookType: 'preToolUse',
              input: { sessionId: sid, timestamp: Date.now(), cwd: '/work', toolName: rest.slice(0, space), toolArgs: JSON.parse(rest.slice(space + 1)) } } });
          } else if (p.prompt === 'tool start') {
            event(sid, 'tool.execution_start', { toolCallId: 'c1', toolName: 'view', toolTitle: 'Read a.txt', arguments: { path: 'C:\\work\\a.txt' } });
            send({ jsonrpc: '2.0', method: 'session.event', params: { sessionId: sid, event: { id: '00000000-0000-4000-8000-0000000000b1',
              timestamp: new Date().toISOString(), parentId: null, agentId: 'sub-1', type: 'tool.execution_start',
              data: { toolCallId: 'c2', toolName: 'powershell', arguments: { command: 'dir' } } } } });
            event(sid, 'session.idle', {});
          } else if (p.prompt === 'think long') {
            event(sid, 'assistant.message_delta', { messageId: 'a2', deltaContent: 'thinking' });
          } else {
            event(sid, 'session.error', { errorType: 'query', message: 'unknown prompt' });
            event(sid, 'session.idle', {});
          }
        };
        const onPermission = (p) => {
          event(p.sessionId, 'assistant.message', { messageId: 'a3', content: 'decision: ' + (p.result && p.result.kind) });
          event(p.sessionId, 'session.idle', {});
        };
        const send = (msg) => {
          const body = Buffer.from(JSON.stringify(msg), 'utf8');
          process.stdout.write('Content-Length: ' + body.length + '\r\n\r\n');
          process.stdout.write(body);
        };
        const results = {
          'connect': { ok: true, protocolVersion: %PROTOCOL%, version: '9.9.9-fake' },
          'ping': { message: 'pong', timestamp: new Date().toISOString(), protocolVersion: %PROTOCOL% },
          'status.get': { version: '9.9.9-fake', protocolVersion: %PROTOCOL% },
          'session.send': { messageId: 'u1' },
          'session.delete': { success: true },
          'session.getMessages': { events: [
            { id: '00000000-0000-4000-8000-0000000000a1', timestamp: new Date().toISOString(), parentId: null, type: 'user.message',
              data: { content: 'earlier question', messageId: 'h1' } },
            { id: '00000000-0000-4000-8000-0000000000a2', timestamp: new Date().toISOString(), parentId: null, type: 'assistant.message',
              data: { messageId: 'h2', content: 'earlier answer' } } ] },

          'models.list': { models: [
            { id: 'gpt-5', name: 'GPT-5', capabilities: { supports: { vision: true, reasoningEffort: true }, limits: { max_context_window_tokens: 128000 } } },
            { id: 'claude-sonnet-4.5', name: 'Claude Sonnet 4.5', capabilities: { supports: { vision: true, reasoningEffort: false }, limits: { max_context_window_tokens: 200000 } } },
            { id: 'blocked-model', name: 'Blocked', policy: { state: 'disabled' }, capabilities: { supports: { vision: false, reasoningEffort: false }, limits: { max_context_window_tokens: 1000 } } },
          ] },
          'account.getQuota': { quotaSnapshots: { premium_interactions: { isUnlimitedEntitlement: false, entitlementRequests: 300, usedRequests: 158,
            remainingPercentage: 47.3, overage: 0, overageAllowedWithExhaustedQuota: false, usageAllowedWithExhaustedQuota: false, resetDate: '2026-11-01T00:00:00Z' } } },
          'session.permissions.handlePendingPermissionRequest': { success: true },
          'session.permissions.configure': { success: true },
        };
        process.stdin.on('data', (chunk) => {
          buf = Buffer.concat([buf, chunk]);
          for (;;) {
            const sep = buf.indexOf('\r\n\r\n');
            if (sep < 0) return;
            const len = parseInt(/Content-Length: (\d+)/i.exec(buf.slice(0, sep).toString())[1], 10);
            if (buf.length < sep + 4 + len) return;
            const msg = JSON.parse(buf.slice(sep + 4, sep + 4 + len).toString('utf8'));
            buf = buf.slice(sep + 4 + len);
            if (msg.method === undefined && hookCalls[msg.id]) {
              const sid = hookCalls[msg.id];
              delete hookCalls[msg.id];
              event(sid, 'assistant.message', { messageId: 'hk' + msg.id, content: 'hook: ' + JSON.stringify(msg.result !== undefined ? msg.result : { error: msg.error }) });
              event(sid, 'session.idle', {});
              continue;
            }
            if (msg.method === 'connect' && %EXIT_AFTER_CONNECT% >= 0) setTimeout(() => process.exit(3), %EXIT_AFTER_CONNECT%);
            fs.appendFileSync(logFile, JSON.stringify(msg) + '\n');
            const p = (msg.params && (Array.isArray(msg.params) ? msg.params[0] : msg.params)) || {};
            let result = results[msg.method] ?? {};
            if (msg.method === 'session.create' || msg.method === 'session.resume')
              result = { sessionId: p.sessionId, workspacePath: null, capabilities: null };
            let after = null;
            if (msg.method === 'session.mode.set') {
              const previous = modes[p.sessionId] || 'interactive';
              modes[p.sessionId] = p.mode;
              result = { modeApplied: true, modelChanged: false };
              after = () => event(p.sessionId, 'session.mode_changed', { previousMode: previous, newMode: p.mode });
            }
            const policy = String(p.sessionId).includes('policy');
            if (msg.method === 'session.permissions.setMode') {
              const refused = policy && p.mode === 'allow-all';
              result = { mode: refused ? 'manual' : p.mode, success: !refused };
              if (refused) after = () => event(p.sessionId, 'session.managed_settings_enforced', { action: 'bypass_permissions_blocked',
                escalation: 'allow_all', failClosed: false, message: 'Allow All is disabled by your enterprise.', setting: 'permissions.disableBypassPermissionsMode' });
            }
            if (msg.method === 'session.permissions.configure') result = { success: !String(p.sessionId).includes('norules') };
            if (msg.method === 'sandbox.getHostSupport') result = { supported: true, capabilities: [] };
            if (msg.method === 'session.sandbox.getEnforcementStatus') result = { required: String(p.sessionId).includes('forcedsandbox'), blocked: false };
            if (msg.method === 'session.managedSettings.get')
              result = policy
                ? { bypassPermissionsDisabled: true, failClosed: false, deviceManaged: false, serverManaged: true, managedKeys: ['permissions'],
                    source: 'server', settings: { permissions: { disableBypassPermissionsMode: 'disable' } } }
                : { bypassPermissionsDisabled: false, failClosed: false, deviceManaged: false, serverManaged: false, managedKeys: [], source: 'none' };
            const fails = (msg.method === 'session.managedSettings.get' && String(p.sessionId).includes('unreadablesettings'))
              || (msg.method === 'session.options.update' && String(p.sessionId).includes('sandboxrefused'));
            if (fails && msg.id !== undefined) { send({ jsonrpc: '2.0', id: msg.id, error: { code: -32603, message: msg.method + ' failed' } }); continue; }
            if (msg.id !== undefined) send({ jsonrpc: '2.0', id: msg.id, result });
            if (after) setTimeout(after, 5);
            if (msg.method === 'session.send') setTimeout(() => onSend(p), 10);
            if (msg.method === 'session.permissions.handlePendingPermissionRequest') setTimeout(() => onPermission(p), 10);
            if (msg.method === 'session.abort') event(p.sessionId, 'session.idle', { aborted: true });
          }
        });
        process.stdin.on('end', () => process.exit(0));
        """;
}
