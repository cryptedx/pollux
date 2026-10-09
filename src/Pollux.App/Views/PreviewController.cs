using System.Text;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Web.WebView2.Core;
using Pollux.App.Services;
using Pollux.Core.Files;

namespace Pollux.App.Views;

/// <summary>Quick Look: Space shows the selected file (image, text/code via Monaco, PDF via the built-in viewer, hex for binaries).</summary>
internal sealed class PreviewController(Grid host, TextBlock title, TextBlock meta)
{
    private static readonly HashSet<string> Images = new(StringComparer.OrdinalIgnoreCase) { "png", "jpg", "jpeg", "gif", "bmp", "webp", "ico", "tif", "tiff" };
    private static readonly HashSet<string> Browser = new(StringComparer.OrdinalIgnoreCase) { "pdf", "svg", "mp4", "webm", "mp3", "wav", "html", "htm" };

    private WebView2? _codeView;
    private Task<CoreWebView2>? _codeInit;
    private CoreWebView2? _codeCore;
    private bool _codeReady;
    private string? _pendingCode;
    private WebView2? _fileView;
    private Task<CoreWebView2>? _fileInit;

    public string? CurrentPath { get; private set; }

    /// <summary>Narrow side-pane mode: no minimap, wrapped text.</summary>
    public bool Compact { get; init; }

    /// <summary>Text files up to this size are shown as text; larger ones as hex.</summary>
    public long MaxText { get; set; } = 4 * 1024 * 1024;

    public void Show(FileEntry entry)
    {
        CurrentPath = entry.FullPath;
        title.Text = entry.Name;
        meta.Text = entry.IsDirectory ? "Folder" : $"{Format.Size(entry.Size)} · {entry.Modified:yyyy-MM-dd HH:mm} · {Format.TypeLabel(entry)}";
        host.Children.Clear();

        if (entry.IsDirectory)
        {
            _ = ShowFolderAsync(entry.FullPath);
        }
        else if (Images.Contains(entry.Extension))
        {
            host.Children.Add(new Image { Source = new BitmapImage(new Uri(entry.FullPath)), Stretch = Stretch.Uniform, Margin = new Thickness(16) });
        }
        else if (Browser.Contains(entry.Extension))
        {
            _ = ShowInBrowserAsync(entry.FullPath);
        }
        else
        {
            _ = ShowFileAsync(entry);
        }
    }

    /// <summary>Sniffing and hex reading happen off the UI thread, so slow or network files never block arrowing.</summary>
    private async Task ShowFileAsync(FileEntry entry)
    {
        var path = entry.FullPath;
        var isText = entry.Size <= MaxText && await Task.Run(() => LooksLikeText(path));
        if (CurrentPath != path) return;
        if (isText)
        {
            await ShowCodeAsync(entry);
            return;
        }

        var hex = await Task.Run(() => HexText(path));
        if (CurrentPath == path) host.Children.Add(HexView(hex));
    }

    private async Task ShowFolderAsync(string path)
    {
        var text = await Task.Run(() => FolderSummaryText(path));
        if (CurrentPath == path) host.Children.Add(Message(text));
    }

    private async Task ShowCodeAsync(FileEntry entry)
    {
        string text;
        try { text = await File.ReadAllTextAsync(entry.FullPath); }
        catch (IOException ex) { host.Children.Add(Message(ex.Message)); return; }

        if (CurrentPath != entry.FullPath) return; // user already moved on
        if (_codeView is null)
        {
            _codeView = new WebView2();
            host.Children.Add(_codeView);
            // One shared initialization: later calls await the same task instead of racing it.
            _codeInit = InitCodeAsync(_codeView);
        }
        else if (!host.Children.Contains(_codeView))
        {
            host.Children.Add(_codeView);
        }

        var message = WebHost.Json(("type", "show"), ("text", text), ("ext", entry.Extension), ("dark", Theme.Current == ElementTheme.Dark), ("wrap", Compact || entry.Extension is "md" or "txt" or "log"), ("minimap", !Compact));
        _pendingCode = message;
        _codeCore = await _codeInit!;
        if (_codeReady && CurrentPath == entry.FullPath) _codeCore.PostWebMessageAsString(message);
    }

