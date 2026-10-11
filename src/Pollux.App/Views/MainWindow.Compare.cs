using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Pollux.App.Services;
using Pollux.Core.Compare;

namespace Pollux.App.Views;

/// <summary>
/// Compare Folders: while it is on, the rows of both Panes carry a marker (only here, newer, older, same, differs) and
/// the status bar a summary; it follows the Panes as they navigate or their folders change. Select Differences picks
/// what copying to the other Pane would bring over; Sync copies it (never deletes) after a confirmation.
/// </summary>
internal sealed partial class MainWindow
{
    private bool _comparing;
    private FolderComparison? _comparison;
    private CancellationTokenSource? _compareCts;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _compareTimer;

    private static CompareSide SideOf(PaneView pane) => pane.Index == 0 ? CompareSide.Left : CompareSide.Right;

    private void HookCompare()
    {
        foreach (var p in _panes) p.ListingChanged += () => { if (_comparing) ScheduleCompare(); };
    }

    private CompareOptions CompareOptionsNow() => Services.Settings.Compare.Options(Services.Settings.Files.ShowHidden);

    /// <summary>The folders of the left and right Pane, or null while there is no second Pane or one of them is on Home.</summary>
    private (string Left, string Right)? ComparedFolders() =>
        _dual && Pane1.Folder is { } left && Pane2.Folder is { } right ? (left, right) : null;

    /// <summary>Settings (recursive, content, hidden files) or the layout changed: compare again if the result no longer fits.</summary>
    private void CompareSettingsChanged()
    {
        if (_comparing && _comparison?.Options != CompareOptionsNow()) ScheduleCompare();
    }

    private void CompareLayoutChanged()
    {
        if (_comparing) ScheduleCompare();
    }

    private void ToggleCompare()
    {
        if (_comparing)
        {
            StopCompare();
            ShowToast("Compare off");
            return;
        }

        if (ComparedFolders() is null)
        {
            ShowToast(_dual ? "Both panes need to show a folder" : "Dual pane is off (Ctrl+<)");
            return;
        }

        _comparing = true;
        _ = RefreshCompareAsync();
    }

    private void StopCompare()
    {
        _comparing = false;
        _compareCts?.Cancel();
        _compareTimer?.Stop();
        _comparison = null;
        Pane1.SetCompare(null, CompareSide.Left);
        Pane2.SetCompare(null, CompareSide.Right);
        CompareText.Visibility = Visibility.Collapsed;
    }

    private void ToggleCompareSetting(bool recursive)
    {
        var s = Services.Settings.Compare;
        if (recursive) s.Recursive = !s.Recursive;
        else s.Content = !s.Content;
        Services.SaveSettings(); // raises SettingsChanged, which compares again if Compare is on
        ShowToast(recursive
            ? (s.Recursive ? "Compare includes subfolders" : "Compare looks at the folders only")
            : (s.Content ? "Compare reads same-size files" : "Compare uses size and date only"));
    }

    private void ScheduleCompare()
    {
        if (_compareTimer is null)
        {
            _compareTimer = DispatcherQueue.CreateTimer();
            _compareTimer.IsRepeating = false;
            _compareTimer.Interval = TimeSpan.FromMilliseconds(250);
            _compareTimer.Tick += (_, _) => _ = RefreshCompareAsync();
        }

        _compareTimer.Stop();
        _compareTimer.Start();
    }

    /// <summary>Compares the Panes' folders off the UI thread and shows the result; null when it was superseded or cannot be done.</summary>
    private async Task<FolderComparison?> RefreshCompareAsync()
    {
        _compareCts?.Cancel();
        var cts = _compareCts = new CancellationTokenSource();
        if (!_comparing || _closed) return null;

        if (ComparedFolders() is not { } folders)
        {
            _comparison = null;
            Pane1.SetCompare(null, CompareSide.Left);
            Pane2.SetCompare(null, CompareSide.Right);
            ShowCompareStatus("⇄ show a folder in both panes");
            return null;
        }

        var (left, right) = folders;
        var options = CompareOptionsNow();
        ShowCompareStatus("⇄ comparing…");
        FolderComparison result;
        try
        {
            result = await Task.Run(() => FolderComparer.Compare(left, right, options, cts.Token), cts.Token);
        }
        catch (OperationCanceledException)
        {
            return null;
        }

        // The Panes moved on meanwhile: their next listing starts a new comparison.
        if (cts.IsCancellationRequested || !_comparing || _closed || ComparedFolders() is not { } now || now != folders) return null;

        _comparison = result;
        Pane1.SetCompare(result, CompareSide.Left);
        Pane2.SetCompare(result, CompareSide.Right);
        var o = result.Options;
        ShowCompareStatus("⇄ " + result.Summary.Text + (o.Recursive ? " · with subfolders" : "") + (o.CompareContent ? " · by content" : ""));
        return result;
    }

    private void ShowCompareStatus(string text)
    {
        CompareText.Text = text;
        CompareText.Visibility = Visibility.Visible;
    }

    /// <summary>Selects what F5 would copy usefully from the active Pane: entries only here, newer here or different.</summary>
    private async Task SelectDifferencesAsync()
    {
        if (ComparedFolders() is not { } folders)
        {
            ShowToast(_dual ? "Both panes need to show a folder" : "Dual pane is off (Ctrl+<)");
            return;
        }

        var (left, right) = folders;

        var current = _comparing && _comparison is { } c && c.LeftFolder == left && c.RightFolder == right && c.Options == CompareOptionsNow() ? c : null;
        if (current is null)
        {
            _comparing = true;
            current = await RefreshCompareAsync();
            if (current is null) return;
        }

        var pane = Active;
        var selected = pane.SelectNames(current.SelectableNames(SideOf(pane)));
        ShowToast(selected == 0 ? "No differences to copy from this pane" : $"Selected {selected} item(s) that differ — F5 copies them");
    }

    /// <summary>One-way sync from the active Pane to the other: copies what is new or newer here through the transfer queue, after a confirmation. Never deletes.</summary>
    private async Task SyncToOtherAsync()
    {
        if (ComparedFolders() is not { } folders)
        {
            ShowToast(_dual ? "Both panes need to show a folder" : "Dual pane is off (Ctrl+<)");
            return;
        }

        var (left, right) = folders;

        if (string.Equals(left, right, StringComparison.OrdinalIgnoreCase))
        {
            ShowToast("Both panes show the same folder");
            return;
        }

        ShowToast("Comparing…");
        var options = CompareOptionsNow();
        var comparison = await Task.Run(() => FolderComparer.Compare(left, right, options));
        if (_closed) return;

        var side = SideOf(Active);
        var plan = FolderSync.Plan(comparison, side);
        if (plan.IsEmpty)
        {
            ShowToast(plan.Clashes > 0 ? $"Nothing to copy ({plan.Clashes} name(s) are a file on one side and a folder on the other)" : "Nothing to copy: nothing is new or newer in this pane");
            return;
        }

        var text = new TextBlock { Text = plan.ConfirmText, FontFamily = Mono, FontSize = 12, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        var dialog = new ContentDialog
        {
            Title = "Sync to other pane",
            Content = new ScrollViewer { Content = text, MaxHeight = 420 },
            PrimaryButtonText = "Copy",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot,
            RequestedTheme = Theme.Current,
        };
        var result = await dialog.ShowAsync();
        Active.FocusList();
        if (result != ContentDialogResult.Primary) return;

        foreach (var request in plan.ToRequests()) _app.Transfers.Enqueue(request, AskConflictAsync);
    }
}
