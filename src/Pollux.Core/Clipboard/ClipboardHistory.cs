namespace Pollux.Core.Clipboard;

/// <summary>All Clips Pollux has captured: Kept Clips in the user's order, the rest newest first.</summary>
public sealed class ClipboardHistory
{
    private readonly List<Clip> _kept = [];
    private readonly List<Clip> _recent = [];

    /// <summary>Limits applied on every Add and on Prune.</summary>
    public ClipRetention Retention { get; set; } = ClipRetention.Default;

    /// <summary>Kept Clips in the user's order.</summary>
    public IReadOnlyList<Clip> Kept => _kept;

    /// <summary>Clips that are not kept, newest first.</summary>
    public IReadOnlyList<Clip> Recent => _recent;

    /// <summary>Kept Clips first, then the rest newest first: the order the Clipboard Stack shows.</summary>
    public IEnumerable<Clip> All => _kept.Concat(_recent);

    /// <summary>Rebuilds a history read from storage.</summary>
    internal static ClipboardHistory Restore(IEnumerable<Clip> kept, IEnumerable<Clip> recent)
    {
        var history = new ClipboardHistory();
        foreach (var clip in kept)
        {
            clip.Kept = true;
            history._kept.Add(clip);
        }

        history._recent.AddRange(recent);
        return history;
    }

    public Clip? Find(string id) => All.FirstOrDefault(c => c.Id == id);

    /// <summary>
    /// Records a Clip. Content identical to an existing Clip moves that Clip to the top instead and returns it;
    /// a Kept Clip stays where the user put it.
    /// </summary>
    public Clip Add(Clip clip, DateTime now)
    {
        var existing = All.FirstOrDefault(c => c.ContentHash == clip.ContentHash);
        if (existing is not null)
        {
            existing.CapturedAt = clip.CapturedAt;
            existing.SourceApp = clip.SourceApp;
            existing.SourcePath = clip.SourcePath;
            if (existing.Kept)
            {
                return existing;
            }

            _recent.Remove(existing);
            clip = existing;
        }

        _recent.Insert(0, clip);
        Prune(now);
        return clip;
    }

    /// <summary>Rotates out Clips beyond the Retention limits. Kept Clips are exempt.</summary>
    public void Prune(DateTime now)
    {
        if (Retention.MaxAgeDays > 0)
        {
            var cutoff = now.AddDays(-Retention.MaxAgeDays);
            _recent.RemoveAll(c => c.CapturedAt < cutoff);
        }

        if (Retention.MaxClips > 0 && _recent.Count > Retention.MaxClips)
        {
            _recent.RemoveRange(Retention.MaxClips, _recent.Count - Retention.MaxClips);
        }
    }

    /// <summary>Makes a Clip a Kept Clip at the end of the Kept section; an already Kept Clip only gets the title.</summary>
    public void Keep(string id, string? title = null)
    {
        if (Find(id) is not { } clip)
        {
            return;
        }

        if (!clip.Kept)
        {
            _recent.Remove(clip);
            _kept.Add(clip);
            clip.Kept = true;
        }

        if (title is not null)
        {
            SetTitle(id, title);
        }
    }

    /// <summary>
    /// Turns a Kept Clip back into a normal Clip, placed in the history by its capture time, then applies the Retention
    /// (so a released Clip beyond the limits rotates out).
    /// </summary>
    public void Release(string id, DateTime now)
    {
        if (_kept.Find(c => c.Id == id) is not { } clip)
        {
            return;
        }

        _kept.Remove(clip);
        clip.Kept = false;
        InsertByTime(clip);
        Prune(now);
    }

    /// <summary>Sets the title of a Clip; blank clears it.</summary>
    public void SetTitle(string id, string? title)
    {
        if (Find(id) is { } clip)
        {
            clip.Title = string.IsNullOrWhiteSpace(title) ? null : title.Trim();
        }
    }

    /// <summary>Moves a Kept Clip to <paramref name="index"/> within the Kept section (clamped).</summary>
    public void MoveKept(string id, int index)
    {
        if (_kept.Find(c => c.Id == id) is not { } clip)
        {
            return;
        }

        _kept.Remove(clip);
        _kept.Insert(Math.Clamp(index, 0, _kept.Count), clip);
    }

    /// <summary>
    /// Records that a Clip was pasted from the Clipboard Stack: it moves to the top like a fresh copy, keeping its source app.
    /// A Kept Clip stays where the user put it. Pollux's own clipboard write must not be captured as a new Clip.
    /// </summary>
    public void Pasted(string id, DateTime now)
    {
        if (Find(id) is not { } clip) return;
        clip.CapturedAt = now;
        if (clip.Kept) return;
        _recent.Remove(clip);
        _recent.Insert(0, clip);
    }

    public void Delete(string id)
    {
        _kept.RemoveAll(c => c.Id == id);
        _recent.RemoveAll(c => c.Id == id);
    }

    /// <summary>Removes every Clip except the Kept Clips.</summary>
    public void ClearHistory() => _recent.Clear();

    /// <summary>
    /// Brings back the stored Clips into a history that started empty because the storage could not be read at first.
    /// Clips of this session win over stored duplicates (newer details), but a Clip that was Kept on disk stays Kept;
    /// stored Kept Clips come first in their stored order. Applies the Retention afterwards.
    /// </summary>
    public void MergeStored(ClipboardHistory stored, DateTime now)
    {
        var keptOrder = new List<Clip>();
        foreach (var storedClip in stored.Kept)
        {
            var clip = All.FirstOrDefault(c => c.ContentHash == storedClip.ContentHash) ?? storedClip;
            if (clip != storedClip)
            {
                clip.Title ??= storedClip.Title;
            }

            _kept.Remove(clip);
            _recent.Remove(clip);
            clip.Kept = true;
            keptOrder.Add(clip);
        }

        _kept.InsertRange(0, keptOrder);

        foreach (var storedClip in stored.Recent)
        {
            if (!All.Any(c => c.ContentHash == storedClip.ContentHash))
            {
                InsertByTime(storedClip);
            }
        }

        Prune(now);
    }

    private void InsertByTime(Clip clip)
    {
        var index = _recent.FindIndex(c => c.CapturedAt < clip.CapturedAt);
        _recent.Insert(index < 0 ? _recent.Count : index, clip);
    }
}
