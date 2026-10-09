using Pollux.App.Views;

namespace Pollux.App.Services;

/// <summary>Palette commands of the Clipboard History. The capture service registers its handlers here; the Clipboard Stack opens through the App.</summary>
internal static class ClipboardCommands
{
    /// <summary>Clears the Clipboard History (Kept Clips stay).</summary>
    public static Action? ClearHistory { get; set; }

    /// <summary>Pauses or resumes capture; null until the capture service runs.</summary>
    public static Action<bool>? SetPaused { get; set; }

    public static bool Execute(string id, MainWindow window)
    {
        switch (id)
        {
            // Through the App: while the Clipboard History is off it asks once whether to turn it on.
            case "clipboard.show": App.Instance.ShowClipboardStack(window); return true;
            case "clipboard.clear": Run(ClearHistory, window); return true;
            case "clipboard.pause": Run(SetPaused is null ? null : () => SetPaused(true), window); return true;
            case "clipboard.resume": Run(SetPaused is null ? null : () => SetPaused(false), window); return true;
            default: return false;
        }
    }

    private static void Run(Action? action, MainWindow window)
    {
        if (action is null) window.ShowToast("Clipboard History is off");
        else action();
    }
}
