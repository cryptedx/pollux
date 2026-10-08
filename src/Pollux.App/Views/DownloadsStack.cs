using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Pollux.App.Services;
using Pollux.Core.Files;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;

namespace Pollux.App.Views;

/// <summary>
/// The Downloads Stack: a compact popup above the tray listing the newest files in Downloads (like a macOS Dock stack).
/// Kept alive and hidden between uses so it opens instantly.
/// </summary>
internal sealed partial class DownloadsStack : Window
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase) { "png", "jpg", "jpeg", "gif", "bmp", "webp" };
    private readonly App _app;
    private readonly Grid _root = new();
    private readonly GridView _gridView = new() { SelectionMode = ListViewSelectionMode.Single, IsItemClickEnabled = true, CanDragItems = true, Padding = new Thickness(8) };
    private readonly ListView _listView = new() { SelectionMode = ListViewSelectionMode.Single, IsItemClickEnabled = true, CanDragItems = true, Padding = new Thickness(8) };
    private ListViewBase _grid;
    private readonly TextBlock _title = new() { Text = "Downloads", FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _count = new() { FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
    private List<FileEntry> _entries = [];
    private FileSystemWatcher? _watcher;
    private bool _dirty = true;
    private DateTime _hiddenByDeactivation;

    public DownloadsStack(App app)
    {
        _app = app;
        _grid = _gridView;
        Title = "Pollux Downloads";
        SystemBackdrop = new DesktopAcrylicBackdrop();
        if (AppWindow.Presenter is OverlappedPresenter p)
        {
            p.IsAlwaysOnTop = true;
            p.IsResizable = false;
            p.IsMaximizable = false;
            p.IsMinimizable = false;
            p.SetBorderAndTitleBar(true, false);
        }

        AppWindow.IsShownInSwitchers = false;
        Build();
        Content = _root;
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
        Watch();
    }

    public static string Folder => KnownFolders.Downloads;

    public void Toggle()
    {
        // Clicking the tray icon first deactivates (and hides) the stack; that click means "close", not "reopen".
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
        _root.RequestedTheme = Theme.Current;
        if (_dirty) Reload();
        Place();
        AppWindow.Show(true);
        Activate();
        Interop.Native.SetForegroundWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
        _grid.Focus(FocusState.Keyboard);
        if (_grid.Items.Count > 0) _grid.SelectedIndex = 0;
    }

    private void Place()
    {
        var area = DisplayArea.Primary.WorkArea;
        var scale = Interop.Native.GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
        var grid = _app.Services.Settings.Downloads.GridView;
        var w = (int)((grid ? 520 : 440) * scale);
        var h = (int)(560 * scale);
        AppWindow.MoveAndResize(new RectInt32(area.X + area.Width - w - (int)(12 * scale), area.Y + area.Height - h - (int)(12 * scale), w, h));
    }

    private void Build()
    {
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new Grid { Padding = new Thickness(16, 12, 10, 6) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(_title);
        _count.FontFamily = Ui.Mono;
        Grid.SetColumn(_count, 1);
        header.Children.Add(_count);
        var toggle = new Button { Content = "▦ / ☰", Style = Ui.FlatButton, FontFamily = Ui.Mono, FontSize = 11 };
        ToolTipService.SetToolTip(toggle, "Grid / list");
        toggle.Click += (_, _) =>
        {
            _app.Services.Settings.Downloads.GridView = !_app.Services.Settings.Downloads.GridView;
            _app.Services.SaveSettings();
            Render();
            Place();
        };
        Grid.SetColumn(toggle, 2);
        header.Children.Add(toggle);
        _root.Children.Add(header);

        foreach (var view in new ListViewBase[] { _gridView, _listView })
        {
            Grid.SetRow(view, 1);
            Wire(view);
            _root.Children.Add(view);
        }



        var footer = new Grid { Padding = new Thickness(12, 6, 12, 10) };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var hint = new TextBlock { Text = "Enter open · Ctrl+Enter reveal · Del · drag out", FontFamily = Ui.Mono, FontSize = 10.5, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.7 };
        footer.Children.Add(hint);
        var openFolder = new Button { Content = "Open in Pollux", FontSize = 12 };
        openFolder.Click += (_, _) => { AppWindow.Hide(); _app.OpenNewWindow(Folder); };
        Grid.SetColumn(openFolder, 1);
        footer.Children.Add(openFolder);
        Grid.SetRow(footer, 2);
        _root.Children.Add(footer);
    }

    private void Wire(ListViewBase view)
    {
        view.ItemClick += (_, e) => { if (e.ClickedItem is FrameworkElement fe && fe.Tag is string path) Open(path); };
        view.KeyDown += Grid_KeyDown;
        view.DragItemsStarting += (_, e) =>
        {
            var paths = e.Items.OfType<FrameworkElement>().Select(f => f.Tag as string).OfType<string>().ToList();
            e.Data.RequestedOperation = DataPackageOperation.Copy | DataPackageOperation.Move;
            e.Data.SetDataProvider(StandardDataFormats.StorageItems, async request =>
            {
                var d = request.GetDeferral();
                try { request.SetData(await PaneView.StorageItemsFor(paths)); }
                finally { d.Complete(); }
            });
        };
    }

    private void Grid_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        var path = (_grid.SelectedItem as FrameworkElement)?.Tag as string;
        switch (e.Key)
        {
            case Windows.System.VirtualKey.Escape:
                e.Handled = true;
                AppWindow.Hide();
                break;
            case Windows.System.VirtualKey.Enter when path is not null:
                e.Handled = true;
                if (KeyInput.IsDown(Windows.System.VirtualKey.Control)) Reveal(path);
                else Open(path);
                break;
            case Windows.System.VirtualKey.Delete when path is not null:
                e.Handled = true;
                Shell.Delete(WinRT.Interop.WindowNative.GetWindowHandle(this), [path], permanent: false, confirm: false);
                break;
        }
    }

    private void Open(string path)
    {
        AppWindow.Hide();
        Shell.Open(path);
    }

    private void Reveal(string path)
    {
        AppWindow.Hide();
        _app.OpenNewWindow().Reveal(path);
    }

    private void Watch()
    {
        try
        {
            _watcher = new FileSystemWatcher(Folder)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size,
                EnableRaisingEvents = true,
            };
            FileSystemEventHandler h = (_, _) => DispatcherQueue.TryEnqueue(() =>
            {
                _dirty = true;
                if (AppWindow.IsVisible) Reload();
            });
            _watcher.Created += h;
            _watcher.Deleted += h;
            _watcher.Renamed += (_, _) => DispatcherQueue.TryEnqueue(() => { _dirty = true; if (AppWindow.IsVisible) Reload(); });
        }
        catch (Exception ex) when (ex is ArgumentException or IOException)
        {
            _watcher = null;
        }
    }

    private void Reload()
    {
        _dirty = false;
        try
        {
            _entries = DirectoryReader.Enumerate(Folder)
                .Where(e => !e.IsHidden && !e.Name.EndsWith(".crdownload", StringComparison.OrdinalIgnoreCase) && !e.Name.EndsWith(".part", StringComparison.OrdinalIgnoreCase) && !e.Name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(e => e.Modified)
                .Take(_app.Services.Settings.Downloads.MaxItems)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _entries = [];
        }

        Render();
    }

    private void Render()
    {
        var grid = _app.Services.Settings.Downloads.GridView;
        _count.Text = $"{_entries.Count} newest";
        _count.Foreground = Theme.Brush("PolluxMutedBrush");
        _grid = grid ? _gridView : _listView;
        _gridView.Visibility = grid ? Visibility.Visible : Visibility.Collapsed;
        _listView.Visibility = grid ? Visibility.Collapsed : Visibility.Visible;
        ((ListViewBase)(grid ? _listView : _gridView)).ItemsSource = null;
        var items = new List<FrameworkElement>(_entries.Count);
        foreach (var e in _entries) items.Add(grid ? Tile(e) : Row(e));
        _grid.ItemsSource = items;
    }

    private FrameworkElement Tile(FileEntry e)
    {
        var panel = new StackPanel { Width = 104, Spacing = 4, Padding = new Thickness(4), Tag = e.FullPath };
        panel.Children.Add(Preview(e, 72));
        panel.Children.Add(new TextBlock { Text = e.Name, FontSize = 11.5, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis, Height = 32 });
        panel.Children.Add(new TextBlock { Text = Format.Relative(e.Modified, DateTime.Now), FontFamily = Ui.Mono, FontSize = 10, TextAlignment = TextAlignment.Center, Foreground = Theme.Brush("PolluxMutedBrush") });
        ToolTipService.SetToolTip(panel, $"{e.Name}\n{(e.IsDirectory ? "Folder" : Format.Size(e.Size))} · {e.Modified:yyyy-MM-dd HH:mm}");
        return panel;
    }

    private FrameworkElement Row(FileEntry e)
    {
        var g = new Grid { ColumnSpacing = 10, Width = 400, Tag = e.FullPath, Padding = new Thickness(2) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.Children.Add(Preview(e, 28));
        var name = new TextBlock { Text = e.Name, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        Grid.SetColumn(name, 1);
        g.Children.Add(name);
        var meta = new TextBlock { Text = $"{(e.IsDirectory ? "" : Format.Size(e.Size))}  {Format.Relative(e.Modified, DateTime.Now)}", FontFamily = Ui.Mono, FontSize = 10.5, VerticalAlignment = VerticalAlignment.Center, Foreground = Theme.Brush("PolluxMutedBrush") };
        Grid.SetColumn(meta, 2);
        g.Children.Add(meta);
        return g;
    }

    private static FrameworkElement Preview(FileEntry e, double size)
    {
        if (!e.IsDirectory && ImageExtensions.Contains(e.Extension) && e.Size < 30_000_000)
        {
            try
            {
                var bmp = new BitmapImage { DecodePixelWidth = (int)(size * 2), UriSource = new Uri(e.FullPath) };
                return new Border { Width = size, Height = size, CornerRadius = new CornerRadius(4), HorizontalAlignment = HorizontalAlignment.Center, Child = new Image { Source = bmp, Stretch = Stretch.UniformToFill } };
            }
            catch (UriFormatException)
            {
                // fall through to badge
            }
        }

        if (e.IsDirectory)
        {
            return new Border { Width = size * 0.8, Height = size * 0.6, Margin = new Thickness(0, size * 0.15, 0, size * 0.05), CornerRadius = new CornerRadius(3, 6, 4, 4), Background = Theme.Brush("PolluxFolderBrush"), HorizontalAlignment = HorizontalAlignment.Center };
        }

        var ext = e.Extension.Length == 0 ? "FILE" : e.Extension.ToUpperInvariant();
        return new Border
        {
            Width = size * 0.75,
            Height = size,
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1),
            BorderBrush = Theme.Brush("PolluxLineBrush"),
            Background = Theme.Brush("PolluxSurface2Brush"),
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = new TextBlock
            {
                Text = ext.Length > 4 ? ext[..4] : ext,
                FontFamily = Ui.Mono,
                FontSize = Math.Max(8, size / 6),
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = AccentFor(ext),
            },
        };
    }

    private static Brush AccentFor(string ext) => ext switch
    {
        "PDF" => new SolidColorBrush(ColorHelper.FromArgb(255, 0xE5, 0x48, 0x3E)),
        "ZIP" or "7Z" or "RAR" or "GZ" or "TAR" => new SolidColorBrush(ColorHelper.FromArgb(255, 0xD9, 0xA2, 0x2C)),
        "EXE" or "MSI" or "MSIX" => new SolidColorBrush(ColorHelper.FromArgb(255, 0x4C, 0xC2, 0xFF)),
        "DOCX" or "DOC" => new SolidColorBrush(ColorHelper.FromArgb(255, 0x2B, 0x7C, 0xD3)),
        "XLSX" or "XLS" or "CSV" => new SolidColorBrush(ColorHelper.FromArgb(255, 0x21, 0xA3, 0x66)),
        _ => Theme.Brush("PolluxMutedBrush"),
    };
}
