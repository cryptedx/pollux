using Pollux.Core.Navigation;

namespace Pollux.Core.Session;

/// <summary>A Tab: a location with its own history, Filter and remembered selection.</summary>
public sealed class TabModel(string location)
{
    public NavigationHistory History { get; } = new(location);
    public string Location => History.Current;
    public bool IsHome => Location == WinPath.Home;
    public string Title => IsHome ? "Home" : WinPath.Name(Location);
    public string? Filter { get; set; }
    public List<string> SelectedNames { get; set; } = [];
    public string? FocusName { get; set; }
}
