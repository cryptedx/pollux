namespace Pollux.Core.Navigation;

/// <summary>
/// Tab behaviour for one <see cref="PathCompletionResult"/>: the first Tab inserts the common prefix when it extends the typed
/// text, further Tabs cycle through the candidates (Shift+Tab backwards). Create a new session whenever the typed text changes.
/// </summary>
public sealed class CompletionSession(string typed, PathCompletionResult result)
{
    private int _index = -1;
    private bool _started;

    /// <summary>The text to put into the box, or null when there is nothing to complete.</summary>
    public string? Tab(bool backwards = false)
    {
        var candidates = result.Candidates;
        if (candidates.Count == 0)
        {
            return null;
        }

        if (!_started)
        {
            _started = true;
            if (candidates.Count > 1 && result.CommonPrefix.Length > typed.TrimStart().Length)
            {
                return result.CommonPrefix;
            }
        }

        _index = backwards
            ? (_index <= 0 ? candidates.Count - 1 : _index - 1)
            : (_index + 1) % candidates.Count;
        return candidates[_index].Text;
    }
}
