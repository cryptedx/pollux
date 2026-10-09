namespace Pollux.Copilot.Models;

/// <summary>A model a Chat can use (from the CLI's model list).</summary>
public sealed record ChatModel(string Id, string Name);

/// <summary>What the CLI knows about the account: usable models and the premium-request quota. Implemented by the real connection.</summary>
public interface ICopilotCatalog
{
    /// <summary>The models the signed-in account may use (models disabled by policy are left out).</summary>
    Task<IReadOnlyList<ChatModel>> ListModelsAsync(CancellationToken cancellationToken);

    /// <summary>The premium-request quota; null when the CLI cannot tell.</summary>
    Task<CopilotQuota?> GetQuotaAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The latest known quota of this Pollux process, shared by the Panels and the status bar. Refreshing asks the CLI;
/// a failed refresh keeps the last value. Refreshes may overlap and complete out of order: only the most recently
/// started one may update the value (a generation counter, also bumped by <see cref="Clear"/>).
/// </summary>
public sealed class CopilotQuotaTracker(Func<CancellationToken, Task<CopilotQuota?>> fetch)
{
    private readonly Lock _gate = new();
    private CopilotQuota? _latest;
    private long _generation;

    /// <summary>Raised (on a background thread) when the quota changed.</summary>
    public event Action<CopilotQuota?>? Changed;

    public CopilotQuota? Latest { get { lock (_gate) return _latest; } }

    /// <summary>Asks the CLI for the quota; returns the latest known value.</summary>
    public async Task<CopilotQuota?> RefreshAsync(CancellationToken cancellationToken)
    {
        long generation;
        lock (_gate) generation = ++_generation;

        CopilotQuota? fresh;
        try
        {
            fresh = await fetch(cancellationToken).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // The quota is a hint; a CLI that cannot tell keeps the last value.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            return Latest;
        }

        if (fresh is null) return Latest;
        lock (_gate)
        {
            if (generation != _generation) return _latest; // A newer refresh (or Clear) superseded this one.
            if (_latest == fresh) return fresh;
            _latest = fresh;
        }

        Changed?.Invoke(fresh);
        return fresh;
    }

    /// <summary>Forgets the quota (Copilot was disabled or the account changed).</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _generation++;
            if (_latest is null) return;
            _latest = null;
        }

        Changed?.Invoke(null);
    }
}
