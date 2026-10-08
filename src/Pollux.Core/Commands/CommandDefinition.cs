namespace Pollux.Core.Commands;

/// <summary>A named action. <paramref name="When"/> is a context key, optionally negated with "!".</summary>
public sealed record CommandDefinition(string Id, string Title, string? DefaultKey = null, string? When = null, bool ShowInPalette = true);

public sealed record KeybindingEntry(string Key, string Command, string? When = null);

/// <summary>The set of context keys that are currently true, e.g. "filterFocused".</summary>
public sealed class Context
{
    private readonly HashSet<string> _keys;

    private Context(IEnumerable<string> keys) => _keys = new HashSet<string>(keys, StringComparer.Ordinal);

    public static Context Empty { get; } = new([]);

    public static Context Of(params string[] keys) => new(keys);

    public bool Matches(string? when)
    {
        if (string.IsNullOrEmpty(when))
        {
            return true;
        }

        return when.StartsWith('!') ? !_keys.Contains(when[1..]) : _keys.Contains(when);
    }
}
