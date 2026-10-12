using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Pollux.App.Services;
using Pollux.Core.Files;
using Pollux.Core.Navigation;
using Windows.System;

namespace Pollux.App.Views;

/// <summary>The path bar's helpers: the chevron after a crumb with a flyout of its subfolders, and the path box's suggestions.</summary>
public sealed partial class PaneView
{
    private static readonly FileSystemDirectoryLister Lister = new();
    private static readonly TimeSpan VirtualListTimeout = TimeSpan.FromSeconds(2);

    private CancellationTokenSource? _crumbCts;
    private CancellationTokenSource? _suggestCts;
    private CompletionSession? _session;
    private PathCompletionResult? _suggestions;
    private string _typed = "";
    private string? _programmaticText;
    private int _suggestIndex = -1;
    private bool _suggestPointerOver;

    // ---------------- chevrons ----------------

    private static bool HasChildren(LocationKind kind) => kind is LocationKind.Folder or LocationKind.Zip or LocationKind.Server;

    private Button Chevron(string location, string? next)
    {
        var b = new Button
        {
            Content = "›",
            FontFamily = Ui.Mono,
            FontSize = 12,
            Padding = new Thickness(4, 2, 4, 2),
            Style = Ui.FlatButton,
            Foreground = Res("PolluxMutedBrush"),
            IsTabStop = false,
        };
        ToolTipService.SetToolTip(b, "Subfolders");
        b.Click += async (_, _) =>
        {
            _host.OnPaneActivated(this);
            await ShowChildrenAsync(b, location, next);
        };
        return b;
    }

