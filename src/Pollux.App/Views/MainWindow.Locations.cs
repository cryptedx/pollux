using Microsoft.UI.Xaml.Controls;
using Pollux.App.Interop;
using Pollux.App.Services;
using Pollux.App.ViewModels;
using Pollux.Core.Archives;
using Pollux.Core.Files;
using Pollux.Core.Navigation;
using Windows.ApplicationModel.DataTransfer;

namespace Pollux.App.Views;

/// <summary>
/// Locations beyond folders in the window (ADR 0010): the This PC and Network sidebar rows, copying items out of zips
/// and shell folders, and Properties, Open with, the Windows menu and previews for items that have no file of their own.
/// </summary>
internal sealed partial class MainWindow
{
    /// <summary>This PC and Network under the Recycle Bin. Neither lists anything until it is clicked (ADR 0010).</summary>
    private void AddLocationRows(string current)
    {
        foreach (var (name, location, glyph) in new[] { ("This PC", WinPath.ThisPc, "🖥"), ("Network", WinPath.Network, "🖧") })
        {
            var row = NavRow(name, location, current, glyph, "");
            ToolTipService.SetToolTip(row, name);
            var open = new MenuFlyoutItem { Text = "Open in other pane" };
            open.Click += (_, _) => OpenInOtherPane(location);
            var newTab = new MenuFlyoutItem { Text = "Open in new tab" };
            newTab.Click += (_, _) => Active.NewTab(location);
            var menu = new MenuFlyout();
            menu.Items.Add(open);
            menu.Items.Add(newTab);
            row.ContextFlyout = menu;
            SidebarPanel.Children.Add(row);
        }
    }

    public void CopyOut(IReadOnlyList<string> sources, string targetDirectory) => _ = CopyOutAsync(sources, targetDirectory);

    public bool ConfirmExtractLimit(ExtractLimitBreach breach) => AskExtractLimitAsync(breach).GetAwaiter().GetResult();

    /// <summary>Extracts zip entries, or copies shell items with Windows' own dialog, into a folder; no undo, like an extraction.</summary>
    private async Task CopyOutAsync(IReadOnlyList<string> sources, string targetDirectory)
    {
        if (sources.Count == 0) return;
        var zip = Locations.Classify(sources[0]) == LocationKind.Zip;
        if (zip) ShowToast("Extracting…", 60);
        var error = await VirtualLocations.CopyOutAsync(Hwnd, sources, targetDirectory, ConfirmExtractLimit);
        if (_closed) return;
        ShowToast(error ?? $"{(zip ? "Extracted" : "Copied")} {sources.Count} item(s) to {WinPath.Name(targetDirectory)}", error is null ? 3 : 6);
        foreach (var p in _panes)
        {
            if (string.Equals(p.Folder, WinPath.Normalize(targetDirectory), StringComparison.OrdinalIgnoreCase)) p.Refresh();
        }
    }

    /// <summary>F5 from a zip or a shell folder: extracted or copied into the other Pane's folder.</summary>
    private void CopyOutToOther(PaneView pane)
    {
        var paths = pane.SelectedPaths;
        if (paths.Count == 0) return;
        if (Other.Folder is not { } target)
        {
            ShowToast(_dual ? "Other pane shows no folder" : "Dual pane is off (Ctrl+<)");
            return;
        }

        CopyOut(paths, target);
    }

    /// <summary>Ctrl+C on items without a file of their own: copied to the temp folder first, so any app can paste them.</summary>
    private async Task CopyOutToClipboardAsync(PaneView pane)
    {
        var paths = pane.SelectedPaths;
        if (paths.Count == 0) return;
        ShowToast("Preparing copy…", 60);
        var copies = await VirtualLocations.MaterializeAsync(Hwnd, paths, ConfirmExtractLimit);
        if (_closed) return;
        if (copies.Count == 0)
        {
            ShowToast("Nothing was copied", 5);
            return;
        }

        var data = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
        data.SetStorageItems(await PaneView.StorageItemsFor(copies));
        data.SetText(string.Join(Environment.NewLine, paths));
        Clipboard.SetContent(data);
        Clipboard.Flush();
        ShowToast($"Copied {paths.Count} item(s)");
    }

