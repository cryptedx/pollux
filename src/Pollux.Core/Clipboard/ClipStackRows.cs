namespace Pollux.Core.Clipboard;

/// <summary>One row of the Clipboard Stack list: a section header (<see cref="Clip"/> is null) or a Clip.</summary>
public sealed record ClipStackRow(string? Header, Clip? Clip);

/// <summary>
/// The rows the Clipboard Stack shows for a search: a "Kept Clips" section (all matching Kept Clips), then the
/// "Clipboard History" section (at most <c>maxRows</c> matching Clips); the history header only appears under Kept Clips.
/// Also the keyboard navigation over those rows (headers are skipped).
/// </summary>
public sealed class ClipStackRows
{
    public const string KeptHeader = "Kept Clips";
    public const string HistoryHeader = "Clipboard History";

    private ClipStackRows(IReadOnlyList<ClipStackRow> rows, int hitCount)
    {
        Rows = rows;
        HitCount = hitCount;
    }

    public IReadOnlyList<ClipStackRow> Rows { get; }

    /// <summary>Clips matching the query, before the row limit.</summary>
    public int HitCount { get; }

    public static ClipStackRows Build(ClipboardHistory history, string query, int maxRows)
    {
        var hits = ClipSearch.Find(history.All, query);
        var rows = new List<ClipStackRow>();
        var kept = hits.Where(c => c.Kept).ToList();
        var recent = hits.Where(c => !c.Kept).Take(maxRows).ToList();
        if (kept.Count > 0)
        {
            rows.Add(new ClipStackRow(KeptHeader, null));
            rows.AddRange(kept.Select(c => new ClipStackRow(null, c)));
            if (recent.Count > 0)
            {
                rows.Add(new ClipStackRow(HistoryHeader, null));
            }
        }

        rows.AddRange(recent.Select(c => new ClipStackRow(null, c)));
        return new ClipStackRows(rows, hits.Count);
    }

    /// <summary>Row index of the Clip <paramref name="id"/>, else of the first Clip row; -1 when there is no Clip row.</summary>
    public int IndexToSelect(string? id)
    {
        var index = id is null ? -1 : Rows.ToList().FindIndex(r => r.Clip?.Id == id);
        return index >= 0 ? index : Rows.ToList().FindIndex(r => r.Clip is not null);
    }

    /// <summary>The next Clip row <paramref name="step"/> (+1 or -1) away from <paramref name="current"/>, wrapping around and skipping headers; -1 when there is none.</summary>
    public int Move(int current, int step)
    {
        if (Rows.Count == 0) return -1;
        var i = current;
        for (var n = 0; n < Rows.Count; n++)
        {
            i = (i + step + Rows.Count) % Rows.Count;
            if (Rows[i].Clip is not null) return i;
        }

        return -1;
    }

    /// <summary>The Clip to select after <paramref name="id"/> is deleted: the next one, else the previous one.</summary>
    public string? NeighbourId(string id)
    {
        var clips = Rows.Select(r => r.Clip).OfType<Clip>().ToList();
        var index = clips.FindIndex(c => c.Id == id);
        if (index + 1 < clips.Count) return clips[index + 1].Id;
        return index > 0 ? clips[index - 1].Id : null;
    }

    /// <summary>Position of a Kept Clip within the Kept section of <paramref name="history"/>, -1 when it is not kept.</summary>
    public static int IndexOfKept(ClipboardHistory history, string id)
    {
        for (var i = 0; i < history.Kept.Count; i++)
        {
            if (history.Kept[i].Id == id) return i;
        }

        return -1;
    }
}
