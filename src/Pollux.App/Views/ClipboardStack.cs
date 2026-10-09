using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Pollux.App.Services;
using Pollux.Core.Clipboard;
using Pollux.Core.Files;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Streams;
using Windows.System;

namespace Pollux.App.Views;

/// <summary>
/// The Clipboard Stack: a centered popup to find a Clip in the Clipboard History. Search box focused on open,
/// Kept Clips on top, preview on the right; closes when it loses focus. Kept alive and hidden between uses so it opens instantly.
/// </summary>
internal sealed partial class ClipboardStack : Window
{
    private const int MaxRows = 200;
    private const int MaxPreviewChars = 100_000;
    private readonly ClipboardHistory _history;
    private readonly Func<Clip, byte[]?> _readImage;
    private readonly Action _changed;
    private readonly Grid _root = new();
    private readonly TextBox _search = new() { PlaceholderText = "search Clips…  @app narrows to one app", FontSize = 14, BorderThickness = new Thickness(0), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
    private readonly ListView _list = new() { SelectionMode = ListViewSelectionMode.Single, IsItemClickEnabled = true, CanDragItems = true, Padding = new Thickness(6, 0, 6, 6) };
    private readonly TextBox _titleBox = new() { PlaceholderText = "title of this Kept Clip (F2)", FontSize = 13, Visibility = Visibility.Collapsed };
    private readonly TextBlock _meta = new() { FontSize = 11, TextWrapping = TextWrapping.Wrap };
    private readonly ScrollViewer _preview = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly TextBlock _footer = new() { FontSize = 10.5, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.7, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly Border _frame = new() { CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1), Padding = new Thickness(8) };
    private readonly Dictionary<string, Task<BitmapImage?>> _icons = new(StringComparer.OrdinalIgnoreCase);
    // The shell answers concurrent thumbnail requests from one process with E_PENDING for all but one,
    // so the Stack's rows, which ask for several app icons at once, load them one at a time.
    private static readonly SemaphoreSlim IconGate = new(1, 1);
    private const int E_PENDING = unchecked((int)0x8000000A);
    // File existence per open, probed off the UI thread: File.Exists on an unavailable network path can block for seconds,
    // and rows re-render on every keystroke. Rows and preview show the missing state once the probe completes.
    private readonly Dictionary<string, Task<bool>> _exists = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _hiddenByDeactivation;
    private string? _dragId;
    private Clip? _titleClip;
    private int _previewVersion;

    public ClipboardStack(App app, ClipboardHistory history, Func<Clip, byte[]?> readImage, Action changed)
    {
        _history = history;
        _readImage = readImage;
        _changed = changed;
        Title = "Pollux Clipboard Stack";
        SystemBackdrop = new DesktopAcrylicBackdrop();
        // Own presenter instead of "AppWindow.Presenter is OverlappedPresenter": that type test fails under Native AOT.
        var p = OverlappedPresenter.Create();
        p.IsAlwaysOnTop = true;
        p.IsResizable = false;
        p.IsMaximizable = false;
        p.IsMinimizable = false;
        p.SetBorderAndTitleBar(true, false);
        AppWindow.SetPresenter(p);

        AppWindow.IsShownInSwitchers = false;
        Build();
        Content = _root;
        app.Services.SettingsChanged += () => _root.RequestedTheme = Theme.Current;
        Activated += (_, e) =>
        {
            if (e.WindowActivationState != WindowActivationState.Deactivated || !AppWindow.IsVisible) return;
            AppWindow.Hide();
            _hiddenByDeactivation = DateTime.UtcNow;
        };
        AppWindow.Closing += (_, e) =>
        {
            e.Cancel = true;
            AppWindow.Hide();
        };
    }

    /// <summary>Enter (false) or Shift+Enter (true: only put the Clip on the clipboard) on a Clip. The Stack is already hidden.</summary>
    public event Action<Clip, bool>? PasteRequested;

    public bool IsOpen => AppWindow.IsVisible;

    public void Toggle()
    {
        // A click elsewhere first deactivates (and hides) the Stack; that click means "close", not "reopen".
        if (AppWindow.IsVisible || (DateTime.UtcNow - _hiddenByDeactivation).TotalMilliseconds < 400)
        {
            AppWindow.Hide();
            _hiddenByDeactivation = default;
            return;
        }

        ShowStack();
    }

    public void ShowStack()
    {
        var requested = Stopwatch.GetTimestamp();
        _root.RequestedTheme = Theme.Current;
        _frame.BorderBrush = Theme.Brush("PolluxLineBrush");
        _frame.Background = Theme.Brush("PolluxSurface2Brush");
        _exists.Clear();
        _search.Text = "";
        Render();
        Place();
        AppWindow.Show(true);
        Activate();
        Interop.Native.SetForegroundWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
        // On the first open the content is not loaded yet and Focus fails: focus the search box once it is.
        if (!_search.Focus(FocusState.Keyboard) && !_search.IsLoaded) _search.Loaded += FocusSearchWhenLoaded;
        StartupLog.Warm("clipboardStack", requested);
    }

    private void FocusSearchWhenLoaded(object sender, RoutedEventArgs e)
    {
        _search.Loaded -= FocusSearchWhenLoaded;
        _search.Focus(FocusState.Keyboard);
    }

    /// <summary>Re-renders after the Clipboard History changed elsewhere (a new Clip while the Stack is open).</summary>
    public void Refresh()
    {
        if (AppWindow.IsVisible) Render(SelectedClip?.Id);
    }

    private Clip? SelectedClip => (_list.SelectedItem as FrameworkElement)?.Tag as Clip;

    private void Place()
    {
        Interop.Native.GetCursorPos(out var cursor);
        var area = DisplayArea.GetFromPoint(new PointInt32(cursor.X, cursor.Y), DisplayAreaFallback.Primary).WorkArea;
        var scale = Interop.Native.GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
        var w = Math.Min((int)(820 * scale), area.Width);
        var h = Math.Min((int)(520 * scale), area.Height);
        AppWindow.MoveAndResize(new RectInt32(area.X + ((area.Width - w) / 2), area.Y + ((area.Height - h) / 2), w, h));
    }

    private void Build()
    {
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new Border { Padding = new Thickness(10, 10, 10, 6) };
        header.Child = _search;
        _root.Children.Add(header);
        _search.TextChanged += (_, _) => Render();
        _search.PreviewKeyDown += Search_PreviewKeyDown;

        var body = new Grid { ColumnSpacing = 8, Padding = new Thickness(0, 0, 10, 0) };
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(5, GridUnitType.Star) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4, GridUnitType.Star) });
        Grid.SetRow(body, 1);
        _root.Children.Add(body);

        // Rows stretch so the relative time sits at the right edge.
        var rowStyle = new Style(typeof(ListViewItem));
        rowStyle.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        _list.ItemContainerStyle = rowStyle;
        body.Children.Add(_list);
        _list.SelectionChanged += (_, _) => ShowPreview();
        _list.ItemClick += (_, e) =>
        {
            _list.SelectedItem = e.ClickedItem;
            _search.Focus(FocusState.Keyboard);
        };
        _list.DragItemsStarting += (_, e) =>
        {
            // Dragging only reorders Kept Clips; nothing leaves the Stack.
            if (e.Items.FirstOrDefault() is not FrameworkElement { Tag: Clip { Kept: true } clip })
            {
                e.Cancel = true;
                return;
            }

            _dragId = clip.Id;
            e.Data.RequestedOperation = DataPackageOperation.Move;
        };
        _list.DragItemsCompleted += (_, _) => _dragId = null;

        var side = new Grid { RowSpacing = 6, Padding = new Thickness(0, 0, 0, 6) };
        side.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        side.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        side.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        side.Children.Add(_titleBox);
        Grid.SetRow(_meta, 1);
        side.Children.Add(_meta);
        _frame.Child = _preview;
        Grid.SetRow(_frame, 2);
        side.Children.Add(_frame);
        Grid.SetColumn(side, 1);
        body.Children.Add(side);
        _titleBox.KeyDown += TitleBox_KeyDown;
        _titleBox.LostFocus += (_, _) => CommitTitle();

        var footer = new Border { Padding = new Thickness(12, 4, 12, 8), Child = _footer };
        _footer.FontFamily = Ui.Mono;
        Grid.SetRow(footer, 2);
        _root.Children.Add(footer);
    }

    private void Render(string? selectId = null)
    {
        var hits = ClipSearch.Find(_history.All, _search.Text);
        var now = DateTime.Now;
        var items = new List<FrameworkElement>();
        var kept = hits.Where(c => c.Kept).ToList();
        var recent = hits.Where(c => !c.Kept).Take(MaxRows).ToList();
        if (kept.Count > 0)
        {
            items.Add(SectionHeader("Kept Clips"));
            foreach (var clip in kept) items.Add(Row(clip, now));
            if (recent.Count > 0) items.Add(SectionHeader("Clipboard History"));
        }

        foreach (var clip in recent) items.Add(Row(clip, now));
        _list.ItemsSource = items;
        var select = items.FindIndex(i => i.Tag is Clip c && (selectId is null || c.Id == selectId));
        if (select < 0) select = items.FindIndex(i => i.Tag is Clip);
        _list.SelectedIndex = select;
        if (select >= 0) _list.ScrollIntoView(items[select]);
        else ShowPreview();
        _footer.Text = $"{hits.Count} Clips · Enter paste · Shift+Enter copy · Ctrl+K keep/release · F2 title · Alt+↑/↓ move · Del delete";
    }

    private static ListViewItem SectionHeader(string text) => new()
    {
        IsEnabled = false,
        IsHitTestVisible = false,
        MinHeight = 0,
        Padding = new Thickness(8, 6, 8, 2),
        Content = new TextBlock { Text = text, FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = Theme.Brush("PolluxMutedBrush") },
    };

    private Grid Row(Clip clip, DateTime now)
    {
        var g = new Grid { ColumnSpacing = 10, Tag = clip, Padding = new Thickness(2), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.Children.Add(AppIcon(clip));
        var label = new TextBlock { Text = (clip.Kept ? "★ " : "") + ClipLabel.For(clip), FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap };
        Grid.SetColumn(label, 1);
        g.Children.Add(label);
        var time = new TextBlock { Text = Format.Relative(clip.CapturedAt, now), FontFamily = Ui.Mono, FontSize = 10.5, VerticalAlignment = VerticalAlignment.Center, Foreground = Theme.Brush("PolluxMutedBrush") };
        Grid.SetColumn(time, 2);
        g.Children.Add(time);
        if (clip.Kind == ClipKind.Files) _ = DimWhenAllMissingAsync(g, clip);
        ToolTipService.SetToolTip(g, $"{clip.SourceApp ?? "unknown app"} · {clip.CapturedAt:yyyy-MM-dd HH:mm}");
        if (clip.Kept)
        {
            g.AllowDrop = true;
            g.DragOver += (_, e) => e.AcceptedOperation = _dragId is not null && _dragId != clip.Id ? DataPackageOperation.Move : DataPackageOperation.None;
            g.Drop += (_, _) =>
            {
                if (_dragId is not { } id) return;
                _dragId = null;
                _history.MoveKept(id, IndexOfKept(clip.Id));
                Changed(id);
            };
        }

        return g;
    }

    private FrameworkElement AppIcon(Clip clip)
    {
        var letter = string.IsNullOrEmpty(clip.SourceApp) ? "?" : clip.SourceApp[..1].ToUpperInvariant();
        var badge = new Border
        {
            Width = 20,
            Height = 20,
            CornerRadius = new CornerRadius(4),
            Background = Theme.Brush("PolluxSurface2Brush"),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = letter, FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Foreground = Theme.Brush("PolluxMutedBrush") },
        };
        if (!string.IsNullOrEmpty(clip.SourcePath)) _ = SetIconAsync(badge, clip.SourcePath);
        return badge;
    }

    private async Task SetIconAsync(Border badge, string exePath)
    {
        if (!_icons.TryGetValue(exePath, out var load))
        {
            load = LoadIconAsync(exePath);
            _icons[exePath] = load;
        }

        var icon = await load;
        if (icon is null)
        {
            // A failed load is not remembered: the next render of the row tries again.
            if (_icons.TryGetValue(exePath, out var cached) && cached == load) _icons.Remove(exePath);
            return;
        }

        badge.Background = null;
        badge.Child = new Image { Source = icon, Width = 20, Height = 20 };
    }

    /// <summary>The icon Explorer shows for the source app's executable; null when it cannot be read.</summary>
    private static async Task<BitmapImage?> LoadIconAsync(string exePath)
    {
        await IconGate.WaitAsync();
        try
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    var file = await StorageFile.GetFileFromPathAsync(exePath);
                    using var thumbnail = await file.GetThumbnailAsync(ThumbnailMode.SingleItem, 32);
                    if (thumbnail is null || thumbnail.Size == 0)
                    {
                        CrashLog.Trace($"clipboard: no icon for {exePath}");
                        return null;
                    }

                    var bitmap = new BitmapImage { DecodePixelWidth = 40 };
                    await bitmap.SetSourceAsync(thumbnail);
                    return bitmap;
                }
                catch (COMException ex) when (ex.HResult == E_PENDING && attempt < 3)
                {
                    await Task.Delay(100 * attempt);
                }
                catch (Exception ex)
                {
                    // Fire-and-forget caller: every failure ends here, logged, and the row keeps its letter.
                    CrashLog.Trace($"clipboard: icon for {exePath} failed: {ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}");
                    return null;
                }
            }
        }
        finally
        {
            IconGate.Release();
        }
    }

    private void ShowPreview()
    {
        var version = ++_previewVersion;
        var clip = SelectedClip;
        CommitTitle(render: false);
        _titleClip = clip;
        _titleBox.Visibility = clip is { Kept: true } ? Visibility.Visible : Visibility.Collapsed;
        _titleBox.Text = clip?.Title ?? "";
        _meta.Foreground = Theme.Brush("PolluxMutedBrush");
        if (clip is null)
        {
            _meta.Text = _history.All.Any() ? "No Clip matches." : "The Clipboard History is empty. Copy something.";
            _preview.Content = null;
            return;
        }

        var from = clip.SourceApp is { Length: > 0 } app ? $"from {app} · " : "";
        switch (clip.Kind)
        {
            case ClipKind.Text:
                var text = clip.Text ?? "";
                _meta.Text = $"{from}{Format.Relative(clip.CapturedAt, DateTime.Now)} · {text.Length:N0} characters";
                _preview.Content = new TextBlock
                {
                    Text = text.Length > MaxPreviewChars ? text[..MaxPreviewChars] + "\n…" : text,
                    FontFamily = Ui.Mono,
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                    IsTextSelectionEnabled = true,
                };
                break;
            case ClipKind.Files:
                var meta = $"{from}{Format.Relative(clip.CapturedAt, DateTime.Now)} · {clip.Files.Count} file(s)";
                _meta.Text = meta;
                var files = new StackPanel { Spacing = 2 };
                var lines = new List<TextBlock>();
                foreach (var path in clip.Files)
                {
                    var line = new TextBlock
                    {
                        Text = path,
                        FontFamily = Ui.Mono,
                        FontSize = 12,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                    };
                    lines.Add(line);
                    files.Children.Add(line);
                }

                _preview.Content = files;
                _ = MarkMissingFilesAsync(clip, lines, meta, version);
                break;
            default:
                _meta.Text = $"{from}{Format.Relative(clip.CapturedAt, DateTime.Now)} · image";
                _preview.Content = null;
                _ = ShowImageAsync(clip, version);
                break;
        }
    }

    private async Task ShowImageAsync(Clip clip, int version)
    {
        try
        {
            // Read and decrypt off the UI thread: a large image must not delay opening the Stack.
            var read = await Task.Run(() => _readImage(clip));
            if (version != _previewVersion || read is not { Length: > 0 } bytes) return;
            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream))
            {
                writer.WriteBytes(bytes);
                await writer.StoreAsync();
                writer.DetachStream();
            }

            stream.Seek(0);
            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(stream);
            if (version != _previewVersion) return;
            // Fit the preview area (the ScrollViewer would otherwise give the image unlimited width), never enlarge.
            _preview.Content = new Image
            {
                Source = bitmap,
                Stretch = Stretch.Uniform,
                MaxWidth = Math.Max(1, Math.Min(_preview.ViewportWidth, bitmap.PixelWidth)),
                MaxHeight = Math.Max(1, Math.Min(_preview.ViewportHeight, bitmap.PixelHeight)),
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException or System.Security.Cryptography.CryptographicException)
        {
            if (version == _previewVersion) _meta.Text += " · cannot be shown";
        }
    }

    private void Search_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var alt = KeyInput.IsDown(VirtualKey.Menu);
        var ctrl = KeyInput.IsDown(VirtualKey.Control);
        var clip = SelectedClip;
        switch (e.Key)
        {
            case VirtualKey.Escape:
                e.Handled = true;
                AppWindow.Hide();
                break;
            case VirtualKey.Up or VirtualKey.Down when alt:
                e.Handled = true;
                if (clip is { Kept: true })
                {
                    _history.MoveKept(clip.Id, IndexOfKept(clip.Id) + (e.Key == VirtualKey.Up ? -1 : 1));
                    Changed(clip.Id);
                }

                break;
            case VirtualKey.Up or VirtualKey.Down:
                e.Handled = true;
                MoveSelection(e.Key == VirtualKey.Up ? -1 : 1);
                break;
            case VirtualKey.Enter when clip is not null:
                e.Handled = true;
                var onlyCopy = KeyInput.IsDown(VirtualKey.Shift);
                AppWindow.Hide();
                PasteRequested?.Invoke(clip, onlyCopy);
                break;
            // Del deletes the Clip unless it would delete a character of the search text.
            case VirtualKey.Delete when clip is not null && _search.SelectionLength == 0 && _search.SelectionStart >= _search.Text.Length:
                e.Handled = true;
                var next = NeighbourId(clip.Id);
                _history.Delete(clip.Id);
                Changed(next);
                break;
            case VirtualKey.K when ctrl && clip is not null:
                e.Handled = true;
                if (clip.Kept) _history.Release(clip.Id, DateTime.Now);
                else _history.Keep(clip.Id);
                Changed(clip.Id);
                break;
            case VirtualKey.F2 when clip is not null:
                e.Handled = true;
                if (!clip.Kept)
                {
                    _history.Keep(clip.Id);
                    Changed(clip.Id);
                }

                _titleBox.Focus(FocusState.Keyboard);
                _titleBox.SelectAll();
                break;
        }
    }

    private void TitleBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Enter:
                e.Handled = true;
                _search.Focus(FocusState.Keyboard);
                break;
            case VirtualKey.Escape:
                e.Handled = true;
                _titleBox.Text = SelectedClip?.Title ?? "";
                _search.Focus(FocusState.Keyboard);
                break;
        }
    }

    /// <summary>Saves an edited title of the Kept Clip shown in the preview (on Enter, focus loss or selecting another Clip).</summary>
    private void CommitTitle(bool render = true)
    {
        if (_titleClip is not { Kept: true } clip || (clip.Title ?? "") == _titleBox.Text.Trim()) return;
        _history.SetTitle(clip.Id, _titleBox.Text);
        _changed();
        if (render) Render(SelectedClip?.Id ?? clip.Id);
    }

    private void MoveSelection(int step)
    {
        if (_list.ItemsSource is not List<FrameworkElement> items || items.Count == 0) return;
        var i = _list.SelectedIndex;
        for (var n = 0; n < items.Count; n++)
        {
            i = (i + step + items.Count) % items.Count;
            if (items[i].Tag is Clip)
            {
                _list.SelectedIndex = i;
                _list.ScrollIntoView(items[i]);
                return;
            }
        }
    }

    /// <summary>The Clip to select after <paramref name="id"/> is deleted: the next one, else the previous one.</summary>
    private string? NeighbourId(string id)
    {
        if (_list.ItemsSource is not List<FrameworkElement> items) return null;
        var clips = items.Select(i => i.Tag).OfType<Clip>().ToList();
        var index = clips.FindIndex(c => c.Id == id);
        if (index + 1 < clips.Count) return clips[index + 1].Id;
        return index > 0 ? clips[index - 1].Id : null;
    }

    private int IndexOfKept(string id)
    {
        for (var i = 0; i < _history.Kept.Count; i++)
        {
            if (_history.Kept[i].Id == id) return i;
        }

        return -1;
    }

    private void Changed(string? selectId)
    {
        _changed();
        Render(selectId);
    }

    private Task<bool> ExistsAsync(string path)
    {
        if (!_exists.TryGetValue(path, out var exists))
        {
            exists = Task.Run(() => File.Exists(path) || Directory.Exists(path));
            _exists[path] = exists;
        }

        return exists;
    }

    /// <summary>Dims a Files row once every file of the Clip turned out to be missing.</summary>
    private async Task DimWhenAllMissingAsync(FrameworkElement row, Clip clip)
    {
        var exists = await Task.WhenAll(clip.Files.Select(ExistsAsync));
        if (!exists.Contains(true)) row.Opacity = 0.45;
    }

    /// <summary>Marks the missing files in the preview (and counts them in the meta line) once the probes complete.</summary>
    private async Task MarkMissingFilesAsync(Clip clip, List<TextBlock> lines, string meta, int version)
    {
        var exists = await Task.WhenAll(clip.Files.Select(ExistsAsync));
        if (version != _previewVersion) return;
        var missing = 0;
        for (var i = 0; i < lines.Count && i < exists.Length; i++)
        {
            if (exists[i]) continue;
            missing++;
            lines[i].Text += "  (missing)";
            lines[i].Opacity = 0.45;
        }

        if (missing > 0) _meta.Text = $"{meta}, {missing} missing";
    }
}
