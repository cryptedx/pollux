namespace Pollux.Core.Snap;

/// <summary>Where Ctrl+S in the Snap Board writes: over the Snap's own file, or a new Snap file in the Snaps folder.</summary>
public static class SnapSaveTarget
{
    /// <param name="savedPath">The file the Snap was auto-saved or last saved to; null when it has none yet.</param>
    /// <param name="exists">Whether a full path exists already.</param>
    public static string For(string? savedPath, string folder, DateTime now, Func<string, bool> exists) =>
        savedPath ?? Path.Combine(folder, SnapFileName.For(now, name => exists(Path.Combine(folder, name))));
}
