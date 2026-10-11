using System.Buffers;

namespace Pollux.Core.Places;

/// <summary>
/// Validation for the Map Network Drive dialog and the drive actions in the sidebar.
/// Pollux only checks the shape of the input; credentials are asked for by Windows itself.
/// </summary>
public static class DriveInput
{
    private static readonly SearchValues<char> InvalidShareChars = SearchValues.Create("<>|\"?*:");

    /// <summary>Accepts "z", "Z:", "z:\" and returns "Z:".</summary>
    public static bool TryNormalizeLetter(string? input, out string letter)
    {
        letter = "";
        var s = (input ?? "").Trim().TrimEnd('\\', '/');
        if (s.EndsWith(':')) s = s[..^1];
        if (s.Length != 1 || !char.IsAsciiLetter(s[0])) return false;
        letter = char.ToUpperInvariant(s[0]) + ":";
        return true;
    }

    /// <summary>Accepts \\server\share (also with forward slashes or quotes) and returns it with backslashes.</summary>
    public static bool TryNormalizeShare(string? input, out string share)
    {
        share = "";
        var s = (input ?? "").Trim().Trim('"').Replace('/', '\\').TrimEnd('\\');
        if (!s.StartsWith(@"\\", StringComparison.Ordinal)) return false;
        var parts = s[2..].Split('\\');
        if (parts.Length != 2) return false;
        foreach (var p in parts)
        {
            if (p.Length == 0 || p.Trim() != p || p.AsSpan().ContainsAny(InvalidShareChars) || p.Any(char.IsControl)) return false;
        }

        share = @"\\" + parts[0] + @"\" + parts[1];
        return true;
    }

    /// <summary>Highest free letter from Z down to D, as "Z:", or null. <paramref name="usedRoots"/> are roots like "C:\" or "c:".</summary>
    public static string? NextFreeLetter(IEnumerable<string> usedRoots)
    {
        var used = new HashSet<char>();
        foreach (var r in usedRoots)
        {
            if (r.Length >= 1 && char.IsAsciiLetter(r[0])) used.Add(char.ToUpperInvariant(r[0]));
        }

        for (var c = 'Z'; c >= 'D'; c--)
        {
            if (!used.Contains(c)) return c + ":";
        }

        return null;
    }

    public static bool CanDisconnect(DriveType type) => type == DriveType.Network;

    public static bool CanEject(DriveType type) => type is DriveType.Removable or DriveType.CDRom;
}
