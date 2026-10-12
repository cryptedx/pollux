using System.Collections.Concurrent;
using static Pollux.App.Interop.PreviewHandlerInterop;

namespace Pollux.App.Services;

/// <summary>
/// The STA thread every shell preview handler is driven from (ADR 0009). It pumps messages while idle, because an
/// out-of-process handler calls back into it (the file stream, the frame). One per process; a thread stuck in a hung
/// handler is abandoned and the next request starts a fresh one.
/// </summary>
internal sealed unsafe class PreviewHandlerWorker
{
    private static readonly Lock Gate = new();
    private static PreviewHandlerWorker? _current;

    private readonly ConcurrentQueue<WorkItem> _queue = new();
    /// <summary>A Win32 auto-reset event: set when work is queued. Lives as long as the process (one worker, rarely replaced).</summary>
    private readonly nint _wake = CreateEventW(0, false, false, null);
    private volatile uint _threadId;
    private volatile bool _abandoned;

    private PreviewHandlerWorker()
    {
        var thread = new Thread(Run) { IsBackground = true, Name = "Pollux preview handlers" };
        // Preview handlers are apartment-threaded COM objects.
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    /// <summary>The live worker, started on first use.</summary>
    public static PreviewHandlerWorker Current
    {
        get
        {
            lock (Gate) return _current ??= new PreviewHandlerWorker();
        }
    }

    /// <summary>
    /// Queues <paramref name="work"/> for the STA thread. <c>Started</c> completes when it begins (so a timeout can measure
    /// its own run, not the queue); both tasks are cancelled if the worker is abandoned before it runs.
    /// </summary>
    public (Task Started, Task<T> Done) Invoke<T>(Func<T> work)
    {
        var item = new WorkItem<T>(work);
        if (_abandoned) item.Cancel();
        else
        {
            _queue.Enqueue(item);
            SetEvent(_wake);
        }

        return (item.Started.Task, item.Done.Task);
    }

    /// <summary>Cancels the outgoing COM call the thread is blocked in (CoCancelCall); any thread.</summary>
    public void CancelCurrentCall()
    {
        var hr = CoCancelCall(_threadId, 0);
        CrashLog.Trace($"preview handler call cancelled: 0x{hr:X8}");
    }

    /// <summary>Gives up on this worker (its thread is stuck): queued work is cancelled and the next caller gets a new one.</summary>
    public void Abandon()
    {
        _abandoned = true;
        lock (Gate)
        {
            if (_current == this) _current = null;
        }

        while (_queue.TryDequeue(out var item)) item.Cancel();
        SetEvent(_wake);
        CrashLog.Info("A preview handler stopped responding; its thread was abandoned.");
    }

    private void Run()
    {
        _threadId = GetCurrentThreadId();
        // Lets CancelCurrentCall break a cross-process call that hangs (a handler stuck in DoPreview).
        CoEnableCallCancellation(0);
        var wake = _wake;
        while (true)
        {
            if (_queue.TryDequeue(out var item))
            {
                if (_abandoned) item.Cancel();
                else item.Execute();
            }
            else if (_abandoned)
            {
                return;
            }
            else
            {
                MsgWaitForMultipleObjectsEx(1, &wake, INFINITE, QS_ALLINPUT, MWMO_INPUTAVAILABLE);
            }

            // Window messages of the handler's frame and COM calls coming back from prevhost.exe.
            while (PeekMessageW(out var msg, 0, 0, 0, PM_REMOVE))
            {
                TranslateMessage(msg);
                DispatchMessageW(msg);
            }
        }
    }

    private abstract class WorkItem
    {
        public abstract void Execute();
        public abstract void Cancel();
    }

    private sealed class WorkItem<T>(Func<T> work) : WorkItem
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<T> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override void Execute()
        {
            Started.TrySetResult();
            try
            {
                Done.TrySetResult(work());
            }
            catch (Exception ex)
            {
                // One bad handler must not stop the thread that serves every preview.
                CrashLog.Trace($"preview handler work failed: {ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}");
                Done.TrySetException(ex);
            }
        }

        public override void Cancel()
        {
            Started.TrySetCanceled();
            Done.TrySetCanceled();
        }
    }
}
