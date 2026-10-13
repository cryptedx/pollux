namespace Pollux.Core.Snap;

/// <summary>Which Snaps snap.keepDays sends to the Recycle Bin.</summary>
public static class SnapRetention
{
    /// <summary>
    /// The names among <paramref name="names"/> that are Snaps (by name) taken more than <paramref name="keepDays"/> days
    /// before <paramref name="now"/>. Age counts from the time in the name, so saving a Snap again does not extend it.
    /// Other files are never due; 0 keeps everything.
    /// </summary>
    public static IEnumerable<string> Due(IEnumerable<string> names, DateTime now, int keepDays)
    {
        if (keepDays <= 0) yield break;
        var limit = now.AddDays(-keepDays);
        foreach (var name in names)
        {
            if (SnapFileName.TakenAt(name) is { } taken && taken < limit) yield return name;
        }
    }
}
