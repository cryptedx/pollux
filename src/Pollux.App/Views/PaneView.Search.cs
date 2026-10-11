using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Pollux.App.ViewModels;
using Pollux.Core.Search;
using Pollux.Core.Settings;
using Windows.System;

namespace Pollux.App.Views;

/// <summary>
/// Folder Search in a Pane: a bar below the path, results streamed into the file list with paths relative to the folder.
/// The bar is open while <c>_searchRoot</c> is set; the list shows results once a search ran (<c>_searchHits</c>).
/// Anything that lists the folder again (navigation, Tab switch, closing the bar) ends the search.
/// </summary>
public sealed partial class PaneView
{
    private string? _searchRoot;
    private List<FolderSearchHit>? _searchHits;
    private ObservableCollection<FileItem> _searchItems = [];
    private CancellationTokenSource? _searchCts;

    internal bool IsSearchActive => _searchRoot is not null;

    /// <summary>True while the list shows search results: their names are relative to nothing the folder-based commands know.</summary>
    internal bool HasSearchResults => _searchHits is not null;

    internal bool IsSearchFocused =>
        FocusManager.GetFocusedElement(XamlRoot) is { } f && (ReferenceEquals(f, SearchNameBox) || ReferenceEquals(f, SearchTextBox));

    /// <summary>Opens the Folder Search bar for the current folder, or focuses it again.</summary>
    internal void ShowSearch()
    {
        if (Tab.IsHome) return;
        if (_searchRoot is null)
        {
            RememberSelection();
            // The folder listing stays as it is while the bar is open: a reload would end the search.
            _loadCts?.Cancel();
            _reloadTimer?.Stop();
            StopWatching();
            LoadingBar.Visibility = Visibility.Collapsed;
            Tab.Filter = null;
            ApplyFilterBar();
            _searchRoot = Tab.Location;
            ToolTipService.SetToolTip(SearchNameBox, FolderSearchQuery.Syntax + "\nEnter searches, Esc cancels or closes, Ctrl+Enter shows a result in its folder.");
            SetSearchStatus("", bad: false);
            SearchBar.Visibility = Visibility.Visible;
        }

        SearchNameBox.Focus(FocusState.Keyboard);
        SearchNameBox.SelectAll();
    }

    /// <summary>Esc: stops a running search (its results stay), otherwise closes the bar and lists the folder again.</summary>
    internal void CancelOrCloseSearch()
    {
        if (_searchCts is { } cts) cts.Cancel();
        else if (IsSearchActive) CloseSearch();
    }

    private void CloseSearch()
    {
        EndSearch();
        FocusList();
        _ = ShowCurrentAsync();
    }

    /// <summary>Stops the search and hides its bar without listing the folder (the caller does that).</summary>
    private void EndSearch()
    {
        if (_searchRoot is null) return;
        _searchCts?.Cancel();
        _searchCts = null;
        _searchRoot = null;
        _searchHits = null;
        SearchBar.Visibility = Visibility.Collapsed;
    }

    /// <summary>Ctrl+Enter: lists the selected result's folder with the result selected.</summary>
    internal void RevealSearchResult()
    {
        if (_searchHits is null || SelectedItems.FirstOrDefault() is not { } item) return;
        if (Path.GetDirectoryName(item.Path) is { } parent) Navigate(parent, item.Entry.Name);
    }

