using Microsoft.UI.Xaml;
using Pollux.Core.Files;

namespace Pollux.App.ViewModels;

/// <summary>
/// One row in a file list; rebuilt when the folder reloads. Only what changes while a listing stays (a folder's computed
/// size, the compare mark) is observable, so the list can update those rows in place instead of being rebuilt.
/// </summary>
public sealed partial class FileItem : System.ComponentModel.INotifyPropertyChanged
{
    private string _sizeText;
    private bool _folderSizeKnown;
    private Core.Compare.CompareMark _compareState;
    private Core.Compare.CompareSide _compareFrom;

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    private void Raise(params string[] names)
    {
        foreach (var n in names) PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(n));
    }

    /// <param name="displayName">Shown instead of the name, e.g. a Folder Search result's path relative to the search root.</param>
    public FileItem(FileEntry entry, string dateFormat, bool showExtension, OptionalColumns columns = OptionalColumns.None, string? displayName = null, bool folderSizeKnown = false)
    {
        Entry = entry;
        Name = displayName ?? (showExtension || entry.IsDirectory || entry.Extension.Length == 0
            ? entry.Name
            : entry.Name[..^(entry.Extension.Length + 1)]);
        ModifiedText = entry.Modified.ToString(dateFormat, System.Globalization.CultureInfo.InvariantCulture);
        TypeText = Format.TypeLabel(entry);
        _folderSizeKnown = folderSizeKnown;
        _sizeText = entry.IsDirectory && !folderSizeKnown ? "" : Format.Size(entry.Size);
        Badge = Format.Badge(entry);
        CreatedText = entry.Created == default ? "" : entry.Created.ToString(dateFormat, System.Globalization.CultureInfo.InvariantCulture);
        AttributesText = Format.AttributeLetters(entry.Attributes);
        ExtensionText = entry.Extension;
        CreatedVisibility = columns.HasFlag(OptionalColumns.Created) ? Visibility.Visible : Visibility.Collapsed;
        AttributesVisibility = columns.HasFlag(OptionalColumns.Attributes) ? Visibility.Visible : Visibility.Collapsed;
        ExtensionVisibility = columns.HasFlag(OptionalColumns.Extension) ? Visibility.Visible : Visibility.Collapsed;
        CloudGlyph = entry.Cloud switch { CloudState.CloudOnly => "☁", CloudState.AlwaysLocal => "●", CloudState.Local => "○", _ => "" };
        OriginText = entry.OriginalLocation is { Length: > 0 } origin ? "from " + origin : "";
    }

    public FileEntry Entry { get; private set; }
    public string Name { get; }
    public string ModifiedText { get; }
    public string TypeText { get; }
    public string SizeText => _sizeText;

    /// <summary>Gives a folder row its computed size (and nothing else of the row changes). False when it already shows exactly that.</summary>
    public bool SetFolderSize(long bytes)
    {
        if (!Entry.IsDirectory || (_folderSizeKnown && Entry.Size == bytes)) return false;
        Entry = Entry with { Size = bytes };
        _folderSizeKnown = true;
        _sizeText = Format.Size(bytes);
        Raise(nameof(SizeText));
        return true;
    }
    public string Badge { get; }
    public string CreatedText { get; }
    public string AttributesText { get; }
    public string ExtensionText { get; }
    public Visibility CreatedVisibility { get; }
    public Visibility AttributesVisibility { get; }
    public Visibility ExtensionVisibility { get; }
    public string CloudGlyph { get; }

    /// <summary>Where a Recycle Bin entry was deleted from ("from C:\Users\…"); empty for every other entry.</summary>
    public string OriginText { get; }

    public Visibility OriginVisibility => OriginText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    public string Path => Entry.FullPath;
    public bool IsDirectory => Entry.IsDirectory;
    public Visibility FolderVisibility => Entry.IsDirectory ? Visibility.Visible : Visibility.Collapsed;
    public Visibility BadgeVisibility => Entry.IsDirectory ? Visibility.Collapsed : Visibility.Visible;
    public double NameOpacity => Entry.IsHidden || Git == Core.Git.GitMark.Ignored ? 0.5 : 1.0;
    private string? _linkTip;

    /// <summary>Target of a shortcut, symlink or junction as tooltip; null for other entries. Read on first use.</summary>
    public string? LinkTip
    {
        get
        {
            _linkTip ??= Services.LinkTools.Describe(Entry);
            return _linkTip.Length == 0 ? null : _linkTip;
        }
    }

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

    /// <summary>How this row stands against the other Pane's folder while the Panes are being compared.</summary>
    public Core.Compare.CompareMark CompareState
    {
        get => _compareState;
        init => _compareState = value;
    }

    public Core.Compare.CompareSide CompareFrom
    {
        get => _compareFrom;
        init => _compareFrom = value;
    }

    /// <summary>Changes the compare mark of the row in place.</summary>
    public void SetCompare(Core.Compare.CompareMark state, Core.Compare.CompareSide from)
    {
        if (_compareState == state && _compareFrom == from) return;
        _compareState = state;
        _compareFrom = from;
        Raise(nameof(CompareText), nameof(CompareVisibility), nameof(CompareBrush));
    }

    public string CompareText => Core.Compare.CompareMarks.Label(CompareState, CompareFrom);
    public Visibility CompareVisibility => CompareState == Core.Compare.CompareMark.None ? Visibility.Collapsed : Visibility.Visible;

    public Microsoft.UI.Xaml.Media.Brush CompareBrush => Services.Theme.Brush(CompareState switch
    {
        Core.Compare.CompareMark.OnlyHere => "PolluxAccentBrush",
        Core.Compare.CompareMark.Newer => "PolluxGoodBrush",
        Core.Compare.CompareMark.Older => "PolluxWarnBrush",
        Core.Compare.CompareMark.Different => "PolluxBadBrush",
        _ => "PolluxMutedBrush",
    });

    public Microsoft.UI.Xaml.Media.Brush GitBrush => Services.Theme.Brush(Git switch
    {
        Core.Git.GitMark.Untracked or Core.Git.GitMark.Added => "PolluxGoodBrush",
        Core.Git.GitMark.Conflict or Core.Git.GitMark.Deleted => "PolluxBadBrush",
        _ => "PolluxWarnBrush",
    });
}
