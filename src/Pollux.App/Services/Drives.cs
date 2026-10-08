using Microsoft.UI.Dispatching;

namespace Pollux.App.Services;

internal sealed record DriveSnapshot(string Root, string Label, DriveType Type, long Free, long Total, bool Ready);

/// <summary>
/// Drive list and capacities, gathered off the UI thread with a per-drive timeout.
/// Disconnected network drives can block DriveInfo for 20+ seconds; the UI only ever reads this cache.
/// </summary>
internal static class Drives
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(1500);
    private static DispatcherQueue? _dispatcher;
    private static DispatcherQueueTimer? _timer;
    private static int _refreshing;

    public static IReadOnlyList<DriveSnapshot> Current { get; private set; } = [];

    public static event Action? Changed;

    public static void Start(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
        Refresh();
        _timer = dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(30);
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
    }

    public static void Refresh()
    {
        if (Interlocked.Exchange(ref _refreshing, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            try
            {
                var drives = DriveInfo.GetDrives();
                var tasks = drives.Select(d => Task.Run(() => Snapshot(d))).ToArray();
                await Task.WhenAny(Task.WhenAll(tasks), Task.Delay(Timeout)).ConfigureAwait(false);
                var result = new List<DriveSnapshot>();
                for (var i = 0; i < drives.Length; i++)
                {
                    var t = tasks[i];
                    result.Add(t.IsCompletedSuccessfully ? t.Result : new DriveSnapshot(drives[i].Name, Fallback(drives[i].DriveType), drives[i].DriveType, 0, 0, false));
                }

                Current = result;
                _dispatcher?.TryEnqueue(() => Changed?.Invoke());
            }
            finally
            {
                Interlocked.Exchange(ref _refreshing, 0);
            }
        });
    }

    public static DriveSnapshot? For(string path)
    {
        var root = Path.GetPathRoot(path);
        return string.IsNullOrEmpty(root) ? null : Current.FirstOrDefault(d => string.Equals(d.Root, root, StringComparison.OrdinalIgnoreCase));
    }

    private static DriveSnapshot Snapshot(DriveInfo d)
    {
        try
        {
            if (!d.IsReady) return new DriveSnapshot(d.Name, Fallback(d.DriveType), d.DriveType, 0, 0, false);
            var label = string.IsNullOrEmpty(d.VolumeLabel) ? Fallback(d.DriveType) : d.VolumeLabel;
            return new DriveSnapshot(d.Name, label, d.DriveType, d.TotalFreeSpace, d.TotalSize, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new DriveSnapshot(d.Name, Fallback(d.DriveType), d.DriveType, 0, 0, false);
        }
    }

    private static string Fallback(DriveType t) => t switch
    {
        DriveType.Fixed => "Local Disk",
        DriveType.Network => "Network",
        DriveType.Removable => "Removable",
        DriveType.CDRom => "CD/DVD",
        _ => "Drive",
    };
}
