using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Pollux.App.Interop;
using Pollux.App.Views;
using Pollux.Core.Settings;
using Pollux.Core.Snap;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Streams;
using WinClipboard = Windows.ApplicationModel.DataTransfer.Clipboard;

namespace Pollux.App.Services;

/// <summary>The ways to take or open a Snap (one command each).</summary>
internal enum SnapMode { Area, Window, Screen, Repeat, AreaAfterDelay, ClipboardImage, CopyText }

/// <summary>
/// Runs a Snap: freezes the screen, shows one overlay per monitor, and puts the chosen area on the clipboard
/// and into the Snaps folder, and opens the Snap Board (spec: .scratch/snap/spec.md).
/// </summary>
internal static class SnapService
{
    private static readonly List<SnapOverlay> Overlays = [];
    private static readonly List<FloatWindow> Floats = [];
    private static FrozenScreen? _screen;
    private static ClipHint? _hint;
    private static SnapBoard? _board;
    private static App? _app;

    private static PixelRect? _lastArea;
    private static bool _readText;
    private static DispatcherQueueTimer? _countdown;
    private static DispatcherQueueTimer? _retention;

    public static bool IsOpen => _screen is not null || _countdown is not null;

    /// <summary>Runs <paramref name="mode"/>; a second request while a Snap is being taken is ignored.</summary>
    public static void Run(App app, SnapMode mode)
    {
        _app = app;
        switch (mode)
        {
            case SnapMode.Area or SnapMode.Window or SnapMode.CopyText when !IsOpen:
                _readText = mode == SnapMode.CopyText;
                OpenOverlays(windowsOnly: mode == SnapMode.Window);
                break;
            case SnapMode.Screen when !IsOpen:
                TakeDirect(layout =>
                {
                    Native.GetCursorPos(out var c);
                    var monitor = layout.Monitors.FirstOrDefault(m => Contains(m.Bounds, c.X, c.Y)) ?? layout.Monitors[0];
                    return SnapSelection.WholeMonitor(monitor);
                });
                break;
            case SnapMode.Repeat when !IsOpen:
                if (_lastArea is not { } last) Hint("No last area yet: take an area Snap first");
                else TakeDirect(layout => SnapSelection.Repeat(last, layout), "The last area is on no monitor any more");
                break;
            case SnapMode.AreaAfterDelay when !IsOpen:
                StartCountdown(app);
                break;
            case SnapMode.ClipboardImage:
                _ = OpenClipboardImageAsync(app);
                break;
        }
    }

    private static void OpenOverlays(bool windowsOnly)
    {
        _screen = FrozenScreen.Take();
        if (_screen is null)
        {
            Hint("Could not read the screen");
            return;
        }

        var monitors = _screen.Layout.Monitors;
        while (Overlays.Count < monitors.Count)
        {
            var overlay = new SnapOverlay();
            overlay.Finished += OnFinished;
            overlay.Deactivated += OnDeactivated;
            overlay.ColorCopied += OnColorCopied;
            Overlays.Add(overlay);
        }

        Native.GetCursorPos(out var c);
        var underCursor = monitors.FirstOrDefault(m => Contains(m.Bounds, c.X, c.Y)) ?? monitors[0];
        SnapOverlay? active = null;
        for (var i = 0; i < monitors.Count; i++)
        {
            Overlays[i].Open(_screen, monitors[i], activate: false, windowsOnly);
            if (monitors[i] == underCursor) active = Overlays[i];
        }

        active?.Focus();
    }

    /// <summary>Freezes the screen and delivers <paramref name="choose"/>'s area without the overlay.</summary>
    private static void TakeDirect(Func<ScreenLayout, SnapSelection?> choose, string? none = null)
    {
        var screen = FrozenScreen.Take();
        if (screen is null)
        {
            Hint("Could not read the screen");
            return;
        }

        if (choose(screen.Layout) is not { } selection)
        {
            Hint(none ?? "Nothing to snap");
            return;
        }

        _lastArea = selection.Area;
        _ = DeliverAsync(_app!, screen, selection);
    }

