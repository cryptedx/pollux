using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Pollux.App.Interop;
using Pollux.Core.Clipboard;
using Pollux.Core.Settings;
using Windows.ApplicationModel.DataTransfer;
using WinClipboard = Windows.ApplicationModel.DataTransfer.Clipboard;

namespace Pollux.App.Services;

/// <summary>
/// Records every Clip into the Clipboard History while it is on: listens for copies, reads the clipboard,
/// applies the capture rules (ClipCapture) and saves the history encrypted (debounced). Runs on the UI thread, except
/// loading and writing the store, which run on a background task.
/// The Admin Instance never captures.
/// </summary>
internal sealed class ClipboardService
{
    private static readonly uint ExcludeFormat = Native.RegisterClipboardFormatW("ExcludeClipboardContentFromMonitorProcessing");
    private static readonly uint CanIncludeFormat = Native.RegisterClipboardFormatW("CanIncludeInClipboardHistory");

    private readonly AppServices _services;
    private readonly DispatcherQueue _dispatcher;
    private ClipboardListener? _listener;
    private ClipStore? _store;
    private ClipHistoryKeeper? _keeper;
    private DispatcherQueueTimer? _captureDebounce;
    private DispatcherQueueTimer? _saveDebounce;
    private bool _capturing;
    private bool _captureAgain;
    private uint _lastSequence;

    public ClipboardService(AppServices services, DispatcherQueue dispatcher)
    {
        _services = services;
        _dispatcher = dispatcher;
    }

    /// <summary>Raised on the UI thread after a Clip was recorded or the history was cleared.</summary>
    public event Action? Changed;

    private ClipboardSettings Settings => _services.Settings.Clipboard;

    private ClipStore Store => _store ??= new ClipStore(ClipStore.DirectoryFor(_services.Paths), DpapiClipProtector.Instance);

    /// <summary>Owns loading, retention and saving of the history (data-safety rules live in Core).</summary>
    private ClipHistoryKeeper Keeper
    {
        get
        {
            if (_keeper is null)
            {
                _keeper = new ClipHistoryKeeper(Store, () => Settings.Retention, CrashLog.Info, CrashLog.Write);
                _keeper.Merged += () => Changed?.Invoke();
            }

            return _keeper;
        }
    }

    /// <summary>The Clipboard History, loaded from disk on first use (waits for a load started by <see cref="Apply"/>).</summary>
    public ClipboardHistory History => Keeper.History;

    /// <summary>Starts or stops listening after a settings change. Called with elevated=true from the Admin Instance, which never captures.</summary>
    public void Apply(bool elevated)
    {
        var on = Settings.Enabled && !elevated;
        if (on && _listener is null)
        {
            // Decrypting a large history takes a while: load in the background; the first use of History waits for it.
            Keeper.BeginLoad();

            _listener = new ClipboardListener(_dispatcher);
            _listener.ClipboardChanged += OnClipboardChanged;
            _lastSequence = Native.GetClipboardSequenceNumber();
        }
        else if (!on && _listener is not null)
        {
            _listener.Dispose();
            _listener = null;
        }

        if (_keeper is { } keeper && keeper.ApplyRetention())
        {
            ScheduleSave();
        }
    }

    /// <summary>Pause / resume recording (command and tray menu). Stored in settings.json so it survives a restart.</summary>
    public void SetPaused(bool paused)
    {
        if (Settings.Paused == paused) return;
        Settings.Paused = paused;
        _services.SaveSettings();
    }

    /// <summary>Clear History: removes every Clip except the Kept Clips.</summary>
    public void ClearHistory()
    {
        Keeper.Clear();
        Changed?.Invoke();
        SaveSoon();
    }

    /// <summary>An image Clip's bytes: in memory until saved, then read from its encrypted file.</summary>
    public byte[]? ReadImage(Clip clip) => clip.Image ?? _store?.ReadImage(clip);

    /// <summary>Stops listening and writes pending changes (on Quit), waiting for a write in progress.</summary>
    public void Shutdown()
    {
        _listener?.Dispose();
        _listener = null;
        _saveDebounce?.Stop();
        _keeper?.Shutdown();
    }

    /// <summary>Call after changing the History from outside (Clipboard Stack): saves debounced.</summary>
    public void SaveSoon() => ScheduleSave();

    private void OnClipboardChanged()
    {
        // One copy can raise several updates (formats rendered one by one): wait until it settles.
        if (_captureDebounce is null)
        {
            _captureDebounce = _dispatcher.CreateTimer();
            _captureDebounce.Interval = TimeSpan.FromMilliseconds(150);
            _captureDebounce.IsRepeating = false;
            _captureDebounce.Tick += (_, _) => _ = CaptureAsync();
        }

        _captureDebounce.Start();
    }

