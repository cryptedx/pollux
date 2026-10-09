namespace Pollux.Core.Operations;

public enum TransferKind { Copy, Move }

/// <summary>What happens when a file already exists at the target.</summary>
public enum ConflictPolicy { Ask, Overwrite, Skip, Rename, OverwriteIfNewer }

public enum ConflictChoice { Overwrite, Skip, Rename, Cancel }

public sealed record ConflictDecision(ConflictChoice Choice, bool ApplyToAll);

public sealed record TransferConflict(
    string SourcePath,
    string TargetPath,
    bool IsDirectory,
    long SourceSize,
    DateTime SourceModified,
    long TargetSize,
    DateTime TargetModified);

public sealed record TransferRequest(TransferKind Kind, IReadOnlyList<string> Sources, string TargetDirectory, ConflictPolicy Policy = ConflictPolicy.Ask);

public enum TransferState { Queued, Running, Paused, Completed, Cancelled, Failed }

public sealed record TransferProgress(long TotalBytes, long DoneBytes, int TotalFiles, int DoneFiles, string CurrentFile, double BytesPerSecond)
{
    public static TransferProgress Empty { get; } = new(0, 0, 0, 0, "", 0);

    public double Fraction => TotalBytes > 0 ? (double)DoneBytes / TotalBytes : TotalFiles > 0 ? (double)DoneFiles / TotalFiles : 0;
}

/// <summary>One copy or move: its request, live progress, pause/cancel switches and the errors it ran into.</summary>
public sealed class TransferJob(TransferRequest request) : IDisposable
{
    private static int _nextId;
    private readonly object _gate = new();
    private readonly List<string> _errors = [];
    private readonly CancellationTokenSource _cancel = new();
    private TaskCompletionSource? _resume;

    public int Id { get; } = Interlocked.Increment(ref _nextId);
    public TransferRequest Request { get; } = request;
    public TransferState State { get; private set; } = TransferState.Queued;
    public TransferProgress Progress { get; private set; } = TransferProgress.Empty;
    public string? Error { get; private set; }

    public IReadOnlyList<string> Errors
    {
        get { lock (_gate) return _errors.ToList(); }
    }

    public bool IsFinished => State is TransferState.Completed or TransferState.Cancelled or TransferState.Failed;

    public CancellationToken CancellationToken => _cancel.Token;

    public event Action? ProgressChanged;
    public event Action? StateChanged;

    public string Title
    {
        get
        {
            var verb = Request.Kind == TransferKind.Copy ? "Copy" : "Move";
            var what = Request.Sources.Count == 1 ? Path.GetFileName(Request.Sources[0].TrimEnd('\\', '/')) : $"{Request.Sources.Count} items";
            return $"{verb} {what} → {Path.GetFileName(Request.TargetDirectory.TrimEnd('\\', '/'))}";
        }
    }

    public void Pause()
    {
        lock (_gate)
        {
            if (IsFinished || _resume is not null) return;
            _resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        if (State == TransferState.Running) SetState(TransferState.Paused);
    }

    public void Resume()
    {
        TaskCompletionSource? resume;
        lock (_gate)
        {
            resume = _resume;
            _resume = null;
        }

        resume?.TrySetResult();
        if (State == TransferState.Paused) SetState(TransferState.Running);
    }

    public void Cancel()
    {
        _cancel.Cancel();
        Resume();
    }

    internal async Task WaitWhilePausedAsync(CancellationToken ct)
    {
        Task? wait;
        lock (_gate) wait = _resume?.Task;
        if (wait is null) return;
        SetState(TransferState.Paused);
        await wait.WaitAsync(ct).ConfigureAwait(false);
        SetState(TransferState.Running);
    }

    internal void SetState(TransferState state, string? error = null)
    {
        if (State == state && error is null) return;
        State = state;
        if (error is not null) Error = error;
        StateChanged?.Invoke();
    }

    internal void Report(TransferProgress progress)
    {
        Progress = progress;
        ProgressChanged?.Invoke();
    }

    public void Dispose() => _cancel.Dispose();

    internal void AddError(string message)
    {
        lock (_gate) _errors.Add(message);
    }
}
