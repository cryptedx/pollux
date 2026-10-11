using System.Text;

namespace Pollux.Core.Operations;

/// <summary>
/// The zone mark (Mark-of-the-Web): the <c>Zone.Identifier</c> alternate data stream Windows attaches to downloaded
/// files so SmartScreen and Office Protected View treat them as coming from the internet.
/// Reading and writing is a no-op off Windows (a ':' there would create a real file) and on volumes without
/// alternate data streams (FAT, exFAT, some network shares).
/// </summary>
public static class ZoneMark
{
    private const string StreamSuffix = ":Zone.Identifier";

    // Latin-1 maps every byte to one char and back, so the stream is copied byte for byte, never rewritten.
    private static readonly Encoding Raw = Encoding.Latin1;

    /// <summary>The raw zone stream of <paramref name="path"/>, or null when it has none (or zone marks don't exist here).</summary>
    public static string? Read(string path)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            return File.ReadAllText(path + StreamSuffix, Raw);
        }
        catch (Exception ex) when (IsUnsupportedOrMissing(ex))
        {
            return null;
        }
    }

    /// <summary>Writes <paramref name="content"/> as the zone stream of <paramref name="path"/>; false when that is not possible here.</summary>
    public static bool Write(string path, string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            File.WriteAllText(path + StreamSuffix, content, Raw);
            return true;
        }
        catch (Exception ex) when (IsUnsupportedOrMissing(ex))
        {
            return false;
        }
    }

    /// <summary>Copies the zone stream of <paramref name="source"/> onto <paramref name="target"/>, if the source has one.</summary>
    public static void Copy(string source, string target)
    {
        if (Read(source) is { } mark) Write(target, mark);
    }

    private static bool IsUnsupportedOrMissing(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException;
}
