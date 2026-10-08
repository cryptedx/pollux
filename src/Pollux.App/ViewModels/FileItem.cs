using Microsoft.UI.Xaml;
using Pollux.Core.Files;

namespace Pollux.App.ViewModels;

/// <summary>One row in a file list. Immutable apart from cut state; rebuilt when the folder reloads.</summary>
public sealed partial class FileItem
{
    public FileItem(FileEntry entry, string dateFormat, bool showExtension)
    {
        Entry = entry;
        Name = showExtension || entry.IsDirectory || entry.Extension.Length == 0
            ? entry.Name
            : entry.Name[..^(entry.Extension.Length + 1)];
        ModifiedText = entry.Modified.ToString(dateFormat, System.Globalization.CultureInfo.InvariantCulture);
        TypeText = Format.TypeLabel(entry);
        SizeText = entry.IsDirectory ? "" : Format.Size(entry.Size);
        Badge = Format.Badge(entry);
        CloudGlyph = entry.Cloud switch { CloudState.CloudOnly => "☁", CloudState.AlwaysLocal => "●", CloudState.Local => "○", _ => "" };
    }

    public FileEntry Entry { get; }
    public string Name { get; }
    public string ModifiedText { get; }
    public string TypeText { get; }
    public string SizeText { get; }
    public string Badge { get; }
    public string CloudGlyph { get; }
    public string Path => Entry.FullPath;
    public bool IsDirectory => Entry.IsDirectory;
    public Visibility FolderVisibility => Entry.IsDirectory ? Visibility.Visible : Visibility.Collapsed;
    public Visibility BadgeVisibility => Entry.IsDirectory ? Visibility.Collapsed : Visibility.Visible;
    public double NameOpacity => Entry.IsHidden || Git == Core.Git.GitMark.Ignored ? 0.5 : 1.0;
    public IReadOnlyList<MatchRange> Highlights { get; set; } = [];

    public Core.Git.GitMark Git { get; init; }

    public string GitText => Git switch
    {
        Core.Git.GitMark.Modified => "M",
        Core.Git.GitMark.Added => "A",
        Core.Git.GitMark.Deleted => "D",
        Core.Git.GitMark.Renamed => "R",
        Core.Git.GitMark.Untracked => "U",
        Core.Git.GitMark.Conflict => "!",
        _ => "",
    };

    public Microsoft.UI.Xaml.Media.Brush GitBrush => Services.Theme.Brush(Git switch
    {
        Core.Git.GitMark.Untracked or Core.Git.GitMark.Added => "PolluxGoodBrush",
        Core.Git.GitMark.Conflict or Core.Git.GitMark.Deleted => "PolluxBadBrush",
        _ => "PolluxWarnBrush",
    });
}
