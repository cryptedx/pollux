namespace Pollux.Core.Clipboard;

/// <summary>The one-line label of a Clip in the Clipboard Stack.</summary>
public static class ClipLabel
{
    private const int MaxLength = 200;

    public static string For(Clip clip)
    {
        if (clip.Title is { } title)
        {
            return title;
        }

        return clip.Kind switch
        {
            ClipKind.Text => FirstLine(clip.Text ?? ""),
            ClipKind.Files when clip.Files.Count == 0 => "(no files)",
            ClipKind.Files => clip.Files.Count == 1
                ? ClipSearch.FileName(clip.Files[0])
                : $"{ClipSearch.FileName(clip.Files[0])} + {clip.Files.Count - 1} more",
            _ => "Image",
        };
    }

    private static string FirstLine(string text)
    {
        foreach (var range in text.AsSpan().EnumerateLines())
        {
            var line = range.Trim();
            if (line.IsEmpty)
            {
                continue;
            }

            var cut = line.Length > MaxLength ? string.Concat(line[..MaxLength], "…") : line.ToString();
            return cut.Replace('\t', ' ');
        }

        return "(blank)";
    }
}