    /// <summary>Counts down snap.delaySeconds in a hint, hides the hint, then opens the overlay.</summary>
    private static void StartCountdown(App app)
    {
        var left = Math.Clamp(app.Services.Settings.Snap.DelaySeconds, 1, 60);
        _countdown = app.Dispatcher.CreateTimer();
        _countdown.Interval = TimeSpan.FromSeconds(1);
        Hint($"Snap in {left}…", 2);
        _countdown.Tick += (t, _) =>
        {
            left--;
            if (left > 0)
            {
                Hint($"Snap in {left}…", 2);
                return;
            }

            t.Stop();
            _hint?.HideHint();
            // One more frame so the hint is gone from the screen before it is frozen.
            var go = app.Dispatcher.CreateTimer();
            go.Interval = TimeSpan.FromMilliseconds(120);
            go.IsRepeating = false;
            go.Tick += (_, _) =>
            {
                _countdown = null;
                _readText = false;
                OpenOverlays(windowsOnly: false);
            };
            go.Start();
        };
        _countdown.Start();
    }

    /// <summary>Opens the image on the clipboard in the Snap Board, as an unsaved Snap.</summary>
    private static async Task OpenClipboardImageAsync(App app)
    {
        byte[] png;
        try
        {
            var view = WinClipboard.GetContent();
            if (!view.Contains(StandardDataFormats.Bitmap))
            {
                Hint("No image on the clipboard");
                return;
            }

            png = await ClipboardImage.ToPngAsync(view);
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException)
        {
            CrashLog.Write(ex);
            Hint("Could not read the clipboard");
            return;
        }

        Native.GetCursorPos(out var c);
        var monitor = Native.MonitorFromPoint(c, 2 /* MONITOR_DEFAULTTONEAREST */);
        var scale = Native.GetDpiForMonitor(monitor, Native.MDT_EFFECTIVE_DPI, out var dpi, out _) == 0 ? dpi / 96.0 : 1.0;
        OpenBoard(app, png, scale, null);
    }

    /// <summary>The Snaps folder in a new Tab of <paramref name="window"/>, or in a new window.</summary>
    public static void OpenHistory(App app, MainWindow? window)
    {
        var folder = Folder(app.Services.Settings.Snap);
        try { Directory.CreateDirectory(folder); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Hint("Snaps folder not available: " + folder);
            return;
        }

        if (window is not null) window.OpenInNewTab(folder);
        else app.OpenNewWindow(folder);
    }

    /// <summary>
    /// snap.keepDays: sends Snaps taken longer ago than that to the Recycle Bin now and then once a day. Only files named
    /// like a Snap are touched. Called whenever the settings apply.
    /// </summary>
    public static void ApplyRetention(App app)
    {
        var s = app.Services.Settings.Snap;
        if (!s.Enabled || s.KeepDays <= 0)
        {
            _retention?.Stop();
            return;
        }

        if (_retention is null)
        {
            _retention = app.Dispatcher.CreateTimer();
            _retention.Interval = TimeSpan.FromHours(24);
            _retention.Tick += (_, _) => RecycleOldSnaps(app);
        }

        _retention.Start();
        RecycleOldSnaps(app);
    }

    private static void RecycleOldSnaps(App app)
    {
        var s = app.Services.Settings.Snap;
        if (!s.Enabled || s.KeepDays <= 0) return;
        var folder = Folder(s);
        try
        {
            if (!Directory.Exists(folder)) return;
            var names = Directory.EnumerateFiles(folder, "Snap *.png").Select(p => Path.GetFileName(p));
            var due = SnapRetention.Due(names, DateTime.Now, s.KeepDays).Select(n => Path.Combine(folder, n)).ToList();
            if (due.Count == 0) return;
            // To the Recycle Bin, without asking; files that cannot be recycled still make Windows ask before deleting them.
            if (Shell.Delete(0, due, permanent: false, confirm: false))
                CrashLog.Trace($"snap: {due.Count} Snap(s) older than {s.KeepDays} days moved to the Recycle Bin");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            CrashLog.Write(ex);
        }
    }

