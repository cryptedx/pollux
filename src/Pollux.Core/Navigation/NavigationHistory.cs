namespace Pollux.Core.Navigation;

/// <summary>Back/forward history of one Tab.</summary>
public sealed class NavigationHistory(string start, int capacity = 100)
{
    private readonly List<string> _entries = [start];
    private int _index;

    public string Current => _entries[_index];
    public bool CanGoBack => _index > 0;
    public bool CanGoForward => _index < _entries.Count - 1;

    public void Navigate(string location)
    {
        if (string.Equals(location, Current, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _entries.RemoveRange(_index + 1, _entries.Count - _index - 1);
        _entries.Add(location);
        if (_entries.Count > capacity)
        {
            _entries.RemoveRange(0, _entries.Count - capacity);
        }

        _index = _entries.Count - 1;
    }

    /// <summary>Swaps the current entry for another location without adding a history step.</summary>
    public void ReplaceCurrent(string location) => _entries[_index] = location;

    public string? Back() => CanGoBack ? _entries[--_index] : null;

    public string? Forward() => CanGoForward ? _entries[++_index] : null;
}
