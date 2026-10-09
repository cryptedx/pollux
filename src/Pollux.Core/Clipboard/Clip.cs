using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace Pollux.Core.Clipboard;

public enum ClipKind { Text, Image, Files }

/// <summary>One item Pollux captured when something was copied: text, an image or a list of files.</summary>
public sealed class Clip
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public ClipKind Kind { get; set; }
    /// <summary>Plain text of a text Clip.</summary>
    public string? Text { get; set; }
    /// <summary>Full paths of a file-list Clip.</summary>
    public List<string> Files { get; set; } = [];
    /// <summary>Encoded image (PNG) of an image Clip. Not part of the history file: the ClipStore keeps images in their own files.</summary>
    [JsonIgnore]
    public byte[]? Image { get; set; }
    /// <summary>Identifies the content; two Clips with the same hash are duplicates.</summary>
    public string ContentHash { get; set; } = "";
    /// <summary>Process name of the app the Clip was copied from, e.g. "Code".</summary>
    public string? SourceApp { get; set; }
    /// <summary>Executable path of the source app, used for its icon.</summary>
    public string? SourcePath { get; set; }
    public DateTime CapturedAt { get; set; }
    /// <summary>Optional title of a Kept Clip.</summary>
    public string? Title { get; set; }
    /// <summary>True for a Kept Clip: it never rotates out of the Clipboard History.</summary>
    [JsonIgnore]
    public bool Kept { get; internal set; }

    public static Clip FromText(string text, DateTime capturedAt, string? sourceApp, string? sourcePath) =>
        new()
        {
            Kind = ClipKind.Text,
            Text = text,
            ContentHash = Hash(ClipKind.Text, Encoding.UTF8.GetBytes(text)),
            CapturedAt = capturedAt,
            SourceApp = sourceApp,
            SourcePath = sourcePath,
        };

    public static Clip FromImage(byte[] image, DateTime capturedAt, string? sourceApp, string? sourcePath) =>
        new()
        {
            Kind = ClipKind.Image,
            Image = image,
            ContentHash = Hash(ClipKind.Image, image),
            CapturedAt = capturedAt,
            SourceApp = sourceApp,
            SourcePath = sourcePath,
        };

    public static Clip FromFiles(IEnumerable<string> files, DateTime capturedAt, string? sourceApp, string? sourcePath)
    {
        var list = files.ToList();
        // Windows paths are case-insensitive, so "C:\A.txt" and "c:\a.txt" are the same file list.
        var key = string.Join('\n', list.Select(f => f.ToUpperInvariant()));
        return new()
        {
            Kind = ClipKind.Files,
            Files = list,
            ContentHash = Hash(ClipKind.Files, Encoding.UTF8.GetBytes(key)),
            CapturedAt = capturedAt,
            SourceApp = sourceApp,
            SourcePath = sourcePath,
        };
    }

    /// <summary>A detached copy of the stored fields (no image bytes), for writing the history off the UI thread.</summary>
    internal Clip CopyForStorage()
    {
        var copy = (Clip)MemberwiseClone();
        copy.Files = [.. Files];
        copy.Image = null;
        return copy;
    }

    private static string Hash(ClipKind kind, byte[] content)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        sha.AppendData([(byte)kind]);
        sha.AppendData(content);
        return Convert.ToHexString(sha.GetHashAndReset());
    }
}
