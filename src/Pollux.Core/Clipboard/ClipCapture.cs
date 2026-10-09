using System.Text;
using Pollux.Core.Settings;

namespace Pollux.Core.Clipboard;

/// <summary>What the clipboard holds after a copy, read by the App's clipboard listener.</summary>
public sealed class CopiedContent
{
    /// <summary>Plain-text form of the copy (rich text and HTML already reduced to plain text).</summary>
    public string? Text { get; init; }
    /// <summary>Encoded image (PNG).</summary>
    public byte[]? Image { get; init; }
    /// <summary>Full paths of copied files.</summary>
    public IReadOnlyList<string>? Files { get; init; }
    /// <summary>Process name of the app that put the content on the clipboard.</summary>
    public string? SourceApp { get; init; }
    /// <summary>Executable path of that app.</summary>
    public string? SourcePath { get; init; }
    /// <summary>
    /// The owner asked clipboard monitors not to record it: ExcludeClipboardContentFromMonitorProcessing is present
    /// or CanIncludeInClipboardHistory is 0 (password managers do this).
    /// </summary>
    public bool Private { get; init; }
}

/// <summary>Why a copy was not recorded.</summary>
public enum CaptureSkip
{
    None,
    /// <summary>Capture is paused.</summary>
    Paused,
    /// <summary>Nothing Pollux records: no files, no non-blank text, no image.</summary>
    Empty,
    /// <summary>The source app marked the content as not for clipboard history.</summary>
    Private,
    /// <summary>The source process is on the exclusion list.</summary>
    ExcludedApp,
    /// <summary>Larger than the configured size limit for its kind.</summary>
    TooLarge,
}

/// <summary>The Clip to record, or why there is none.</summary>
/// <param name="LogLine">For a copy that is skipped for its size: a line for the log, never the content itself.</param>
public sealed record CaptureResult(Clip? Clip, CaptureSkip Skip, string? LogLine = null);

/// <summary>Decides whether and how a copy goes into the Clipboard History.</summary>
public static class ClipCapture
{
    public static CaptureResult Decide(CopiedContent copied, ClipboardSettings settings, DateTime now)
    {
        if (settings.Paused)
        {
            return new(null, CaptureSkip.Paused);
        }

        if (copied.Private)
        {
            return new(null, CaptureSkip.Private);
        }

        if (IsExcluded(copied.SourceApp, settings.ExcludeProcesses))
        {
            return new(null, CaptureSkip.ExcludedApp);
        }

        if (copied.Files is { Count: > 0 } files)
        {
            return new(Clip.FromFiles(files, now, copied.SourceApp, copied.SourcePath), CaptureSkip.None);
        }

        if (!string.IsNullOrWhiteSpace(copied.Text))
        {
            var bytes = Encoding.UTF8.GetByteCount(copied.Text);
            return Exceeds(bytes, settings.MaxTextKilobytes * 1024L)
                ? TooLarge("text", bytes, settings.MaxTextKilobytes + " KB", copied.SourceApp)
                : new(Clip.FromText(copied.Text, now, copied.SourceApp, copied.SourcePath), CaptureSkip.None);
        }

        if (copied.Image is { Length: > 0 } image)
        {
            return Exceeds(image.Length, settings.MaxImageMegabytes * 1024L * 1024L)
                ? TooLarge("image", image.Length, settings.MaxImageMegabytes + " MB", copied.SourceApp)
                : new(Clip.FromImage(image, now, copied.SourceApp, copied.SourcePath), CaptureSkip.None);
        }

        return new(null, CaptureSkip.Empty);
    }

    /// <summary>A limit of 0 or less means no limit.</summary>
    private static bool Exceeds(long size, long limit) => limit > 0 && size > limit;

    private static CaptureResult TooLarge(string kind, long bytes, string limit, string? sourceApp) =>
        new(null, CaptureSkip.TooLarge,
            $"Clip not recorded: {kind} of {bytes} bytes from {sourceApp ?? "unknown app"} is larger than the limit of {limit}");

    /// <summary>True when the process name is on the list; names compare case-insensitively, ".exe" optional.</summary>
    private static bool IsExcluded(string? process, IEnumerable<string> excluded)
    {
        if (string.IsNullOrEmpty(process))
        {
            return false;
        }

        var name = WithoutExe(process);
        return excluded.Any(e => string.Equals(WithoutExe(e.Trim()), name, StringComparison.OrdinalIgnoreCase));
    }

    private static string WithoutExe(string name) =>
        name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
}