    private async Task ShowChildrenAsync(Button anchor, string location, string? next)
    {
        _crumbCts?.Cancel();
        var cts = _crumbCts = new CancellationTokenSource();
        BreadcrumbChildren children;
        try
        {
            children = await LoadChildrenAsync(location, Settings.Files.ShowHidden, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (cts.IsCancellationRequested || anchor.XamlRoot is null) return;
        var flyout = new MenuFlyout();
        foreach (var child in children.Items)
        {
            var item = new MenuFlyoutItem { Text = child.Name, FontFamily = Ui.Mono, FontSize = 12 };
            if (string.Equals(child.FullPath, next, StringComparison.OrdinalIgnoreCase)) item.FontWeight = FontWeights.SemiBold;
            var target = child.FullPath;
            item.Click += (_, _) => Navigate(target);
            flyout.Items.Add(item);
        }

        if (children.Items.Count == 0) flyout.Items.Add(new MenuFlyoutItem { Text = "No subfolders", IsEnabled = false, FontSize = 12 });
        if (children.More > 0) flyout.Items.Add(new MenuFlyoutItem { Text = $"{children.More} more…", IsEnabled = false, FontSize = 12 });
        flyout.ShowAt(anchor);
    }

    /// <summary>A folder's subfolders; for a zip, a server or This PC what the location reader lists, given up on after a moment.</summary>
    private static async Task<BreadcrumbChildren> LoadChildrenAsync(string location, bool showHidden, CancellationToken ct)
    {
        if (Locations.Classify(location) == LocationKind.Folder)
        {
            return await Task.Run(() => BreadcrumbMenu.Children(location, showHidden, Lister, cancellationToken: ct), ct);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(VirtualListTimeout);
        var found = new List<BreadcrumbChild>();
        try
        {
            await foreach (var batch in VirtualLocations.Reader.ReadAsync(location, 200, timeout.Token))
            {
                foreach (var e in batch)
                {
                    if (!e.IsDirectory || (!showHidden && (e.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0)) continue;
                    found.Add(new BreadcrumbChild(e.Name, e.FullPath));
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return BreadcrumbChildren.None;
        }

        found.Sort((a, b) => EntryComparer.NaturalCompare(a.Name, b.Name));
        var max = BreadcrumbMenu.DefaultMax;
        return new BreadcrumbChildren(found.Take(max).ToList(), Math.Max(0, found.Count - max));
    }

    // ---------------- path box ----------------

    internal void EditPath()
    {
        _host.OnPaneActivated(this);
        CrumbHost.Visibility = Visibility.Collapsed;
        PathBox.Visibility = Visibility.Visible;
        SetPathBoxText(PathBoxText(Tab));
        PathBox.Focus(FocusState.Keyboard);
        PathBox.SelectAll();
    }

    /// <summary>Puts text into the box without starting a new suggestion lookup.</summary>
    private void SetPathBoxText(string text)
    {
        if (PathBox.Text != text)
        {
            _programmaticText = text;
            PathBox.Text = text;
        }

        PathBox.SelectionStart = text.Length;
    }

    private void PathBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (PathBox.Visibility != Visibility.Visible) return;
        if (_programmaticText is { } p)
        {
            _programmaticText = null;
            if (PathBox.Text == p) return;
        }

        _ = SuggestAsync(PathBox.Text, 150);
    }

    /// <summary>Looks up completions off the UI thread and shows them; false when superseded or nothing is shown.</summary>
    private async Task<bool> SuggestAsync(string text, int delayMs)
    {
        _suggestCts?.Cancel();
        var cts = _suggestCts = new CancellationTokenSource();
        _session = null;
        try
        {
            if (delayMs > 0) await Task.Delay(delayMs, cts.Token);
            var dir = Folder ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var hidden = Settings.Files.ShowHidden;
            var result = await Task.Run(() => PathCompletion.Complete(text, dir, hidden, Lister, Environment.GetEnvironmentVariable, cancellationToken: cts.Token), cts.Token);
            if (cts.IsCancellationRequested || PathBox.Visibility != Visibility.Visible || PathBox.Text != text) return false;
            _typed = text;
            _session = new CompletionSession(text, result);
            ShowSuggestions(result);
            return result.Candidates.Count > 0;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private void ShowSuggestions(PathCompletionResult result)
    {
        _suggestions = result;
        _suggestIndex = -1;
        SuggestList.Items.Clear();
        if (result.Candidates.Count == 0)
        {
            SuggestPopup.IsOpen = false;
            return;
        }

        foreach (var c in result.Candidates) SuggestList.Items.Add(SuggestionRow(c));
        if (result.More > 0)
        {
            SuggestList.Items.Add(new ListViewItem
            {
                Content = new TextBlock { Text = $"{result.More} more…", FontSize = 12, Foreground = Res("PolluxMutedBrush"), Margin = new Thickness(24, 0, 0, 0) },
                IsEnabled = false,
                IsTabStop = false,
                AllowFocusOnInteraction = false,
                MinHeight = 28,
            });
        }

        SuggestBorder.Width = Math.Max(160, PathBox.ActualWidth);
        SuggestPopup.VerticalOffset = PathBox.ActualHeight + 2;
        SuggestPopup.IsOpen = true;
    }

    private ListViewItem SuggestionRow(PathCandidate c)
    {
        var glyph = new TextBlock
        {
            Text = c.IsWellKnown ? "" : c.IsDirectory ? "" : "",
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 13,
            Foreground = c.IsDirectory && !c.IsWellKnown ? Res("PolluxFolderBrush") : Res("PolluxMutedBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            Width = 18,
        };
        var label = new TextBlock { Text = c.Display, FontFamily = Ui.Mono, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        row.Children.Add(glyph);
        row.Children.Add(label);
        var item = new ListViewItem { Content = row, IsTabStop = false, AllowFocusOnInteraction = false, MinHeight = 28, Padding = new Thickness(8, 0, 8, 0), Tag = c.Text };
        item.Tapped += (_, e) =>
        {
            e.Handled = true;
            CloseSuggestions();
            SetPathBoxText(c.Text);
            _ = GoToTypedAsync(c.Text);
        };
        return item;
    }

    private void CloseSuggestions()
    {
        _suggestCts?.Cancel();
        SuggestPopup.IsOpen = false;
        _suggestPointerOver = false;
        _session = null;
        _suggestions = null;
        _suggestIndex = -1;
    }

    private void Suggest_PointerEntered(object sender, PointerRoutedEventArgs e) => _suggestPointerOver = true;

    private void Suggest_PointerExited(object sender, PointerRoutedEventArgs e) => _suggestPointerOver = false;

    private void SelectSuggestion(int index)
    {
        _suggestIndex = index;
        SuggestList.SelectedIndex = index;
        if (index >= 0) SuggestList.ScrollIntoView(SuggestList.Items[index]);
    }

    /// <summary>Up/Down in the list: the box shows the highlighted suggestion, past either end the typed text again.</summary>
    private bool MoveSuggestion(int delta)
    {
        if (!SuggestPopup.IsOpen || _suggestions is not { Candidates.Count: > 0 } r) return false;
        var i = _suggestIndex + delta;
        if (i < -1) i = r.Candidates.Count - 1;
        else if (i >= r.Candidates.Count) i = -1;
        SelectSuggestion(i);
        SetPathBoxText(i < 0 ? _typed : r.Candidates[i].Text);
        return true;
    }

    private async Task TabCompleteAsync(bool backwards)
    {
        if (_session is null && !await SuggestAsync(PathBox.Text, 0)) return;
        if (_session?.Tab(backwards) is not { } text) return;
        SetPathBoxText(text);
        var index = _suggestions?.Candidates.ToList().FindIndex(c => c.Text == text) ?? -1;
        SelectSuggestion(index);
    }

    private void PathBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Enter:
                e.Handled = true;
                CloseSuggestions();
                _ = GoToTypedAsync(PathBox.Text);
                break;
            case VirtualKey.Escape:
                e.Handled = true;
                if (SuggestPopup.IsOpen)
                {
                    CloseSuggestions();
                    break;
                }

                RenderCrumbs();
                FocusList();
                break;
            case VirtualKey.Down or VirtualKey.Up:
                if (MoveSuggestion(e.Key == VirtualKey.Down ? 1 : -1)) e.Handled = true;
                else if (e.Key == VirtualKey.Down && PathBox.Text.Length > 0)
                {
                    e.Handled = true;
                    _ = SuggestAsync(PathBox.Text, 0);
                }

                break;
            case VirtualKey.Tab:
                e.Handled = true;
                _ = TabCompleteAsync(KeyInput.IsDown(VirtualKey.Shift));
                break;
            default:
                PathBox.ClearValue(ForegroundProperty);
                break;
        }
    }

    private void PathBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_suggestPointerOver) return;
        CloseSuggestions();
        if (PathBox.Visibility == Visibility.Visible) RenderCrumbs();
    }
}
