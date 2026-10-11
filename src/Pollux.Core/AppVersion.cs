namespace Pollux.Core;

/// <summary>
/// The running build as the title bar shows it: the release number and the short commit hash.
/// Parsed from the assembly's informational version, which the .NET SDK writes as <c>1.4.0+&lt;full commit hash&gt;</c>.
/// </summary>
public sealed record AppVersion(string Number, string? Commit)
{
    public const int ShortHashLength = 7;

    public static AppVersion Parse(string? informationalVersion)
    {
        var text = informationalVersion?.Trim() ?? "";
        var plus = text.IndexOf('+', StringComparison.Ordinal);
        var number = plus < 0 ? text : text[..plus];
        var commit = plus < 0 ? "" : text[(plus + 1)..];
        if (number.Length == 0) number = "0.0.0";
        return new AppVersion(number, commit.Length == 0 ? null : commit[..Math.Min(ShortHashLength, commit.Length)]);
    }

    /// <summary>Compact label: <c>1.4.0 · f69a251</c>.</summary>
    public string Label => Commit is null ? Number : $"{Number} · {Commit}";

    /// <summary>What a click copies, for bug reports: <c>1.4.0+f69a251</c>.</summary>
    public override string ToString() => Commit is null ? Number : $"{Number}+{Commit}";
}
