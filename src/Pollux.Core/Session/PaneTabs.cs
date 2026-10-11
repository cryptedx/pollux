namespace Pollux.Core.Session;

/// <summary>
/// The Tabs of one Pane: their order, which one is active and the stack of closed Tabs.
/// A Pane always has at least one Tab: detaching or closing the last one replaces it with a new Tab.
/// </summary>
public sealed class PaneTabs
{
    private readonly List<TabModel> _tabs = [];
    private readonly Stack<string> _closed = new();
    private readonly Func<string> _newTabLocation;

    /// <param name="newTabLocation">Where a new Tab opens (a folder or <see cref="Navigation.WinPath.Home"/>).</param>
    public PaneTabs(Func<string> newTabLocation, PaneSession? session = null)
    {
        _newTabLocation = newTabLocation;
        Load(session ?? new PaneSession { Tabs = [] });
    }

    public IReadOnlyList<TabModel> Tabs => _tabs;
    public int ActiveIndex { get; private set; }
    public TabModel Active => _tabs[ActiveIndex];

    /// <summary>Replaces all Tabs (the closed-Tab stack is kept).</summary>
    public void Load(PaneSession session)
    {
        _tabs.Clear();
        foreach (var t in session.Tabs) _tabs.Add(new TabModel(t.Location) { ViewMode = t.ViewMode });
        if (_tabs.Count == 0) _tabs.Add(new TabModel(_newTabLocation()));
        ActiveIndex = Math.Clamp(session.ActiveTab, 0, _tabs.Count - 1);
    }

    public PaneSession ToSession() => new()
    {
        Tabs = _tabs.Select(t => new TabSession { Location = t.Location, ViewMode = t.ViewMode }).ToList(),
        ActiveTab = ActiveIndex,
    };

    /// <summary>Removes a Tab and returns its location (for moving it elsewhere). The shown Tab stays active unless it is the one removed.</summary>
    public string Detach(int index)
    {
        var location = _tabs[index].Location;
        if (_tabs.Count == 1)
        {
            _tabs[0] = new TabModel(_newTabLocation());
            ActiveIndex = 0;
            return location;
        }

        _tabs.RemoveAt(index);
        if (ActiveIndex >= _tabs.Count || index < ActiveIndex) ActiveIndex = Math.Max(0, ActiveIndex - 1);
        return location;
    }

    /// <summary>Inserts a Tab at <paramref name="index"/> (clamped) and makes it active.</summary>
    public void Insert(int index, string location)
    {
        _tabs.Insert(Math.Clamp(index, 0, _tabs.Count), new TabModel(location));
        ActiveIndex = Math.Clamp(index, 0, _tabs.Count - 1);
    }

    /// <summary>Moves a Tab within the Pane; the shown Tab stays active, wherever it ends up.</summary>
    public void Reorder(int from, int to)
    {
        if (from == to || from < 0 || from >= _tabs.Count) return;
        var active = Active;
        var t = _tabs[from];
        _tabs.RemoveAt(from);
        _tabs.Insert(Math.Clamp(to, 0, _tabs.Count), t);
        ActiveIndex = _tabs.IndexOf(active);
    }

    /// <summary>Opens a Tab right after the active one.</summary>
    public void New(string? location = null, bool activate = true)
    {
        _tabs.Insert(ActiveIndex + 1, new TabModel(location ?? _newTabLocation()));
        if (activate) ActiveIndex++;
    }

    /// <summary>Detaches a Tab and remembers it for <see cref="ReopenClosed"/>. Ignores an index out of range.</summary>
    public void Close(int index)
    {
        if (index < 0 || index >= _tabs.Count) return;
        _closed.Push(Detach(index));
    }

    /// <summary>Opens the most recently closed Tab again, after the active one.</summary>
    public void ReopenClosed()
    {
        if (_closed.Count > 0) New(_closed.Pop());
    }

    /// <summary>Activates the Tab <paramref name="delta"/> steps away, wrapping around.</summary>
    public void Next(int delta) => SwitchTo(((ActiveIndex + delta) % _tabs.Count + _tabs.Count) % _tabs.Count);

    public void SwitchTo(int index) => ActiveIndex = Math.Clamp(index, 0, _tabs.Count - 1);

    /// <summary>Moves Tab <paramref name="fromIndex"/> of one Pane to <paramref name="toIndex"/> of the same or another Pane.</summary>
    public static void Move(PaneTabs from, int fromIndex, PaneTabs to, int toIndex)
    {
        if (ReferenceEquals(from, to))
        {
            from.Reorder(fromIndex, toIndex);
            return;
        }

        var mode = from._tabs[fromIndex].ViewMode;
        to.Insert(toIndex, from.Detach(fromIndex));
        to.Active.ViewMode = mode;
    }
}