    private async Task CaptureAsync()
    {
        if (_capturing)
        {
            _captureAgain = true;
            return;
        }

        _capturing = true;
        try
        {
            do
            {
                _captureAgain = false;
                await CaptureOnceAsync();
            }
            while (_captureAgain);
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex);
        }
        finally
        {
            _capturing = false;
        }
    }

    private async Task CaptureOnceAsync()
    {
        if (_listener is null) return;
        var sequence = Native.GetClipboardSequenceNumber();
        if (sequence == _lastSequence) return;
        _lastSequence = sequence;
        // Pasting from the Clipboard Stack already moved that Clip to the top: its clipboard write is no new Clip.
        if (ClipboardPasteBack.IsOwnWrite(sequence)) return;

        var (sourceApp, sourcePath) = SourceApp();
        // Paused, private or excluded: decide before reading, so such content is never read at all.
        // Checked again on every read attempt and before the read content is used (see ReadAsync).
        bool Allowed()
        {
            if (_listener is not { } listener) return false;
            var gate = ClipCapture.Decide(new CopiedContent { SourceApp = sourceApp, Private = IsPrivate(listener.Handle) }, Settings, DateTime.Now);
            if (gate.Skip == CaptureSkip.Empty) return true;
            CrashLog.Trace($"clipboard: not recorded ({gate.Skip}) from {sourceApp}");
            return false;
        }

        if (!Allowed()) return;

        var copied = await ReadAsync(sequence, sourceApp, sourcePath, Allowed);
        if (copied is null || _listener is null) return;
        var now = DateTime.Now;
        var result = ClipCapture.Decide(copied, Settings, now);
        if (result.LogLine is { } line) CrashLog.Info(line);
        if (result.Clip is not { } clip) return;

        History.Add(clip, now);
        Changed?.Invoke();
        ScheduleSave();
    }

    /// <summary>
    /// Reads text (the plain-text form of rich text and HTML), file lists or an image (as PNG). Null when the clipboard
    /// stays busy, when its content changed after the privacy check passed for <paramref name="checkedSequence"/>
    /// (that new content raises its own update and is checked again), or when <paramref name="allowed"/> (the privacy
    /// formats and exclusion check) fails on any attempt or after reading.
    /// </summary>
    private async Task<CopiedContent?> ReadAsync(uint checkedSequence, string? sourceApp, string? sourcePath, Func<bool> allowed)
    {
        for (var attempt = 0; ; attempt++)
        {
            if (Native.GetClipboardSequenceNumber() != checkedSequence || !allowed()) return null;
            try
            {
                var view = WinClipboard.GetContent();
                List<string>? files = null;
                string? text = null;
                byte[]? image = null;
                if (view.Contains(StandardDataFormats.StorageItems))
                {
                    var items = await view.GetStorageItemsAsync();
                    files = items.Select(i => i.Path).Where(p => !string.IsNullOrEmpty(p)).ToList();
                }

                if (files is not { Count: > 0 } && view.Contains(StandardDataFormats.Text))
                {
                    text = await view.GetTextAsync();
                }

                if (files is not { Count: > 0 } && string.IsNullOrWhiteSpace(text) && view.Contains(StandardDataFormats.Bitmap))
                {
                    image = await ClipboardImage.ToPngAsync(view);
                }

                // Changed while reading: what was read may be newer content that never passed the check.
                if (Native.GetClipboardSequenceNumber() != checkedSequence || !allowed()) return null;
                return new CopiedContent { Files = files, Text = text, Image = image, SourceApp = sourceApp, SourcePath = sourcePath };
            }
            catch (Exception ex) when (ex is COMException or UnauthorizedAccessException)
            {
                // Another app has the clipboard open: try again shortly.
                if (attempt >= 4)
                {
                    CrashLog.Trace("clipboard: busy, copy not recorded: " + ex.Message);
                    return null;
                }

                await Task.Delay(100);
            }
        }
    }

    /// <summary>
    /// True when the owner asked clipboard monitors to skip the content (password managers):
    /// ExcludeClipboardContentFromMonitorProcessing is present, or CanIncludeInClipboardHistory is 0.
    /// </summary>
    private static unsafe bool IsPrivate(nint owner)
    {
        if (Native.IsClipboardFormatAvailable(ExcludeFormat)) return true;
        if (!Native.IsClipboardFormatAvailable(CanIncludeFormat)) return false;
        if (!Native.OpenClipboard(owner)) return true; // cannot check: better not record it
        try
        {
            var handle = Native.GetClipboardData(CanIncludeFormat);
            if (handle == 0) return false;
            var data = Native.GlobalLock(handle);
            if (data == 0) return false;
            try
            {
                return Native.GlobalSize(handle) >= sizeof(uint) && *(uint*)data == 0;
            }
            finally
            {
                Native.GlobalUnlock(handle);
            }
        }
        finally
        {
            Native.CloseClipboard();
        }
    }

    /// <summary>Process name and executable of the app that owns the clipboard (falls back to the foreground window).</summary>
    private static unsafe (string? Name, string? Path) SourceApp()
    {
        var hwnd = Native.GetClipboardOwner();
        if (hwnd == 0) hwnd = Native.GetForegroundWindow();
        if (hwnd == 0) return (null, null);
        Native.GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == 0) return (null, null);

        var process = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process != 0)
        {
            try
            {
                var buffer = stackalloc char[1024];
                uint size = 1024;
                if (Native.QueryFullProcessImageNameW(process, 0, buffer, ref size))
                {
                    var path = new string(buffer, 0, (int)size);
                    return (Path.GetFileNameWithoutExtension(path), path);
                }
            }
            finally
            {
                Native.CloseHandle(process);
            }
        }

        try
        {
            using var p = System.Diagnostics.Process.GetProcessById((int)pid);
            return (p.ProcessName, null);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return (null, null);
        }
    }

    private void ScheduleSave()
    {
        Keeper.MarkChanged();
        if (_saveDebounce is null)
        {
            _saveDebounce = _dispatcher.CreateTimer();
            _saveDebounce.Interval = TimeSpan.FromSeconds(1);
            _saveDebounce.IsRepeating = false;
            _saveDebounce.Tick += (_, _) => _ = SaveNowAsync();
        }

        _saveDebounce.Start();
    }

    /// <summary>
    /// Flushes the history: the keeper takes its snapshot on the UI thread (this runs from the dispatcher timer) and
    /// writes on a background task. A flush that had to wait for a running write is retried by the keeper itself.
    /// </summary>
    private async Task SaveNowAsync()
    {
        _saveDebounce?.Stop();
        try
        {
            await Keeper.FlushAsync();
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex);
        }
    }
}
