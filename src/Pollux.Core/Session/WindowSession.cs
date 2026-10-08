namespace Pollux.Core.Session;

public sealed class TabSession
{
    /// <summary>Folder path or <see cref="Navigation.WinPath.Home"/>.</summary>
    public string Location { get; set; } = Navigation.WinPath.Home;
}

public sealed class PaneSession
{
    public List<TabSession> Tabs { get; set; } = [new()];
    public int ActiveTab { get; set; }
}

/// <summary>Everything needed to reopen a window the way it was left.</summary>
public sealed class WindowSession
{
    public List<PaneSession> Panes { get; set; } = [new(), new()];
    public int ActivePane { get; set; }
    public bool DualPane { get; set; } = true;
    public bool SidebarVisible { get; set; } = true;
    public int X { get; set; } = -1;
    public int Y { get; set; } = -1;
    public int Width { get; set; } = 1280;
    public int Height { get; set; } = 800;
    public bool Maximized { get; set; }

    /// <summary>Name of the Workspace this window shows, if any.</summary>
    public string? Workspace { get; set; }
}

public sealed class SessionState
{
    public List<WindowSession> Windows { get; set; } = [];
}