    public static void Shutdown()
    {
        _retention?.Stop();
        foreach (var o in Overlays) o.Close();
        Overlays.Clear();
        foreach (var f in Floats.ToList()) f.Close();
        _countdown?.Stop();
        _countdown = null;
        _hint?.Close();
        _board?.Close();
    }

    private static void OnFinished(SnapOverlay sender, SnapSelection? selection)
    {
        var screen = _screen;
        Dismiss();
        if (selection is null || screen is null || _app is null) return;
        _lastArea = selection.Area;
        if (_readText) _ = CopyTextAsync(_app, screen, selection.Area);
        else _ = DeliverAsync(_app, screen, selection);
    }

    /// <summary>Focus moved: to another overlay (a click on the other monitor) is fine; anywhere else cancels the Snap.</summary>
    private static void OnDeactivated(SnapOverlay sender)
    {
        sender.DispatcherQueue.TryEnqueue(() =>
        {
            var fg = Native.GetForegroundWindow();
            if (IsOpen && !Overlays.Any(o => o.Hwnd == fg)) Dismiss();
        });
    }

    private static void OnColorCopied(string hex)
    {
        Dismiss();
        _ = CopyColorAsync(hex);
    }

    private static async Task CopyColorAsync(string hex) =>
        Hint(await CopyTextAsync(hex) ? $"Copied {hex}" : "Clipboard is busy");

    private static void Dismiss()
    {
        foreach (var o in Overlays) o.Dismiss();
        _screen = null;
    }

    /// <summary>What happens with the chosen area, per snap.afterCapture.</summary>
    private static async Task DeliverAsync(App app, FrozenScreen screen, SnapSelection selection)
    {
        var settings = app.Services.Settings.Snap;
        byte[] png;
        try
        {
            png = await screen.EncodePngAsync(selection.Area);
        }
        catch (COMException ex)
        {
            CrashLog.Write(ex);
            Hint("Could not encode the Snap");
            return;
        }

        var after = settings.AfterCapture;
        var copy = after != SnapAfterCapture.Save;
        var save = settings.AutoSave || after == SnapAfterCapture.Save;
        var copied = copy && await CopyAsync(png);
        string? saved = null;
        if (save)
        {
            var folder = Folder(settings);
            var path = SnapSaveTarget.For(null, folder, DateTime.Now, File.Exists);
            saved = await WriteAsync(path, png) ? path : "";
        }

        if (after == SnapAfterCapture.AsFloat)
        {
            _ = OpenFloatAsync(app, png, selection.Monitor.Scale, string.IsNullOrEmpty(saved) ? null : saved, selection.Area);
            if (!copied) Hint("Clipboard is busy");
            return;
        }

        if (after == SnapAfterCapture.Board)
        {
            OpenBoard(app, png, selection.Monitor.Scale, string.IsNullOrEmpty(saved) ? null : saved);
            if (copy && !copied) Hint("Clipboard is busy");
            return;
        }

        var parts = new List<string>();
        if (copy) parts.Add(copied ? "Snap copied" : "Clipboard is busy");
        if (saved is not null) parts.Add(saved.Length == 0 ? "not saved" : "saved as " + Path.GetFileName(saved));
        Hint(string.Join(" · ", parts));
    }

    /// <summary>
    /// Keeps <paramref name="png"/> on screen as a Float: exactly over the area it was taken from (<paramref name="at"/>),
    /// or at its own size in the middle of the monitor under the cursor.
    /// </summary>
    public static async Task OpenFloatAsync(App app, byte[] png, double scale, string? savedPath, PixelRect? at = null)
    {
        var (w, h) = PngSize(png);
        if (w <= 0 || h <= 0) return;
        if (at is null)
        {
            Native.GetCursorPos(out var c);
            var work = Microsoft.UI.Windowing.DisplayArea.GetFromPoint(new Windows.Graphics.PointInt32(c.X, c.Y), Microsoft.UI.Windowing.DisplayAreaFallback.Primary).WorkArea;
            at = SnapFloat.Place(w, h, new PixelRect(work.X, work.Y, work.Width, work.Height));
        }

        var window = new FloatWindow(png, w, h, scale, savedPath);
        window.Closed += (_, _) => Floats.Remove(window);
        window.BoardRequested += f => OpenBoard(app, f.Png, f.Scale, f.SavedPath);
        Floats.Add(window);
        await window.ShowAtAsync(at.Value);
    }

