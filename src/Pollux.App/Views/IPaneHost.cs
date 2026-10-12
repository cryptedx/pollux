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

    /// <summary>Creates a .lnk shortcut to each source in the target folder, named like Explorer does.</summary>
    void CreateShortcuts(IReadOnlyList<string> sources, string targetDirectory);

    /// <summary>Copies items of a zip or shell folder into a folder: extracting, or the shell's copy with its own progress.</summary>
    void CopyOut(IReadOnlyList<string> sources, string targetDirectory);

    /// <summary>Worker thread: asks whether to extract an archive that breaks the zip bomb limits, and waits for the answer.</summary>
    bool ConfirmExtractLimit(Pollux.Core.Archives.ExtractLimitBreach breach);

    /// <summary>Creates a file from a "New" template in the active pane's folder and offers to rename it.</summary>
    void NewFromTemplate(Pollux.Core.ShellNew.ShellNewEntry entry);

    void ShowToast(string text, int seconds = 3);

    /// <summary>Brings the window to the front (Window.Activate).</summary>
    void Activate();
}
