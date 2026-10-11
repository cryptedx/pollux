using Microsoft.UI.Xaml.Controls;
using Pollux.App.Services;
using Pollux.Core.Navigation;
using Pollux.Core.Recycle;

namespace Pollux.App.Views;

/// <summary>The Recycle Bin as a location: its sidebar row, restore, delete for good and empty.</summary>
internal sealed partial class MainWindow
{
    /// <summary>The sidebar row under Home; its context menu empties the bin from anywhere.</summary>
    private Button RecycleBinRow(string current)
    {
        var row = NavRow("Recycle Bin", WinPath.RecycleBin, current, "♻", "");
        ToolTipService.SetToolTip(row, "Recycle Bin");
        var open = new MenuFlyoutItem { Text = "Open in other pane" };
        open.Click += (_, _) => OpenInOtherPane(WinPath.RecycleBin);
        var empty = new MenuFlyoutItem { Text = "Empty Recycle Bin…" };
        empty.Click += (_, _) => Execute(RecycleBinCommands.Empty);
        var menu = new MenuFlyout();
        menu.Items.Add(open);
        menu.Items.Add(empty);
        row.ContextFlyout = menu;
        return row;
    }

    /// <summary>Puts the selected entries back where they were deleted from (Windows asks on a name clash).</summary>
    private async Task RestoreRecycledAsync()
    {
        var paths = Active.SelectedPaths;
        if (paths.Count == 0) return;
        var restored = await Interop.RecycleBin.RestoreAsync(Hwnd, paths);
        if (_closed) return;
        if (restored > 0) ShowToast("Restored " + RecycleBinCommands.Items(restored));
        _ = RefreshRecycleBinsAsync();
    }

    private async Task DeleteRecycledAsync()
    {
        var selection = Active.SelectedItems;
        if (selection.Count == 0) return;
        if (!await ConfirmAsync("Delete permanently?", RecycleBinCommands.DeleteQuestion([.. selection.Select(i => i.Entry.Name)]), "Delete")) return;
        await Interop.RecycleBin.DeletePermanentlyAsync(Hwnd, [.. selection.Select(i => i.Path)]);
        if (_closed) return;
        _ = RefreshRecycleBinsAsync();
    }

    private async Task EmptyRecycleBinAsync()
    {
        var contents = Interop.RecycleBin.Query();
        if (contents?.Count == 0)
        {
            ShowToast("The Recycle Bin is already empty");
            return;
        }

        if (!await ConfirmAsync("Empty the Recycle Bin?", RecycleBinCommands.EmptyQuestion(contents?.Count, contents?.Bytes ?? 0), "Empty Recycle Bin")) return;
        var emptied = await Interop.RecycleBin.EmptyAsync(Hwnd);
        if (_closed) return;
        if (emptied) ShowToast("Recycle Bin emptied");
        _ = RefreshRecycleBinsAsync();
    }

    /// <summary>Asks before something that cannot be undone; Cancel is the default button.</summary>
    private async Task<bool> ConfirmAsync(string title, string question, string action)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = question,
            PrimaryButtonText = action,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot,
            RequestedTheme = Theme.Current,
        };
        var confirmed = await dialog.ShowAsync() == ContentDialogResult.Primary;
        Active.FocusList();
        return confirmed;
    }

    /// <summary>
    /// Lists the bin again in every Pane showing it. Nothing watches the bin (it spans all drives), and the shell may
    /// finish a restore or delete a moment after the verb returns, so it lists once more shortly after.
    /// </summary>
    private async Task RefreshRecycleBinsAsync()
    {
        RefreshRecycleBins();
        await Task.Delay(1000);
        RefreshRecycleBins();
    }

    private void RefreshRecycleBins()
    {
        foreach (var p in _panes)
        {
            if (p.Tab.IsRecycleBin) p.Refresh();
        }
    }
}
