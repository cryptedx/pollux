using GitHub.Copilot;
using GitHub.Copilot.Rpc;
using Pollux.Copilot.Chat;
using Pollux.Copilot.Connection;
using Pollux.Copilot.Models;
using Pollux.Copilot.Tools;

namespace Pollux.Copilot;

/// <summary>
/// The real Copilot CLI behind <see cref="CopilotHost"/>: an SDK <see cref="CopilotClient"/> talking to the CLI child
/// process over stdio. The SDK exposes no exit event, so a periodic ping notices when the CLI is gone.
/// </summary>
public sealed class CopilotCliConnection : ICopilotConnection, ICopilotAccountSource, ICopilotChatSource, ICopilotCatalog
{
    private static readonly TimeSpan PingTimeout = TimeSpan.FromSeconds(30);

    private readonly TimeSpan _healthCheckInterval;
    private readonly CancellationTokenSource _stopping = new();
    private readonly TaskCompletionSource<string> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <param name="healthCheckInterval">How often the CLI is pinged to detect a crash; default 5 s.</param>
    /// <param name="environment">Full environment of the CLI process (proxy and CA settings, see <see cref="CopilotCliEnvironment"/>); null inherits.</param>
    public CopilotCliConnection(CopilotCli cli, TimeSpan? healthCheckInterval = null, IReadOnlyDictionary<string, string>? environment = null)
    {
        Cli = cli;
        _healthCheckInterval = healthCheckInterval ?? TimeSpan.FromSeconds(5);
        Client = new CopilotClient(new CopilotClientOptions
        {
            // Always explicit, so a stray COPILOT_CLI_PATH cannot swap the CLI; throws for a missing CLI on start.
            Connection = cli is CopilotCli.Missing ? null : RuntimeConnection.ForStdio(CopilotCliProbe.PathOf(cli)),
            LogLevel = CopilotLogLevel.Error,
            // Telemetry (OpenTelemetry export) stays null and OnGitHubTelemetry unset on purpose (ADR 0005).
            Environment = environment ?? CopilotCliEnvironment.Build(null, null, CopilotCliEnvironment.Current()),
        });
    }

    public CopilotCli Cli { get; }

    /// <summary>The SDK client for Chats; valid while the host reports Running.</summary>
    public CopilotClient Client { get; }

    public Task<string> Exited => _exited.Task;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (Cli is CopilotCli.Missing missing)
        {
            throw new InvalidOperationException(missing.Reason);
        }

