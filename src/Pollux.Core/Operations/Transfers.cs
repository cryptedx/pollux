namespace Pollux.Core.Operations;

/// <summary>Runs transfer jobs one after another (parallel copies to one disk are slower, not faster) and keeps recent ones for display.</summary>
public sealed class Transfers
{
    private readonly object _gate = new();
    private readonly List<(TransferJob Job, Func<TransferConflict, Task<ConflictDecision>> Ask)> _pending = [];
    private readonly List<TransferJob> _jobs = [];
    private bool _running;

    /// <summary>Raised (on a worker thread) when a job is added, changes state or makes progress.</summary>
    public event Action<TransferJob>? Changed;

    public IReadOnlyList<TransferJob> Jobs
    {
        get { lock (_gate) return _jobs.ToList(); }
    }

    public bool IsBusy
    {
        get { lock (_gate) return _running || _pending.Count > 0; }
    }

    public TransferJob Enqueue(TransferRequest request, Func<TransferConflict, Task<ConflictDecision>> ask)
    {
        var job = new TransferJob(request);
        job.ProgressChanged += () => Changed?.Invoke(job);
        job.StateChanged += () => Changed?.Invoke(job);
        lock (_gate)
        {
            _jobs.Add(job);
            _pending.Add((job, ask));
            if (!_running)
            {
                _running = true;
                _ = Task.Run(PumpAsync);
            }
        }

        Changed?.Invoke(job);
        return job;
    }

    /// <summary>Removes finished jobs from the list.</summary>
    public void ClearFinished()
    {
        lock (_gate)
        {
            foreach (var j in _jobs.Where(j => j.IsFinished).ToList())
            {
                _jobs.Remove(j);
                j.Dispose();
            }
        }
    }

    private async Task PumpAsync()
    {
        while (true)
        {
            (TransferJob Job, Func<TransferConflict, Task<ConflictDecision>> Ask) next;
            lock (_gate)
            {
                if (_pending.Count == 0)
                {
                    _running = false;
                    return;
                }

                next = _pending[0];
                _pending.RemoveAt(0);
            }

            if (next.Job.CancellationToken.IsCancellationRequested)
            {
                next.Job.SetState(TransferState.Cancelled);
                continue;
            }

            await TransferRunner.RunAsync(next.Job, next.Ask, CancellationToken.None).ConfigureAwait(false);
        }
    }
}