    private async Task<CoreWebView2> InitCodeAsync(WebView2 view)
    {
        var core = await WebHost.InitializeAsync(view, "preview.html");
        core.WebMessageReceived += (_, e) =>
        {
            using var doc = JsonDocument.Parse(e.TryGetWebMessageAsString());
            if (doc.RootElement.GetProperty("type").GetString() != "ready") return;
            _codeReady = true;
            if (_pendingCode is not null) core.PostWebMessageAsString(_pendingCode);
        };
        return core;
    }

    private async Task ShowInBrowserAsync(string path)
    {
        if (_fileView is null)
        {
            _fileView = new WebView2();
            host.Children.Add(_fileView);
            _fileInit = WebHost.InitializeAsync(_fileView, "blank.html");
        }
        else if (!host.Children.Contains(_fileView))
        {
            host.Children.Add(_fileView);
        }

        var core = await _fileInit!;
        if (CurrentPath != path) return; // a newer preview replaced this one
        core.ClearVirtualHostNameToFolderMapping("pollux.preview");
        core.SetVirtualHostNameToFolderMapping("pollux.preview", Path.GetDirectoryName(path)!, CoreWebView2HostResourceAccessKind.Allow);
        core.Navigate(WebHost.PreviewOrigin + Uri.EscapeDataString(Path.GetFileName(path)));
    }

    private static bool LooksLikeText(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            var buf = new byte[Math.Min(8192, fs.Length)];
            var n = fs.Read(buf, 0, buf.Length);
            for (var i = 0; i < n; i++) if (buf[i] == 0) return false;
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static FrameworkElement HexView(string text) => new ScrollViewer
    {
        Padding = new Thickness(16),
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        Content = new TextBlock { Text = text, FontFamily = Ui.Mono, FontSize = 12, IsTextSelectionEnabled = true },
    };

    private static string HexText(string path)
    {
        var sb = new StringBuilder();
        try
        {
            using var fs = File.OpenRead(path);
            var buf = new byte[4096];
            var n = fs.Read(buf, 0, buf.Length);
            for (var off = 0; off < n; off += 16)
            {
                sb.Append(off.ToString("X8", System.Globalization.CultureInfo.InvariantCulture)).Append("  ");
                var ascii = new StringBuilder();
                for (var i = 0; i < 16; i++)
                {
                    if (off + i < n)
                    {
                        var b = buf[off + i];
                        sb.Append(b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture)).Append(' ');
                        ascii.Append(b is >= 32 and < 127 ? (char)b : '.');
                    }
                    else
                    {
                        sb.Append("   ");
                    }
                }

                sb.Append(' ').Append(ascii).AppendLine();
            }

            if (fs.Length > n) sb.Append("… ").Append(Format.Size(fs.Length - n)).AppendLine(" more");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            sb.Append(ex.Message);
        }

        return sb.ToString();
    }

    private static string FolderSummaryText(string path)
    {
        int files = 0, dirs = 0;
        long size = 0;
        try
        {
            foreach (var e in DirectoryReader.Enumerate(path).Take(50_000))
            {
                if (e.IsDirectory) dirs++;
                else { files++; size += e.Size; }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ex.Message;
        }

        return $"{dirs} folders · {files} files · {Format.Size(size)} (top level)\n⎇ {GitInfo.BranchFor(path) ?? "no git repository"}";
    }

    private static TextBlock Message(string text) => new()
    {
        Text = text,
        FontFamily = Ui.Mono,
        FontSize = 13,
        Margin = new Thickness(20),
        TextWrapping = TextWrapping.Wrap,
        Foreground = Theme.Brush("PolluxMutedBrush"),
    };
}
