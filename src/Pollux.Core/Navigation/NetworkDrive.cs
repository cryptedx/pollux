namespace Pollux.Core.Navigation;

/// <summary>
/// Whether a path lies on a network drive: the one check behind git trust (ADR 0006) and Chat taint (SECURITY.md S-12).
/// Both are security decisions, so a drive whose type is unknown or can't be read counts as network (fail closed).
/// </summary>
public static class NetworkDrive
{
    /// <summary>The real machine; on other systems nothing is a network drive.</summary>
    public static bool IsNetwork(string path) =>
        OperatingSystem.IsWindows() && IsNetwork(path, root => new DriveInfo(root).DriveType);

    /// <summary>
    /// True when <paramref name="path"/> is a UNC path, has no drive letter, or its drive (as "X:\") is
    /// <see cref="DriveType.Network"/> or <see cref="DriveType.Unknown"/> per <paramref name="driveType"/>, or that can't be read.
    /// </summary>
    public static bool IsNetwork(string path, Func<string, DriveType> driveType)
    {
        var p = path.Trim();
        if (p.Length < 2 || p[1] != ':' || !char.IsAsciiLetter(p[0])) return true;
        try
        {
            return driveType(char.ToUpperInvariant(p[0]) + @":\") is DriveType.Network or DriveType.Unknown;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return true;
        }
    }
}
