namespace Pollux.Core.Files;

/// <summary>
/// Explorer-style type-ahead: typing selects the next entry starting with the typed text. Keys typed within
/// <see cref="Window"/> of each other accumulate; a single key cycles through the entries starting with it.
/// </summary>
public sealed class TypeAheadMatcher
{
    public static readonly TimeSpan Window = TimeSpan.FromMilliseconds(900);

    private string _text = "";
    private DateTime _at;

    /// <summary>The text typed so far.</summary>
    public string Text => _text;

    /// <summary>Adds <paramref name="c"/> typed at <paramref name="now"/>; returns the index to select in <paramref name="names"/>, or -1 if nothing matches.</summary>
    public int Next(char c, DateTime now, int selectedIndex, IReadOnlyList<string> names)
    {
        _text = now - _at < Window ? _text + c : c.ToString();
        _at = now;
        var start = _text.Length == 1 ? selectedIndex + 1 : Math.Max(0, selectedIndex);
        for (var k = 0; k < names.Count; k++)
        {
            var i = (start + k) % names.Count;
            if (names[i].StartsWith(_text, StringComparison.OrdinalIgnoreCase)) return i;
        }

        return -1;
    }
}
