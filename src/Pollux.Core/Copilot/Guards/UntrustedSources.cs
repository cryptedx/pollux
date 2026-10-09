namespace Pollux.Core.Copilot.Guards;

/// <summary>
/// Untrusted places (spec "Tainted Chat"): content read from them may carry instructions aimed at Copilot (prompt
/// injection), so reading it makes the Chat a Tainted Chat. Patterns as in <see cref="PathPatterns"/>.
/// </summary>
public sealed class UntrustedSources
{
    /// <summary>Downloads, the Inbox (the Desktop), Temp folders, the browser/Outlook download cache and archives.</summary>
    public static IReadOnlyList<string> Defaults { get; } =
    [
        @"~\Downloads",
        @"~\Desktop",
        @"%TEMP%\",
        @"%LOCALAPPDATA%\Temp",
        @"%SystemRoot%\Temp",
        @"%LOCALAPPDATA%\Microsoft\Windows\INetCache",
        "*.zip",
        "*.7z",
        "*.rar",
        "*.tar",
        "*.tgz",
        "*.gz",
        "*.bz2",
        "*.xz",
        "*.cab",
        "*.iso",
    ];

    private readonly PathPatterns _patterns;

    public UntrustedSources(IEnumerable<string>? extra = null)
    {
        _patterns = new([.. Defaults, .. extra ?? []]);
    }

    /// <summary>The pattern that makes <paramref name="path"/> (already resolved to an absolute path) untrusted, or null.</summary>
    public string? Match(string path, GuardContext context) => _patterns.Match(path, context);
}
