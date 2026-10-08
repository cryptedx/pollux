namespace Pollux.Core.Commands;

[Flags]
public enum KeyModifiers
{
    None = 0,
    Control = 1,
    Shift = 2,
    Alt = 4,
    Windows = 8,
}

/// <summary>A key plus modifiers, written VS Code style: "ctrl+shift+p". Key names are lower-case ("p", "f5", "left", "&lt;", "ö").</summary>
public readonly record struct KeyChord(KeyModifiers Modifiers, string Key)
{
    public static KeyChord Parse(string text) =>
        TryParse(text, out var chord) ? chord : throw new FormatException($"Invalid key chord '{text}'.");

    public static bool TryParse(string? text, out KeyChord chord)
    {
        chord = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var t = text.Trim().ToLowerInvariant();
        // "ctrl++" means Ctrl and the plus key.
        string key;
        string modifierPart;
        if (t.EndsWith("++", StringComparison.Ordinal))
        {
            key = "+";
            modifierPart = t[..^2];
        }
        else
        {
            var lastPlus = t.LastIndexOf('+');
            key = lastPlus < 0 ? t : t[(lastPlus + 1)..];
            modifierPart = lastPlus < 0 ? "" : t[..lastPlus];
        }

        if (key.Length == 0)
        {
            return false;
        }

        var modifiers = KeyModifiers.None;
        foreach (var part in modifierPart.Split('+', StringSplitOptions.RemoveEmptyEntries))
        {
            switch (part)
            {
                case "ctrl" or "control": modifiers |= KeyModifiers.Control; break;
                case "shift": modifiers |= KeyModifiers.Shift; break;
                case "alt": modifiers |= KeyModifiers.Alt; break;
                case "win" or "meta" or "cmd": modifiers |= KeyModifiers.Windows; break;
                default: return false;
            }
        }

        chord = new KeyChord(modifiers, Normalize(key));
        return true;
    }

    private static string Normalize(string key) => key switch
    {
        "esc" => "escape",
        "return" => "enter",
        "del" => "delete",
        "pgup" => "pageup",
        "pgdn" => "pagedown",
        _ => key,
    };

    public override string ToString()
    {
        var parts = new List<string>(5);
        if (Modifiers.HasFlag(KeyModifiers.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(KeyModifiers.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(KeyModifiers.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(KeyModifiers.Windows)) parts.Add("Win");
        parts.Add(Key.Length == 1 ? Key.ToUpperInvariant() : char.ToUpperInvariant(Key[0]) + Key[1..]);
        return string.Join('+', parts);
    }
}
