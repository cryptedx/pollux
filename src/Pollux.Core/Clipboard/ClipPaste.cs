namespace Pollux.Core.Clipboard;

/// <summary>The window that was active before the Clipboard Stack opened.</summary>
public enum PasteTarget
{
    /// <summary>A window that accepts the Ctrl+V Pollux sends.</summary>
    Ready,
    /// <summary>An elevated window: UIPI blocks the Ctrl+V Pollux would send.</summary>
    Elevated,
    /// <summary>No previous window, or it is gone.</summary>
    None,
}

/// <summary>What pasting a Clip does: whether it goes on the clipboard, whether Ctrl+V follows, and the hint to show.</summary>
public sealed record PastePlan(bool SetClipboard, bool SendCtrlV, IReadOnlyList<string> Files, string? Hint);

/// <summary>Decides how a Clip chosen in the Clipboard Stack is pasted back.</summary>
public static class ClipPaste
{
    /// <summary>Hint when Pollux cannot send Ctrl+V itself.</summary>
    public const string CopiedHint = "Copied – press Ctrl+V";

    private const string GoneHint = "The files of this Clip no longer exist";

    /// <summary>
    /// Plans pasting <paramref name="clip"/>: Enter pastes into <paramref name="target"/>, Shift+Enter (<paramref name="onlyCopy"/>)
    /// only puts it on the clipboard. A file-list Clip pastes the files that still exist.
    /// </summary>
    public static PastePlan Plan(Clip clip, bool onlyCopy, PasteTarget target, Func<string, bool> fileExists)
    {
        List<string> files = [];
        string? missingHint = null;
        if (clip.Kind == ClipKind.Files)
        {
            files = clip.Files.Where(fileExists).ToList();
            var missing = clip.Files.Count - files.Count;
            if (files.Count == 0) return new(false, false, [], GoneHint);
            if (missing > 0) missingHint = $"{missing} of {clip.Files.Count} files no longer {(missing == 1 ? "exists" : "exist")}";
        }

        if (onlyCopy) return new(true, false, files, missingHint);
        var ready = target == PasteTarget.Ready;
        var hint = ready ? missingHint : missingHint is null ? CopiedHint : CopiedHint + " · " + missingHint;
        return new(true, ready, files, hint);
    }
}
