namespace Pollux.Core.Clipboard;

/// <summary>How long Clips stay in the Clipboard History, whichever limit hits first. 0 means no limit. Kept Clips never rotate out.</summary>
public sealed record ClipRetention(int MaxClips = 500, int MaxAgeDays = 30)
{
    public static ClipRetention Default { get; } = new();
}