    private void SearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Enter:
                e.Handled = true;
                StartSearch();
                break;
            case VirtualKey.Down when _visible.Count > 0:
                e.Handled = true;
                FocusList();
                break;
        }
    }

    private void StartSearch()
    {
        if (_searchRoot is not { } root) return;
        var parsed = FolderSearchQuery.Parse(SearchNameBox.Text, SearchTextBox.Text, DateTime.Now);
        if (parsed.Query is not { } query)
        {
            SetSearchStatus(parsed.Error ?? "", bad: true);
            return;
        }

        _searchCts?.Cancel();
        var cts = _searchCts = new CancellationTokenSource();
        _searchHits = [];
        PresentSearch();
        _ = RunSearchAsync(root, query, cts);
    }

    private async Task RunSearchAsync(string root, FolderSearchQuery query, CancellationTokenSource cts)
    {
        var options = FolderSearchOptions.For(Settings);
        var s = Settings.Files;
        LoadingBar.Visibility = Visibility.Visible;
        SetSearchStatus("searching…", bad: false);
        UpdateSearchEmpty();
        string outcome;
        var bad = false;
        try
        {
            await foreach (var batch in FolderSearch.SearchAsync(root, query, options, TimeSpan.FromMilliseconds(150), cts.Token))
            {
                // A newer search, or the end of this one, replaced the lists: this batch belongs to neither.
                if (_searchCts != cts || _searchHits is not { } hits) return;
                var first = hits.Count == 0;
                foreach (var hit in batch)
                {
                    hits.Add(hit);
                    var item = SearchItem(hit, s);
                    _visible.Add(item);
                    _searchItems.Add(item);
                }

                if (first && _visible.Count > 0) List.SelectedIndex = 0;
                SetSearchStatus($"{hits.Count} found · searching…", bad: false);
                UpdateSearchEmpty();
                _host.OnPaneStateChanged(this);
            }

            var count = _searchHits?.Count ?? 0;
            outcome = count >= options.MaxResults ? $"first {count} results (limit)" : $"{count} found";
        }
        catch (OperationCanceledException)
        {
            outcome = $"{_searchHits?.Count ?? 0} found · cancelled";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            outcome = ex.Message;
            bad = true;
        }

        if (_searchCts != cts) return;
        _searchCts = null;
        cts.Dispose();
        LoadingBar.Visibility = Visibility.Collapsed;
        SetSearchStatus(outcome, bad);
        UpdateSearchEmpty();
        _host.OnPaneStateChanged(this);
    }

    private static FileItem SearchItem(FolderSearchHit hit, FilesSettings s) =>
        new(hit.Entry, s.DateFormat, s.ShowExtensions, displayName: hit.RelativePath) { Highlights = hit.Highlights };

    /// <summary>Lists the results found so far (again), keeping the selection; Tab.FocusName selects that result instead.</summary>
    private void PresentSearch()
    {
        if (_searchHits is not { } hits) return;
        var keep = new HashSet<string>(SelectedPaths, StringComparer.OrdinalIgnoreCase);
        var focus = Tab.FocusName;
        Tab.FocusName = null;
        var s = Settings.Files;
        _visible = [.. hits.Select(h => SearchItem(h, s))];
        _searchItems = new ObservableCollection<FileItem>(_visible);
        List.ItemsSource = _searchItems;
        // Results keep the order they were found in (shallow folders first); the Name column shows the relative path.
        SortName.Content = "Path";
        SortModified.Content = "Modified";
        SortType.Content = "Type";
        SortSize.Content = "Size";

        var selected = focus is not null
            ? _visible.Where(i => string.Equals(i.Entry.Name, focus, StringComparison.OrdinalIgnoreCase)).Take(1).ToList()
            : _visible.Where(i => keep.Contains(i.Path)).ToList();
        foreach (var i in selected) List.SelectedItems.Add(i);
        if (selected.Count > 0) List.ScrollIntoView(selected[0]);
        UpdateSearchEmpty();
        _host.OnPaneStateChanged(this);
    }

    /// <summary>After a delete or rename: drops results that are gone; a running search goes on.</summary>
    private void RefreshSearch()
    {
        _searchHits?.RemoveAll(h => !File.Exists(h.Entry.FullPath) && !Directory.Exists(h.Entry.FullPath));
        PresentSearch();
    }

    private void UpdateSearchEmpty()
    {
        if (_searchHits is null) return;
        EmptyText.Visibility = _visible.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = _searchCts is not null ? "searching…" : "no results";
    }

    private void SetSearchStatus(string text, bool bad)
    {
        SearchStatus.Text = text;
        SearchStatus.Foreground = Res(bad ? "PolluxBadBrush" : "PolluxMutedBrush");
        ToolTipService.SetToolTip(SearchStatus, text.Length > 0 ? text : null);
    }
}
