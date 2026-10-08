using Pollux.Core.Files;
using Pollux.Core.Navigation;

namespace Pollux.Core.Search;

public sealed record SearchHit(string Name, string Path, bool IsDirectory, int Score);

/// <summary>
/// In-memory index of all files and folders under the Index Roots, for Quick Search.
/// Built in the background; kept fresh by the caller (watchers) through <see cref="Add"/>/<see cref="Remove"/>.
/// </summary>
public sealed class SearchIndex
{
    private readonly object _gate = new();
    private List<IndexEntry> _entries = [];

    private sealed record IndexEntry(string Name, string Path, bool IsDirectory);

    public int Count { get { lock (_gate) return _entries.Count; } }
    public bool IsBuilding { get; private set; }
    public DateTime? BuiltAt { get; private set; }

    public void Build(IEnumerable<string> roots, ICollection<string> excludeNames, CancellationToken cancellationToken)
    {
        IsBuilding = true;
        var exclude = new HashSet<string>(excludeNames, StringComparer.OrdinalIgnoreCase);
        var result = new List<IndexEntry>(100_000);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var root in roots)
            {
                if (!seen.Add(root) || !Directory.Exists(root)) continue;
                Crawl(root, exclude, result, cancellationToken);
            }

            lock (_gate)
            {
                _entries = result;
            }

            BuiltAt = DateTime.Now;
        }
        finally
        {
            IsBuilding = false;
        }
    }

    private static void Crawl(string root, HashSet<string> exclude, List<IndexEntry> into, CancellationToken ct)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = stack.Pop();
            IEnumerable<FileEntry> entries;
            try
            {
                entries = DirectoryReader.Enumerate(dir).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var e in entries)
            {
                if (exclude.Contains(e.Name)) continue;
                into.Add(new IndexEntry(e.Name, e.FullPath, e.IsDirectory));
                // Do not follow junctions/symlinks: avoids cycles and duplicate trees.
                if (e.IsDirectory && !e.Attributes.HasFlag(FileAttributes.ReparsePoint)) stack.Push(e.FullPath);
            }
        }
    }

    // Copy-on-write: Find works on a snapshot that writers never mutate.
    public void Add(string path, bool isDirectory)
    {
        lock (_gate)
        {
            if (_entries.Exists(e => string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase))) return;
            _entries = [.. _entries, new IndexEntry(System.IO.Path.GetFileName(path), path, isDirectory)];
        }
    }

    public void Remove(string path)
    {
        lock (_gate)
        {
            if (!_entries.Exists(e => WinPath.IsSameOrUnder(e.Path, path))) return;
            _entries = _entries.Where(e => !WinPath.IsSameOrUnder(e.Path, path)).ToList();
        }
    }

    /// <summary>Ranks: exact name > name prefix > word initials/substring; folders and shorter paths win ties.</summary>
    public IReadOnlyList<SearchHit> Find(string query, int max)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];
        var terms = query.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var nameTerm = terms[^1];
        var pathTerms = terms[..^1];
        List<IndexEntry> snapshot;
        lock (_gate) snapshot = _entries;

        var hits = new List<SearchHit>();
        foreach (var e in snapshot)
        {
            var m = FilterMatcher.Match(e.Name, nameTerm);
            if (!m.IsMatch) continue;
            var pathOk = true;
            foreach (var t in pathTerms)
            {
                if (e.Path.IndexOf(t, StringComparison.OrdinalIgnoreCase) < 0) { pathOk = false; break; }
            }

            if (!pathOk) continue;
            var score = 0;
            if (e.Name.Equals(nameTerm, StringComparison.OrdinalIgnoreCase)) score += 1000;
            else if (e.Name.StartsWith(nameTerm, StringComparison.OrdinalIgnoreCase)) score += 500;
            else if (m.Ranges.Count == 1) score += 200 - Math.Min(100, m.Ranges[0].Start);
            else score += 100;
            if (e.IsDirectory) score += 30;
            score -= Math.Min(150, e.Path.Length / 2);
            hits.Add(new SearchHit(e.Name, e.Path, e.IsDirectory, score));
        }

        return hits.OrderByDescending(h => h.Score).Take(max).ToList();
    }
}
