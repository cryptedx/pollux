using Pollux.Core.Navigation;
using Pollux.Core.Search;
using Pollux.Core.Settings;

namespace Pollux.App.Services;

/// <summary>Owns the Quick Search index: which roots, when to rebuild, live updates from watchers.</summary>
internal sealed class SearchService : IDisposable
{
    private readonly SearchIndex _index = new();
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly AppServices _services;
    private CancellationTokenSource? _buildCts;
    private Task _build = Task.CompletedTask;

    public SearchService(AppServices services) => _services = services;

    public string StatusText => _index.IsBuilding
        ? $"indexing… {_index.Count:N0} entries"
        : $"{_index.Count:N0} indexed";

    public IReadOnlyList<string> Roots()
    {
        var s = _services.Settings.Search;
        var roots = new List<string>(s.IndexRoots);
        if (s.IndexBookmarks) roots.AddRange(_services.Bookmarks.Flat.Select(b => b.Path).Where(Directory.Exists));
        if (s.IndexDownloads) roots.Add(Views.KnownFolders.Downloads);
        if (s.IndexDesktop) roots.Add(Views.KnownFolders.Desktop);
        if (s.IndexDocuments) roots.Add(Views.KnownFolders.Documents);
        var notes = NotesFolder(_services.Settings);
        if (Directory.Exists(notes)) roots.Add(notes);
        if (!s.IndexNetworkPaths) roots.RemoveAll(WinPath.IsUnc);

        // Drop roots contained in other roots.
        var distinct = roots.Select(WinPath.Normalize).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return distinct.Where(r => !distinct.Any(o => !ReferenceEquals(o, r) && !string.Equals(o, r, StringComparison.OrdinalIgnoreCase) && WinPath.IsSameOrUnder(r, o))).ToList();
    }

    public static string NotesFolder(PolluxSettings s) =>
        string.IsNullOrWhiteSpace(s.Notes.Folder) ? Path.Combine(Views.KnownFolders.Documents, "Pollux", "Notes") : s.Notes.Folder;

    public void Rebuild()
    {
        _buildCts?.Cancel();
        var cts = _buildCts = new CancellationTokenSource();
        var roots = Roots();
        var exclude = _services.Settings.Search.ExcludeNames.ToList();
        _build = Task.Run(() =>
        {
            Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;
            try { _index.Build(roots, exclude, cts.Token); }
            catch (OperationCanceledException) { }
        }, cts.Token);
        Watch(roots, exclude);
    }

    public async Task<IReadOnlyList<SearchHit>> FindAsync(string query, int max, CancellationToken ct)
    {
        await Task.Delay(60, ct).ConfigureAwait(false); // debounce typing
        return await Task.Run(() => _index.Find(query, max), ct).ConfigureAwait(true);
    }

    private void Watch(IReadOnlyList<string> roots, List<string> exclude)
    {
        foreach (var w in _watchers) w.Dispose();
        _watchers.Clear();
        var ex = new HashSet<string>(exclude, StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
        {
            try
            {
                var w = new FileSystemWatcher(root)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName,
                    InternalBufferSize = 64 * 1024,
                    EnableRaisingEvents = true,
                };
                w.Created += (_, e) => { if (!Excluded(e.FullPath, ex)) _index.Add(e.FullPath, Directory.Exists(e.FullPath)); };
                w.Deleted += (_, e) => _index.Remove(e.FullPath);
                w.Renamed += (_, e) =>
                {
                    _index.Remove(e.OldFullPath);
                    if (!Excluded(e.FullPath, ex)) _index.Add(e.FullPath, Directory.Exists(e.FullPath));
                };
                _watchers.Add(w);
            }
            catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException)
            {
                // Root not watchable (e.g. removed drive); periodic rebuild covers it.
            }
        }
    }

    private static bool Excluded(string path, HashSet<string> names) =>
        path.Split('\\').Any(names.Contains);

    public void Dispose()
    {
        _buildCts?.Cancel();
        foreach (var w in _watchers) w.Dispose();
    }
}
