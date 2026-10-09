using Pollux.App.Services;

namespace Pollux.App.Views;

/// <summary>What a Pane needs from the window it lives in.</summary>
internal interface IPaneHost
{
    AppServices Services { get; }
    nint Hwnd { get; }

    /// <summary>The window is hidden (pre-created for a resident Pollux): it must not take the keyboard focus.</summary>
    bool IsHidden { get; }
    void Execute(string commandId);
    void OnPaneActivated(PaneView pane);
    void OnPaneStateChanged(PaneView pane);
    void MoveTab(int fromPane, int fromIndex, int toPane, int toIndex);
    void Transfer(bool move, IReadOnlyList<string> sources, string targetDirectory);

    /// <summary>Brings the window to the front (Window.Activate).</summary>
    void Activate();
}
