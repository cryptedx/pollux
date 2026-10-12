using System.Text.Json;
using System.Text.Json.Serialization;
using Pollux.Core.Navigation;
using Pollux.Core.Settings;

namespace Pollux.Core.Files;

/// <summary>What a folder remembers. A null field follows the inherited entry or the global default.</summary>
public sealed class FolderViewEntry
{
    public string Path { get; set; } = "";
    [JsonConverter(typeof(JsonStringEnumConverter<ViewMode>))]
    public ViewMode? Mode { get; set; }
    [JsonConverter(typeof(JsonStringEnumConverter<SortColumn>))]
    public SortColumn? SortBy { get; set; }
    public bool? SortDescending { get; set; }
    [JsonConverter(typeof(JsonStringEnumConverter<GroupBy>))]
    public GroupBy? GroupBy { get; set; }
    /// <summary>Opaque column layout (widths, order); reserved for the Details columns.</summary>
    public string? Columns { get; set; }
    /// <summary>Subfolders without an entry of their own use this entry.</summary>
    public bool ApplyToSubfolders { get; set; }
    /// <summary>Recency counter for the LRU bound; higher is newer.</summary>
    public long Used { get; set; }

    public FolderViewEntry Clone() => (FolderViewEntry)MemberwiseClone();
}

/// <summary>The view settings the global defaults stand for (files.sortBy, files.sortDescending, files.groupBy and the default view mode).</summary>
public sealed record FolderViewDefaults(ViewMode Mode, SortColumn SortBy, bool SortDescending, GroupBy GroupBy);

/// <summary>The view a folder is shown with; <see cref="Source"/> is the path of the entry that supplied the first field taken from an entry, null if all come from the defaults.</summary>
public sealed record ResolvedFolderView(ViewMode Mode, SortColumn SortBy, bool SortDescending, GroupBy GroupBy, string? Columns, string? Source);

/// <summary>Effective view of a folder: its own entry, else the nearest ancestor that applies to subfolders, else the defaults, field by field.</summary>
public static class FolderViewResolver
{
    public static ResolvedFolderView Resolve(FolderViewEntry? own, FolderViewEntry? inherited, FolderViewDefaults defaults)
    {
        string? source = null;

        T Pick<T>(Func<FolderViewEntry, T?> get, T fallback) where T : struct
        {
            if (own is not null && get(own) is { } a)
            {
                source ??= own.Path;
                return a;
            }

            if (inherited is not null && get(inherited) is { } b)
            {
                source ??= inherited.Path;
                return b;
            }

            return fallback;
        }

        var mode = Pick(e => e.Mode, defaults.Mode);
        var sort = Pick(e => e.SortBy, defaults.SortBy);
        var desc = Pick(e => e.SortDescending, defaults.SortDescending);
        var group = Pick(e => e.GroupBy, defaults.GroupBy);
        return new ResolvedFolderView(mode, sort, desc, group, own?.Columns ?? inherited?.Columns, source);
    }
}

public sealed class FolderViewFile
{
    public List<FolderViewEntry> Entries { get; set; } = [];
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true)]
[JsonSerializable(typeof(FolderViewFile))]
internal sealed partial class FolderViewsJson : JsonSerializerContext;

/// <summary>
/// Per-folder view settings in folder-views.json, bounded to the most recently used folders. Keys are normalised and case-insensitive;
/// pseudo locations (pollux:*) are keyed as written. Thread-safe; saving is debounced and a corrupt file reads as empty.
/// </summary>
public sealed class FolderViews : IDisposable
{
    public const int DefaultCapacity = 5000;
    public static readonly TimeSpan DefaultDebounce = TimeSpan.FromSeconds(2);

    private readonly object _gate = new();
    private readonly Dictionary<string, FolderViewEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly string? _file;
    private readonly int _capacity;
    private readonly TimeSpan _debounce;
    private readonly Timer? _timer;
    private long _clock;
    private bool _dirty;