    /// <summary>Makes every click-through Float take clicks again.</summary>
    public static void ReleaseFloats()
    {
        var released = Floats.Count(f => f.ClickThrough);
        foreach (var f in Floats) f.Release();
        Hint(Floats.Count == 0 ? "No Floats open" : released == 0 ? "No Float was click-through" : $"Released {released} Float(s)");
    }

    /// <summary>Width and height from a PNG's IHDR chunk (bytes 16–23, big-endian).</summary>
    public static (int Width, int Height) PngSize(byte[] png) =>
        png.Length < 24 ? (0, 0) : ((png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19], (png[20] << 24) | (png[21] << 16) | (png[22] << 8) | png[23]);

    private static void OpenBoard(App app, byte[] png, double scale, string? savedPath)
    {
        _board ??= new SnapBoard(app);
        _board.Open(png, scale, savedPath);
    }

    /// <summary>Snap: Copy Text: reads the text in the area and puts it on the clipboard; nothing is saved.</summary>
    private static async Task CopyTextAsync(App app, FrozenScreen screen, PixelRect area)
    {
        var png = await screen.EncodePngAsync(area);
        var result = await SnapTextRecognition.RecognizeAsync(png, app.Services.Settings.Snap.OcrLanguage);
        if (result.Problem is not null)
        {
            Hint(result.Problem, 5);
            return;
        }

        var lines = result.Text.Count(c => c == '\n') + 1;
        Hint(await CopyTextAsync(result.Text) ? $"Copied {lines} line{(lines == 1 ? "" : "s")} of text" : "Clipboard is busy");
    }

    /// <summary>Puts plain text on the clipboard; false when another app keeps the clipboard busy.</summary>
    public static Task<bool> CopyTextAsync(string text)
    {
        var data = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
        data.SetText(text);
        return SetClipboardAsync(data);
    }

    /// <summary>The Snaps folder from settings (empty = Pictures\Snaps).</summary>
    public static string Folder(SnapSettings settings) =>
        settings.FolderOrDefault(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures));

    /// <summary>Puts a PNG on the clipboard; false when another app keeps the clipboard busy.</summary>
    public static async Task<bool> CopyAsync(byte[] png)
    {
        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream))
        {
            writer.WriteBytes(png);
            await writer.StoreAsync();
            writer.DetachStream();
        }

        stream.Seek(0);
        var data = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
        data.SetBitmap(RandomAccessStreamReference.CreateFromStream(stream));
        return await SetClipboardAsync(data);
    }

    private static async Task<bool> SetClipboardAsync(DataPackage data)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                WinClipboard.SetContent(data);
                WinClipboard.Flush();
                return true;
            }
            catch (Exception ex) when (ex is COMException or UnauthorizedAccessException)
            {
                // Another app has the clipboard open: try again shortly.
                if (attempt >= 4)
                {
                    CrashLog.Trace("snap: clipboard busy: " + ex.Message);
                    return false;
                }

                await Task.Delay(50);
            }
        }
    }

    /// <summary>Writes a PNG to <paramref name="path"/>, creating its folder; false (and a log entry) when that fails.</summary>
    public static async Task<bool> WriteAsync(string path, byte[] png)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, png);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            CrashLog.Write(ex);
            return false;
        }
    }

    private static void Hint(string text, int seconds = 3)
    {
        _hint ??= new ClipHint();
        _hint.ShowHint(text, seconds);
    }

    private static bool Contains(PixelRect r, int x, int y) => x >= r.X && x < r.Right && y >= r.Y && y < r.Bottom;
}
