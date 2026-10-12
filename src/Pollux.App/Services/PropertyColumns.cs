using Microsoft.UI.Dispatching;
using Pollux.App.Interop;
using Pollux.Core.Files;
using Pollux.Core.Navigation;

namespace Pollux.App.Services;

/// <summary>How the property columns read a folder's files.</summary>
/// <param name="OnNetworkDrive">The folder lies on a mapped network drive (Z:\ looks local, so the caller says it): nothing is read.</param>
/// <param name="DateFormat">The <c>files.dateFormat</c> setting, for Date taken.</param>
internal readonly record struct PropertyReadOptions(bool OnNetworkDrive, string DateFormat);

/// <summary>
/// Shell property values for the Details property columns (<see cref="ShellProperty"/>). Realized rows ask with
/// <see cref="Request"/>: one background STA thread reads them, newest request first, so only what is on screen is read.
/// Sorting by a property column needs every file of the folder: <see cref="LoadAllAsync"/> reads them on a thread of
/// its own. Both stop on cancellation (a new folder, a closed Pane). The values are shared by all Panes and windows.
/// Files on network drives and cloud-only placeholders are never read: opening them can hang or start a download.
/// </summary>
internal static class PropertyColumns
{
    public static PropertyValues Values { get; } = new(20_000);

    private static string? _dateFormat;

    /// <summary>UI thread. Date taken is stored formatted: a new <c>files.dateFormat</c> drops what was read.</summary>
    public static void UseDateFormat(string format)
    {
        if (_dateFormat is not null && _dateFormat != format) Values.Clear();
        _dateFormat = format;
    }

    /// <summary>False for folders, files on network drives and UNC paths, and cloud-only placeholders.</summary>
    public static bool Readable(FileEntry entry, PropertyReadOptions options) =>
        !entry.IsDirectory && !options.OnNetworkDrive && entry.Cloud != CloudState.CloudOnly && !WinPath.IsUnc(entry.FullPath);

    private static readonly ThumbnailRequests<Job> Requests = new(512);
    private static Thread? _worker;

    private sealed record Job(FileEntry Entry, ShellProperty[] Properties, PropertyReadOptions Options, DispatcherQueue Ui, Action<FileEntry> Loaded, CancellationToken Ct);

    /// <summary>
    /// UI thread. True when every property of <paramref name="entry"/> is in <see cref="Values"/> already; otherwise
    /// they are queued and <paramref name="loaded"/> runs on the UI thread once read (unless <paramref name="ct"/> is cancelled first).
    /// </summary>
    public static bool Request(FileEntry entry, IReadOnlyList<ShellProperty> properties, PropertyReadOptions options, DispatcherQueue ui, Action<FileEntry> loaded, CancellationToken ct)
    {
        if (properties.Count == 0 || !Readable(entry, options) || Values.Missing(entry, properties).Count == 0) return true;
        Requests.Push(new Job(entry, [.. properties], options, ui, loaded, ct));
        EnsureWorker();
        return false;
    }

    /// <summary>Reads the missing properties of all <paramref name="entries"/> (for sorting); cancelled with <paramref name="ct"/>.</summary>
    public static Task LoadAllAsync(IReadOnlyList<FileEntry> entries, IReadOnlyList<ShellProperty> properties, PropertyReadOptions options, CancellationToken ct)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (properties.Count == 0)
        {
            done.SetResult();
            return done.Task;
        }

        var wanted = properties.ToArray();
        var thread = new Thread(() =>
        {
            try
            {
                foreach (var entry in entries)
                {
                    if (ct.IsCancellationRequested) break;
                    ReadMissing(entry, wanted, options);
                }

                if (ct.IsCancellationRequested) done.SetCanceled(ct);
                else done.SetResult();
            }
            catch (Exception ex)
            {
                CrashLog.Trace($"properties of {entries.Count} entries failed: {ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}");
                done.SetException(ex);
            }
        })
        { IsBackground = true, Name = "Pollux properties (sort)", Priority = ThreadPriority.BelowNormal };
        // Property handlers are apartment-threaded COM objects.
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task;
    }

    private static void EnsureWorker()
    {
        if (_worker is not null) return;
        _worker = new Thread(Run) { IsBackground = true, Name = "Pollux properties", Priority = ThreadPriority.BelowNormal };
        _worker.SetApartmentState(ApartmentState.STA);
        _worker.Start();
    }

    private static void Run()
    {
        while (Requests.Take(CancellationToken.None) is { } job)
        {
            if (job.Ct.IsCancellationRequested) continue;
            try
            {
                ReadMissing(job.Entry, job.Properties, job.Options);
            }
            catch (Exception ex)
            {
                // The thread serves every Pane: one bad file must not stop it.
                CrashLog.Trace($"properties {job.Entry.FullPath} failed: {ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}");
            }

            job.Ui.TryEnqueue(DispatcherQueuePriority.Low, () =>
            {
                if (!job.Ct.IsCancellationRequested) job.Loaded(job.Entry);
            });
        }
    }

    /// <summary>Background thread. A file whose store cannot be opened is recorded as having none, so it is not asked again.</summary>
    private static void ReadMissing(FileEntry entry, ShellProperty[] properties, PropertyReadOptions options)
    {
        if (!Readable(entry, options)) return;
        var missing = Values.Missing(entry, properties);
        if (missing.Count == 0) return;
        var read = ShellProperties.Read(entry.FullPath, missing, fastOnly: false, options.DateFormat);
        Values.Set(entry, read ?? missing.ToDictionary(p => p, _ => (PropertyValue?)null));
    }
}
