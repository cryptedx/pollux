namespace Pollux.Core.Files;

/// <summary>How a Tab lays out its entries. Stored as a number in session.json, so keep the values.</summary>
public enum ViewMode { Details = 0, Tiles = 1, Icons = 2 }

/// <summary>The decisions behind the view.* mode commands and the thumbnail sizes of each mode.</summary>
public static class ViewModes
{
    /// <summary>True for view.details, view.tiles, view.icons and view.cycleMode.</summary>
    public static bool IsCommand(string id) => ForCommand(id, ViewMode.Details) is not null;

    /// <summary>The mode a command switches a Tab to from <paramref name="current"/>; null if it is no view mode command.</summary>
    public static ViewMode? ForCommand(string id, ViewMode current) => id switch
    {
        "view.details" => ViewMode.Details,
        "view.tiles" => ViewMode.Tiles,
        "view.icons" => ViewMode.Icons,
        "view.cycleMode" => Next(current),
        _ => null,
    };

    public static ViewMode Next(ViewMode mode) => mode switch
    {
        ViewMode.Details => ViewMode.Tiles,
        ViewMode.Tiles => ViewMode.Icons,
        _ => ViewMode.Details,
    };

    /// <summary>A Tab's own mode, or the files.viewMode setting while it has none.</summary>
    public static ViewMode Effective(ViewMode? tab, ViewMode setting) => tab ?? setting;

    public static string Label(ViewMode mode) => mode switch
    {
        ViewMode.Tiles => "Tiles",
        ViewMode.Icons => "Large Icons",
        _ => "Details",
    };

    /// <summary>Edge of the square thumbnail in device-independent pixels; 0 when the mode shows none (Details keeps its badges).</summary>
    public static int ThumbnailSize(ViewMode mode) => mode switch
    {
        ViewMode.Tiles => 48,
        ViewMode.Icons => 96,
        _ => 0,
    };

    /// <summary>Thumbnail edge in physical pixels at a display scale (1.0 = 96 dpi).</summary>
    public static int ThumbnailPixels(ViewMode mode, double scale) =>
        (int)Math.Round(ThumbnailSize(mode) * Math.Max(1.0, scale));
}