    /// <param name="file">The json file; null keeps everything in memory.</param>
    public FolderViews(string? file = null, int capacity = DefaultCapacity, TimeSpan? debounce = null)
    {
        _file = file;
        _capacity = Math.Max(1, capacity);
        _debounce = debounce ?? DefaultDebounce;
        if (file is not null)
        {
            Load(file);
            _timer = new Timer(_ => Flush());
        }
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    /// <summary>The key a folder is stored under; pseudo locations keep their form, lower-cased.</summary>
    public static string KeyOf(string folder) =>
        folder.TrimStart().StartsWith("pollux:", StringComparison.OrdinalIgnoreCase) ? folder.Trim().ToLowerInvariant() : WinPath.Normalize(folder);

    /// <summary>A copy of the folder's own entry, or null.</summary>
    public FolderViewEntry? Find(string folder)
    {
        lock (_gate)
        {
            return _entries.TryGetValue(KeyOf(folder), out var e) ? e.Clone() : null;
        }
    }

    /// <summary>The view <paramref name="folder"/> is shown with. Counts as a use of its own entry.</summary>
    public ResolvedFolderView Resolve(string folder, FolderViewDefaults defaults)
    {
        lock (_gate)
        {
            var key = KeyOf(folder);
            if (_entries.TryGetValue(key, out var own))
            {
                own.Used = ++_clock;
            }

            return FolderViewResolver.Resolve(own, NearestInherited(key), defaults);
        }
    }

    /// <summary>Remembers the given fields for a folder; null arguments keep what the entry has. Keeps the apply-to-subfolders flag.</summary>
    public void Update(string folder, ViewMode? mode = null, SortColumn? sortBy = null, bool? sortDescending = null, GroupBy? groupBy = null, string? columns = null)
    {
        lock (_gate)
        {
            var e = GetOrAdd(folder);
            e.Mode = mode ?? e.Mode;
            e.SortBy = sortBy ?? e.SortBy;
            e.SortDescending = sortDescending ?? e.SortDescending;
            e.GroupBy = groupBy ?? e.GroupBy;
            e.Columns = columns ?? e.Columns;
            Touched(e);
        }

        ScheduleSave();
    }

    /// <summary>
    /// Stores <paramref name="view"/> for the folder and lets all subfolders inherit it; their own entries are dropped
    /// (like Explorer's "Apply to Folders"). Ignored for pseudo locations.
    /// </summary>
    public void ApplyToSubfolders(string folder, ResolvedFolderView view)
    {
        var key = KeyOf(folder);
        if (IsPseudo(key))
        {
            return;
        }

        lock (_gate)
        {
            foreach (var k in _entries.Keys.Where(k => !IsPseudo(k) && !k.Equals(key, StringComparison.OrdinalIgnoreCase) && WinPath.IsSameOrUnder(k, key)).ToList())
            {
                _entries.Remove(k);
            }

            var e = GetOrAdd(folder);
            e.Mode = view.Mode;
            e.SortBy = view.SortBy;
            e.SortDescending = view.SortDescending;
            e.GroupBy = view.GroupBy;
            e.Columns = view.Columns;
            e.ApplyToSubfolders = true;
            Touched(e);
        }

        ScheduleSave();
    }

    /// <summary>Forgets the folder's own entry ("Reset view for this folder"); it then follows an ancestor or the defaults. False if it had none.</summary>
    public bool Reset(string folder)
    {
        bool removed;
        lock (_gate)
        {
            removed = _entries.Remove(KeyOf(folder));
        }

        if (removed)
        {
            ScheduleSave();
        }

        return removed;
    }

    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
        }

        ScheduleSave();
    }

    /// <summary>Restarts the debounce timer; the file is written once nothing changed for the debounce period.</summary>
    public void ScheduleSave()
    {
        if (_timer is null)
        {
            return;
        }

        lock (_gate)
        {
            _dirty = true;
        }

        try
        {
            _timer.Change(_debounce, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>Writes pending changes now.</summary>
    public void Flush()
    {
        if (_file is null)
        {
            return;
        }

        FolderViewFile snapshot;
        lock (_gate)
        {
            if (!_dirty)
            {
                return;
            }

            _dirty = false;
            snapshot = new FolderViewFile { Entries = [.. _entries.Values.OrderBy(e => e.Used).Select(e => e.Clone())] };
        }

        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_file)!);
            AtomicFile.WriteAllText(_file, JsonSerializer.Serialize(snapshot, FolderViewsJson.Default.FolderViewFile));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            lock (_gate)
            {
                _dirty = true;
            }
        }
    }

    public void Dispose()
    {
        _timer?.Dispose();
        Flush();
    }

    private static bool IsPseudo(string key) => key.StartsWith("pollux:", StringComparison.OrdinalIgnoreCase);

    private FolderViewEntry? NearestInherited(string key)
    {
        if (IsPseudo(key))
        {
            return null;
        }

        var segments = WinPath.Segments(key);
        for (var i = segments.Count - 2; i >= 0; i--)
        {
            if (_entries.TryGetValue(segments[i], out var e) && e.ApplyToSubfolders)
            {
                return e;
            }
        }

        return null;
    }

    private FolderViewEntry GetOrAdd(string folder)
    {
        var key = KeyOf(folder);
        if (!_entries.TryGetValue(key, out var e))
        {
            e = new FolderViewEntry { Path = key };
            _entries[key] = e;
        }

        return e;
    }

    private void Touched(FolderViewEntry e)
    {
        e.Used = ++_clock;
        while (_entries.Count > _capacity)
        {
            _entries.Remove(_entries.MinBy(x => x.Value.Used).Key);
        }
    }

    private void Load(string file)
    {
        FolderViewFile? data = null;
        try
        {
            if (File.Exists(file))
            {
                data = JsonSerializer.Deserialize(File.ReadAllText(file), FolderViewsJson.Default.FolderViewFile);
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
        }

        foreach (var e in (data?.Entries ?? []).Where(e => e is { Path.Length: > 0 }).OrderBy(e => e.Used))
        {
            e.Path = KeyOf(e.Path);
            e.Used = ++_clock;
            _entries[e.Path] = e;
        }

        while (_entries.Count > _capacity)
        {
            _entries.Remove(_entries.MinBy(x => x.Value.Used).Key);
        }
    }
}