        await Client.StartAsync(cancellationToken).ConfigureAwait(false);
        _ = WatchAsync(_stopping.Token);
    }

    /// <summary>Asks the CLI who is signed in (credentials of <c>copilot login</c> or <c>gh auth</c>) and whether quota is left.</summary>
    public async Task<CopilotAccountStatus> GetAccountStatusAsync(CancellationToken cancellationToken)
    {
        var auth = await Client.GetAuthStatusAsync(cancellationToken).ConfigureAwait(false);
        if (!auth.IsAuthenticated)
        {
            return new CopilotAccountStatus(false, auth.Login, auth.StatusMessage);
        }

        return new CopilotAccountStatus(true, auth.Login, null, await QuotaExhaustedAsync(cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// Opens a Chat (creating or resuming the SDK session with the Chat's own id): streamed answers, the baseline system
    /// message, the CLI's built-in tools, and every tool request decided by <paramref name="approvals"/>. The Chat's Agent Mode
    /// and Permission Mode are applied by <see cref="CopilotChat"/> once the session is open (an organization policy may refuse one).
    /// </summary>
    public async Task<IChatSession> OpenChatAsync(ChatRecord chat, bool existing, ToolApprovals approvals, IPolluxActions actions, CancellationToken cancellationToken)
    {
        var session = existing
            ? await Client.ResumeSessionAsync(chat.Id, ChatSessionConfig.Resume(chat, approvals, actions), cancellationToken).ConfigureAwait(false)
            : await Client.CreateSessionAsync(ChatSessionConfig.Create(chat, approvals, actions), cancellationToken).ConfigureAwait(false);
        try
        {
            await ApplyDenyRulesAsync(session, approvals.Guard, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Fail closed: a Chat whose Guard Rules could not be set up in the CLI is not opened.
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }

#pragma warning disable GHCP001 // Experimental SDK API, pinned together with the CLI version (ADR 0005).
        return new CliChatSession(session, HostSandboxSupportAsync);
#pragma warning restore GHCP001
    }

    /// <summary>
    /// First Guard Rules layer, independent of the pre-tool-use hook: the blocked commands and the protected secrets as the
    /// session's own CLI deny rules (documented to win even over Allow All). Rules are per-session runtime state, so they
    /// are set again whenever a Chat is opened or resumed; a CLI that does not take them fails the opening (fail closed).
    /// </summary>
    private static async Task ApplyDenyRulesAsync(CopilotSession session, ChatGuard guard, CancellationToken cancellationToken)
    {
#pragma warning disable GHCP001 // Experimental SDK API, pinned together with the CLI version (ADR 0005).
        var rules = new PermissionRulesSet
        {
            Denied =
            [
                .. guard.CliDeniedCommands.Select(c => new PermissionRule { Kind = "shell", Argument = c }),
                .. guard.CliDeniedPaths.Select(r => new PermissionRule { Kind = r.Kind, Argument = r.Argument }),
            ],
        };
        var result = await session.Rpc.Permissions.ConfigureAsync(rules: rules, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            throw new InvalidOperationException("The Copilot CLI did not accept the Guard Rules' deny rules; the Chat is not opened.");
        }
#pragma warning restore GHCP001
    }

#pragma warning disable GHCP001 // Experimental SDK API, pinned together with the CLI version (ADR 0005).
    /// <summary>Whether this Windows build can run the CLI's local sandbox, and if not, why.</summary>
    private async Task<(bool Supported, string? Reason)> HostSandboxSupportAsync(CancellationToken cancellationToken)
    {
        var support = await Client.Rpc.Sandbox.GetHostSupportAsync(cancellationToken).ConfigureAwait(false);
        return (support.Supported, support.Reason);
    }
#pragma warning restore GHCP001

    public Task DeleteChatAsync(string chatId, CancellationToken cancellationToken) => Client.DeleteSessionAsync(chatId, cancellationToken);

    /// <summary>The models the signed-in account may use, minus those an organization policy disabled.</summary>
    public async Task<IReadOnlyList<ChatModel>> ListModelsAsync(CancellationToken cancellationToken)
    {
        var models = await Client.ListModelsAsync(cancellationToken).ConfigureAwait(false);
        return [.. models
            .Where(m => !string.Equals(m.Policy?.State, "disabled", StringComparison.OrdinalIgnoreCase))
            .Select(m => new ChatModel(m.Id, string.IsNullOrWhiteSpace(m.Name) ? m.Id : m.Name))];
    }

    /// <summary>The premium-request quota of the account.</summary>
    public async Task<CopilotQuota?> GetQuotaAsync(CancellationToken cancellationToken)
    {
#pragma warning disable GHCP001 // Experimental SDK API, pinned together with the CLI version (ADR 0005).
        var quota = await Client.Rpc.Account.GetQuotaAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!quota.QuotaSnapshots.TryGetValue("premium_interactions", out var p)) return null;
        return new CopilotQuota(p.IsUnlimitedEntitlement, (int)p.EntitlementRequests, (int)p.UsedRequests, p.RemainingPercentage,
            p.ResetDate, p.UsageAllowedWithExhaustedQuota);
#pragma warning restore GHCP001
    }

    private async Task<bool> QuotaExhaustedAsync(CancellationToken cancellationToken)
    {
        try
        {
            return (await GetQuotaAsync(cancellationToken).ConfigureAwait(false))?.Exhausted ?? false;
        }
#pragma warning disable CA1031 // The quota is a hint; failing to read it must not block sign-in.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            return false;
        }
    }

    public async Task StopAsync()
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        await Client.StopAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (!_stopping.IsCancellationRequested)
        {
            await _stopping.CancelAsync().ConfigureAwait(false);
        }

        await Client.DisposeAsync().ConfigureAwait(false);
        _stopping.Dispose();
    }

    private async Task WatchAsync(CancellationToken stopping)
    {
        try
        {
            while (true)
            {
                await Task.Delay(_healthCheckInterval, stopping).ConfigureAwait(false);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stopping);
                timeout.CancelAfter(PingTimeout);
                await Client.PingAsync(cancellationToken: timeout.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            // Stopped on purpose.
        }
#pragma warning disable CA1031 // Any ping failure means the CLI is gone; the host surfaces the cause.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            if (!stopping.IsCancellationRequested)
            {
                _exited.TrySetResult($"The Copilot CLI stopped unexpectedly: {ex.Message}");
            }
        }
    }
}
