using Pollux.Core.Navigation;

namespace Pollux.Core.Launch;

/// <summary>
/// Where Pollux runs from, as the Admin Instance sees it (ADR 0008, SECURITY.md S-09): an elevated process started from
/// a folder the non-elevated user can write to runs an exe and DLLs any process of that user could have replaced.
/// </summary>
public static class InstallLocation
{
    /// <summary>
    /// True unless <paramref name="exeFolder"/> lies inside one of <paramref name="adminOnlyRoots"/> (Program Files and
    /// Program Files (x86)), the folders only administrators can write to. A path that can't be judged (empty, network,
    /// with "." or ".." segments) counts as user-writable.
    /// </summary>
    public static bool IsUserWritable(string exeFolder, IEnumerable<string> adminOnlyRoots)
    {
        if (!IsPlainLocalPath(exeFolder)) return true;
        return !adminOnlyRoots.Where(IsPlainLocalPath).Any(root => WinPath.IsSameOrUnder(exeFolder, root));
    }

    /// <summary>An absolute "X:\…" path without relative segments.</summary>
    private static bool IsPlainLocalPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var p = WinPath.Normalize(path);
        return WinPath.IsDriveRooted(p) && !p.Split('\\').Any(segment => segment is "." or "..");
    }
}