    /// <summary>Open with: a file in a zip is extracted first; a shell item without a file cannot be handed to another app.</summary>
    private async Task OpenWithAsync(PaneView pane)
    {
        var sel = pane.SelectedItems;
        if (sel.Count != 1 || sel[0].IsDirectory) return;
        var path = sel[0].Path;
        if (!pane.IsCopyOutOnly([path]))
        {
            Shell.OpenWith(path);
            return;
        }

        if (pane.Tab.Kind != LocationKind.Zip)
        {
            ShowToast(LocationCommands.Unavailable);
            return;
        }

        try
        {
            Shell.OpenWith(await Task.Run(() => VirtualLocations.Reader.Zips.ExtractForOpen(path, VirtualLocations.TempFolder, ConfirmExtractLimit)));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ExtractLimitExceededException)
        {
            ShowToast(ex.Message, 6);
        }
    }

    /// <summary>Properties of the selection or the shown location: the shell's own sheet for shell items, the archive's for a zip.</summary>
    private void ShowProperties(PaneView pane)
    {
        var path = pane.SelectedPaths.FirstOrDefault() ?? pane.Folder ?? pane.Tab.Location;
        if (Locations.IsShell(path))
        {
            _ = InvokeShellVerbAsync(path, "properties");
            return;
        }

        if (ZipFolder.TryParse(path, out var archive, out var inner) && Locations.Classify(path) == LocationKind.Zip)
        {
            if (inner.Length > 0 && pane.SelectedPaths.Count > 0)
            {
                ShowToast("Properties of items inside a zip are not available");
                return;
            }

            path = archive;
        }

        if (path == WinPath.Home || path == WinPath.RecycleBin) return;
        Shell.ShowProperties(Hwnd, path);
    }

    private async Task InvokeShellVerbAsync(string location, string verb)
    {
        if (!await ShellNamespace.InvokeVerbAsync(Hwnd, Locations.ParsingName(location), verb) && !_closed) ShowToast(LocationCommands.Unavailable);
    }

    /// <summary>The full Windows menu for items of a shell folder or a server (by parsing name); none inside a zip.</summary>
    private void ShowVirtualShellMenu(PaneView pane)
    {
        if (pane.Tab.Kind == LocationKind.Zip)
        {
            ShowToast(LocationCommands.Unavailable);
            return;
        }

        var paths = pane.SelectedPaths;
        if (paths.Count == 0) paths = [pane.Tab.Location];
        Native.GetCursorPos(out var pt);
        var extended = KeyInput.IsDown(Windows.System.VirtualKey.Shift);
        if (ShellNamespace.ShowContextMenu(Hwnd, paths, pt.X, pt.Y, extended)) pane.Refresh();
    }

    /// <summary>
    /// What a preview shows for an item of the active Pane: the item itself, a file in a zip extracted to the temp folder,
    /// or null for items without a file (shell items, a server's shares) and folders or large files inside a zip.
    /// </summary>
    private static async Task<FileEntry?> PreviewableAsync(PaneView pane, FileItem item)
    {
        var entry = item.Entry;
        switch (pane.Tab.Kind)
        {
            case LocationKind.Server:
                return null;
            case LocationKind.Shell:
                return pane.IsCopyOutOnly([entry.FullPath]) ? null : entry;
            case LocationKind.Zip:
                if (entry.IsDirectory || entry.Size > MaxZipPreviewBytes) return null;
                try
                {
                    var path = await Task.Run(() => VirtualLocations.Reader.Zips.ExtractForOpen(entry.FullPath, VirtualLocations.TempFolder));
                    return entry with { FullPath = path };
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ExtractLimitExceededException)
                {
                    return null;
                }

            default:
                return entry;
        }
    }

    private const long MaxZipPreviewBytes = 64L * 1024 * 1024;

    /// <summary>Shows <paramref name="item"/> in a preview, extracting it first when it lies in a zip; not when <paramref name="stillWanted"/> says the user moved on.</summary>
    private async Task ShowPreviewAsync(PreviewController preview, PaneView pane, FileItem item, Func<bool> stillWanted)
    {
        var entry = pane.Tab.Kind == LocationKind.Folder || pane.Tab.IsRecycleBin ? item.Entry : await PreviewableAsync(pane, item);
        if (_closed || !stillWanted()) return;
        if (entry is null) preview.ShowUnavailable(item.Entry, "No preview for this item");
        else preview.Show(entry);
    }
}
