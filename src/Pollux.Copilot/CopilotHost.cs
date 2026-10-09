namespace Pollux.Copilot;

/// <summary>A running Copilot CLI as the host sees it: started once, stopped on purpose, or gone on its own.</summary>
public interface ICopilotConnection : IAsyncDisposable
{
    /// <summary>Starts the CLI process and completes the protocol handshake.</summary>
    Task StartAsync(CancellationToken cancellationToken);

    /// <summary>Stops the CLI gracefully.</summary>
    Task StopAsync();

    /// <summary>Completes with the cause when the CLI goes away without being stopped (crash, killed, broken pipe).</summary>
    Task<string> Exited { get; }
}

public enum CopilotHostStatus
{
    /// <summary>Copilot is off (setting) or unavailable (Admin Instance): no CLI process.</summary>
    Disabled,

    /// <summary>Copilot is on but nothing used it yet: no CLI process.</summary>
    NotStarted,

    Starting,
    Running,

    /// <summary>The CLI could not be started; the next use tries again.</summary>
    Failed,

    /// <summary>The CLI went away while running; the next use starts a new one.</summary>
    Crashed,

    /// <summary>Pollux is exiting.</summary>
    Stopped,
}

/// <param name="Reason">For <see cref="CopilotHostStatus.Failed"/> and <see cref="CopilotHostStatus.Crashed"/>: the cause.</param>
public sealed record CopilotHostState(CopilotHostStatus Status, string? Reason = null);

/// <summary>
/// The one Copilot CLI of a Pollux process (spec "Process &amp; auth", ADR 0003/0005): owned by the resident process,
/// started lazily on first Copilot use, shared by all windows, stopped when Copilot is disabled or Pollux exits.
/// A crash is detected and surfaced as <see cref="CopilotHostStatus.Crashed"/>; the next use starts a fresh CLI.
/// </summary>
public sealed class CopilotHost : IAsyncDisposable
{
    private readonly Func<ICopilotConnection> _createConnection;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ICopilotConnection? _connection;
    private bool _available;
    private bool _disposed;

    /// <param name="createConnection">Creates a not yet started connection; called on each (re)start, so it can
    /// pick up the current <c>copilot.cliPath</c>.</param>
    /// <param name="available">Whether Copilot is enabled and this is not the Admin Instance.</param>
    public CopilotHost(Func<ICopilotConnection> createConnection, bool available)
    {
        _createConnection = createConnection;
        _available = available;
        State = new CopilotHostState(available ? CopilotHostStatus.NotStarted : CopilotHostStatus.Disabled);
    }

    public CopilotHostState State { get; private set; }

    /// <summary>Raised after every state change, on whichever thread caused it.</summary>
    public event Action<CopilotHostState>? StateChanged;

    /// <summary>
    /// Returns the running CLI, starting it on first use. Null when Copilot is disabled, Pollux is exiting, or the
    /// CLI could not be started (then <see cref="State"/> says why).
    /// </summary>
    public async Task<ICopilotConnection?> ConnectAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_disposed || !_available)
            {
                return null;
            }

            if (_connection is not null)
            {
                return _connection;
            }

            SetState(new CopilotHostState(CopilotHostStatus.Starting));
            var connection = _createConnection();
            try
            {
                await connection.StartAsync(cancellationToken).ConfigureAwait(false);
            }
#pragma warning disable CA1031 // Any start failure is a state the Copilot Panel shows, not a crash of Pollux.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                await connection.DisposeAsync().ConfigureAwait(false);
                SetState(new CopilotHostState(CopilotHostStatus.Failed, ex.Message));
                return null;
            }

            _connection = connection;
            SetState(new CopilotHostState(CopilotHostStatus.Running));
            _ = WatchAsync(connection);
            return connection;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Follows the "Enable Copilot" setting: off stops the CLI; on waits for the next use.</summary>
    public async Task SetAvailableAsync(bool available)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed || available == _available)
            {
                return;
            }

            _available = available;
            if (!available)
            {
                await StopConnectionAsync().ConfigureAwait(false);
            }

            SetState(new CopilotHostState(available ? CopilotHostStatus.NotStarted : CopilotHostStatus.Disabled));
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Pollux exits: stops the CLI.</summary>
    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            await StopConnectionAsync().ConfigureAwait(false);
            SetState(new CopilotHostState(CopilotHostStatus.Stopped));
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task WatchAsync(ICopilotConnection connection)
    {
        var reason = await connection.Exited.ConfigureAwait(false);
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!ReferenceEquals(_connection, connection))
            {
                return; // stopped on purpose
            }

            _connection = null;
            await connection.DisposeAsync().ConfigureAwait(false);
            SetState(new CopilotHostState(CopilotHostStatus.Crashed, reason));
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task StopConnectionAsync()
    {
        if (_connection is not { } connection)
        {
            return;
        }

        _connection = null;
        try
        {
            await connection.StopAsync().ConfigureAwait(false);
        }
#pragma warning disable CA1031 // A CLI that fails to stop cleanly is disposed (killed) anyway.
        catch (Exception)
#pragma warning restore CA1031
        {
        }

        await connection.DisposeAsync().ConfigureAwait(false);
    }

    private void SetState(CopilotHostState state)
    {
        State = state;
        StateChanged?.Invoke(state);
    }
}
