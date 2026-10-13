using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Web.WebView2.Core;
using Pollux.App.Services;
using Pollux.Core.Files;
using Pollux.Core.Snap;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace Pollux.App.Views;

/// <summary>
/// The Snap Board: marks up a Snap (snap.html on a canvas), then copies, saves or drags the result. Created on the
/// first Snap and kept hidden between Snaps so the next one opens at once (spec: .scratch/snap/spec.md).
/// </summary>
internal sealed partial class SnapBoard : Window
{
    private const int MaxExportBytes = 256 * 1024 * 1024;

    private readonly App _app;
    private readonly Grid _root = new();
    private readonly WebView2 _web = new();
    private readonly TextBlock _status = new() { FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
    private readonly Border _grip = new() { CanDrag = true, Padding = new Thickness(10, 0, 10, 0), CornerRadius = new CornerRadius(4) };
    private CoreWebView2? _core;
    private bool _ready;
    private byte[]? _png;
    private double _scale = 1;
    private string? _savedPath;
    private int _loads;
    private TaskCompletionSource<byte[]?>? _dragExport;

    public SnapBoard(App app)
    {
        _app = app;
        Title = "Snap Board";
        SystemBackdrop = new MicaBackdrop();
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "pollux.ico"));
        Build();
        Content = _root;
        AppWindow.Closing += (_, e) =>
        {
            e.Cancel = true;
            Hide();
        };
        _ = InitWebAsync();
    }

    /// <summary>Shows <paramref name="png"/> (taken at display scale <paramref name="scale"/>, auto-saved to <paramref name="savedPath"/> or not saved).</summary>
    public void Open(byte[] png, double scale, string? savedPath)
    {
        _png = png;
        _scale = scale;
        _savedPath = savedPath;
        _status.Text = "";
        _root.RequestedTheme = Theme.Current;
        _grip.Background = Theme.Brush("PolluxAccentSoftBrush");
        Place(png);
        AppWindow.Show(true);
        Activate();
        Interop.Native.SetForegroundWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
        _web.Focus(FocusState.Programmatic);
        PostLoad();
    }

    private void Hide()
    {
        AppWindow.Hide();
        _png = null;
        _dragExport?.TrySetResult(null);
    }

    // ---------------- layout ----------------

    private void Build()
    {
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(28) });
        _root.Children.Add(_web);

        var strip = new Grid { Padding = new Thickness(10, 3, 6, 3), ColumnSpacing = 10 };
        strip.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        strip.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        strip.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _status.FontFamily = Ui.Mono;
        strip.Children.Add(_status);
        var keys = new TextBlock { Text = "Ctrl+C copy · Ctrl+S save · X text · G backdrop · F float · Esc close", FontSize = 11, FontFamily = Ui.Mono, Opacity = 0.6, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(keys, 1);
        strip.Children.Add(keys);
        _grip.Child = new TextBlock { Text = "⠿ drag file", FontSize = 11, FontFamily = Ui.Mono, VerticalAlignment = VerticalAlignment.Center };
        ToolTipService.SetToolTip(_grip, "Drag the marked-up Snap into a folder or an app");
        _grip.DragStarting += Grip_DragStarting;
        Grid.SetColumn(_grip, 2);
        strip.Children.Add(_grip);
        Grid.SetRow(strip, 1);
        _root.Children.Add(strip);
    }

    /// <summary>Sizes the window to show the Snap at 100 % where it fits, within 90 % of the work area under the cursor.</summary>
    private void Place(byte[] png)
    {
        var (w, h) = SnapService.PngSize(png);
        Interop.Native.GetCursorPos(out var c);
        var area = DisplayArea.GetFromPoint(new PointInt32(c.X, c.Y), DisplayAreaFallback.Primary).WorkArea;
        var dpi = Interop.Native.GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
        // Physical pixels of the image plus rail (44), margins (32), status strip (28) and title bar (32) in this window's DIPs.
        var width = Math.Clamp(w + (int)((44 + 40) * dpi), (int)(640 * dpi), (int)(area.Width * 0.9));
        var height = Math.Clamp(h + (int)((28 + 32 + 40) * dpi), (int)(420 * dpi), (int)(area.Height * 0.9));
        AppWindow.MoveAndResize(new RectInt32(area.X + ((area.Width - width) / 2), area.Y + ((area.Height - height) / 2), width, height));
    }

    // ---------------- web ----------------

    private async Task InitWebAsync()
    {
        try
        {
            _core = await WebHost.InitializeSnapBoardAsync(_web, () => _png);
            _core.WebMessageReceived += Core_WebMessageReceived;
            _core.NavigationCompleted += (_, e) => { if (!e.IsSuccess) CrashLog.Write(new InvalidOperationException($"snap.html navigation failed: {e.WebErrorStatus} {e.HttpStatusCode}")); };
        }
        catch (Exception ex)
        {
            _status.Text = "Snap Board unavailable: " + ex.Message;
            CrashLog.Write(ex);
        }
    }

    private void PostLoad()
    {
        if (!_ready || _png is null) return;
        _loads++;
        Post(WebHost.Json(("type", "load"), ("url", "https://" + WebViewPolicy.SnapHost + "/image?n=" + _loads), ("scale", _scale),
            ("dark", Theme.Current == ElementTheme.Dark), ("accent", Hex(Theme.Accent))));
    }

    private void Core_WebMessageReceived(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        // Only snap.html, parsed defensively (S-16): an export writes the clipboard or a file.
        var message = WebHost.Receive(args, WebViewKind.SnapBoard);
        if (message is null) return;
        switch (message.Type)
        {
            case "ready":
                _ready = true;
                PostLoad();
                break;
            case "status":
                var color = message.Text("color");
                _status.Text = $"{message.Text("size")}   {message.Text("zoom")}{(string.IsNullOrEmpty(color) ? "" : "   " + color)}";
                break;
            case "export":
                _ = ExportAsync(message.Text("action"), message.Bytes("png", MaxExportBytes));
                break;
            case "copyText":
                if (message.Text("text") is { Length: > 0 } copy) _ = CopyTextAsync(copy);
                break;
            case "close":
                Hide();
                break;
            case "error":
                CrashLog.Write(new InvalidOperationException("snap.html: " + message.Text("message")));
                break;
        }
    }

    private async Task ExportAsync(string? action, byte[]? png)
    {
        if (action == "drag")
        {
            _dragExport?.TrySetResult(png);
            return;
        }

        if (png is null) return;
        switch (action)
        {
            case "copy":
                _status.Text = await SnapService.CopyAsync(png) ? "Copied" : "Clipboard is busy";
                break;
            case "save":
                var folder = SnapService.Folder(_app.Services.Settings.Snap);
                var target = SnapSaveTarget.For(_savedPath, folder, DateTime.Now, File.Exists);
                if (await SnapService.WriteAsync(target, png))
                {
                    _savedPath = target;
                    _status.Text = "Saved " + Path.GetFileName(target);
                }
                else _status.Text = "Could not save";
                break;
            case "saveAs":
                await SaveAsAsync(png);
                break;
            case "float":
                await SnapService.OpenFloatAsync(_app, png, _scale, _savedPath);
                break;
            case "ocr":
                var text = await SnapTextRecognition.RecognizeAsync(png, _app.Services.Settings.Snap.OcrLanguage);
                Post(WebHost.Json(("type", "ocrResult"), ("text", text.Text), ("language", text.Language), ("problem", text.Problem)));
                break;
        }
    }

    private async Task CopyTextAsync(string text) =>
        _status.Text = await SnapService.CopyTextAsync(text) ? "Text copied" : "Clipboard is busy";

    private async Task SaveAsAsync(byte[] png)
    {
        var picker = new FileSavePicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary, SuggestedFileName = Path.GetFileNameWithoutExtension(_savedPath) ?? "Snap" };
        picker.FileTypeChoices.Add("PNG image", [".png"]);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var file = await picker.PickSaveFileAsync();
        if (file is null) return;
        if (await SnapService.WriteAsync(file.Path, png))
        {
            _savedPath = file.Path;
            _status.Text = "Saved " + file.Name;
        }
        else _status.Text = "Could not save";
    }

    /// <summary>Asks the page for the marked-up Snap, writes it to a temporary file and drags that file.</summary>
    private async void Grip_DragStarting(UIElement sender, Microsoft.UI.Xaml.DragStartingEventArgs e)
    {
        if (!_ready) { e.Cancel = true; return; }
        var deferral = e.GetDeferral();
        try
        {
            _dragExport = new TaskCompletionSource<byte[]?>();
            Post(WebHost.Json(("type", "export"), ("action", "drag")));
            var png = await _dragExport.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var name = _savedPath is null ? SnapFileName.For(DateTime.Now, _ => false) : Path.GetFileName(_savedPath);
            var path = Path.Combine(Path.GetTempPath(), "Pollux", "Snaps", name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (png is null || !await SnapService.WriteAsync(path, png))
            {
                e.Cancel = true;
                return;
            }

            e.Data.RequestedOperation = DataPackageOperation.Copy;
            e.Data.SetStorageItems([await StorageFile.GetFileFromPathAsync(path)]);
        }
        catch (TimeoutException)
        {
            e.Cancel = true;
        }
        finally
        {
            _dragExport = null;
            deferral.Complete();
        }
    }

    private void Post(string json)
    {
        if (_ready) _core?.PostWebMessageAsString(json);
    }

    private static string Hex(Windows.UI.Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";
}
