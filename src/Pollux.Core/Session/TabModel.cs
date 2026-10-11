using Pollux.Core.Navigation;

namespace Pollux.Core.Session;

/// <summary>A Tab: a location with its own history, Filter and remembered selection.</summary>
public sealed class TabModel(string location)
{
    public NavigationHistory History { get; } = new(location);
    public string Location => History.Current;
    public bool IsHome => Location == WinPath.Home;
    public bool IsRecycleBin => Location == WinPath.RecycleBin;
    public string Title => IsHome ? "Home" : IsRecycleBin ? "Recycle Bin" : WinPath.Name(Location);

    /// <summary>The folder shown, or null on Home and in the Recycle Bin (nothing can be created or pasted there).</summary>
    public string? Folder => IsHome || IsRecycleBin ? null : Location;
    public string? Filter { get; set; }
    public List<string> SelectedNames { get; set; } = [];
    public string? FocusName { get; set; }

    /// <summary>The Tab's own view mode; null follows the files.viewMode setting.</summary>
    public Files.ViewMode? ViewMode { get; set; }
}
