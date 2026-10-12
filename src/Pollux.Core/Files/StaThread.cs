namespace Pollux.Core.Files;

/// <summary>Runs blocking work on a fresh single-threaded-apartment thread, which shell namespace folders expect (on Windows; elsewhere a plain background thread).</summary>
public static class StaThread
{
    public static Task<T> Run<T>(Func<T> work, string name = "Pollux shell worker")
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Start(() =>
        {
            try
            {
                done.SetResult(work());
            }
            catch (Exception ex)
            {
                done.SetException(ex);
            }
        }, name);
        return done.Task;
    }

    private static void Start(Action work, string name)
    {
        var thread = new Thread(() => work()) { IsBackground = true, Name = name };
        if (OperatingSystem.IsWindows()) thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }
}
