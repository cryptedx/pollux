using Pollux.Core.Navigation;

namespace Pollux.Core.Session;

/// <summary>A Tab: a location with its own history, Filter and remembered selection.</summary>
/// <param name="isFile">Whether a path is a file, for telling a .zip from a folder named "x.zip"; defaults to <see cref="File.Exists"/>.</param>
public sealed class TabModel(string location, Func<string, bool>? isFile = null)
{
    private string? _kindOf;
    private LocationKind _kind;
    private (string Location, string Name)? _shellName;

    public NavigationHistory History { get; } = new(location);
    public string Location => History.Current;
    public bool IsHome => Location == WinPath.Home;
    public bool IsRecycleBin => Location == WinPath.RecycleBin;

    /// <summary>The kind of <see cref="Location"/>, classified once per location.</summary>
    public LocationKind Kind
    {
        get
        {
            var current = Location;
            if (!ReferenceEquals(_kindOf, current))
            {
                _kind = Locations.Classify(current, isFile);
                _kindOf = current;
            }

            return _kind;
        }
    }

    /// <summary>What the current location allows (paste, open, copy out, filesystem features).</summary>
    public LocationAccess Access => Locations.Access(Kind);

    public string Title =>
        _shellName is { } n && n.Location == Location ? n.Name : Locations.Title(Location) ?? Locations.ParsingName(Location);

    /// <summary>The folder shown, or null where nothing can be created or pasted: Home, the Recycle Bin, zips, servers, shell folders.</summary>
    public string? Folder => Kind == LocationKind.Folder ? Location : null;

    /// <summary>Remembers the shell's display name for a shell location (a phone, a library) as the title while the Tab shows it.</summary>
    public void SetDisplayName(string location, string name) => _shellName = (location, name);

    public string? Filter { get; set; }
    public List<string> SelectedNames { get; set; } = [];
    public string? FocusName { get; set; }

    /// <summary>The Tab's own view mode; null follows the files.viewMode setting.</summary>
    public Files.ViewMode? ViewMode { get; set; }
}
