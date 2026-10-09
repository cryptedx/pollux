using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Pollux.App.Interop;
using Pollux.App.Views;
using Pollux.Core.Clipboard;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Streams;
using WinClipboard = Windows.ApplicationModel.DataTransfer.Clipboard;

namespace Pollux.App.Services;

/// <summary>
/// Pastes a Clip chosen in the Clipboard Stack back into the window that was active before the Stack opened:
/// puts it on the clipboard, returns focus there and sends Ctrl+V. UI thread only.
/// </summary>
internal static class ClipboardPasteBack
{
    private const ushort VK_RETURN = 0x0D, VK_CONTROL = 0x11, VK_V = 0x56;
    private const uint KEYEVENTF_KEYUP = 2;
    private static nint _target;
    private static uint _ownFrom;
    private static uint _ownTo;
    private static ClipHint? _hint;

    /// <summary>Remembers the foreground window as the paste target. Call right before the Clipboard Stack opens.</summary>
    public static void RememberTarget() => _target = Native.GetForegroundWindow();

    /// <summary>
    /// True when the clipboard change with this sequence number (GetClipboardSequenceNumber) is Pollux's own paste back:
    /// capture must not record it as a new Clip (the pasted Clip already moved to the top).
    /// </summary>
    public static bool IsOwnWrite(uint sequence) => sequence > _ownFrom && sequence <= _ownTo;

    /// <summary>Enter (<paramref name="onlyCopy"/> false) or Shift+Enter on <paramref name="clip"/>; the Stack is already hidden.</summary>
    public static async Task PasteAsync(Clip clip, bool onlyCopy, ClipboardHistory history, Func<Clip, byte[]?> readImage, Action changed)
    {
        try
        {
            var target = _target;
            var hasTarget = target != 0 && Native.IsWindow(target);
            var kind = !hasTarget ? PasteTarget.None : !MainWindow.IsElevated && IsElevated(target) ? PasteTarget.Elevated : PasteTarget.Ready;
            var plan = ClipPaste.Plan(clip, onlyCopy, kind, Exists);
            if (plan.SetClipboard)
            {
                if (!await WriteAsync(clip, plan.Files, readImage))
                {
                    ShowHint("This Clip cannot be pasted");
                    return;
                }

                history.Pasted(clip.Id, DateTime.Now);
                changed();
            }

            var hint = plan.Hint;
            // Return focus to the previous window even when only copying, so Ctrl+V lands there.
            var focused = hasTarget && await FocusAsync(target);
            if (plan.SendCtrlV)
            {
                if (focused) await SendCtrlVAsync();
                else hint = ClipPaste.Plan(clip, onlyCopy: false, PasteTarget.None, Exists).Hint;
            }

            if (hint is not null) ShowHint(hint);
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex);
        }
    }

    /// <summary>Closes the hint window (on Quit).</summary>
    public static void Shutdown()
    {
        _hint?.Close();
        _hint = null;
    }

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    private static void ShowHint(string text)
    {
        _hint ??= new ClipHint();
        _hint.ShowHint(text);
    }

    /// <summary>Puts the Clip on the clipboard: text as plain text, an image as bitmap, the given files as file list.</summary>
    private static async Task<bool> WriteAsync(Clip clip, IReadOnlyList<string> files, Func<Clip, byte[]?> readImage)
    {
        var data = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
        using var stream = new InMemoryRandomAccessStream();
        switch (clip.Kind)
        {
            case ClipKind.Text:
                data.SetText(clip.Text ?? "");
                break;
            case ClipKind.Files:
                var items = await PaneView.StorageItemsFor(files);
                if (items.Count == 0) return false;
                data.SetStorageItems(items);
                data.SetText(string.Join(Environment.NewLine, files));
                break;
            default:
                byte[]? bytes;
                try
                {
                    bytes = readImage(clip);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
                {
                    CrashLog.Write(ex);
                    return false;
                }

                if (bytes is not { Length: > 0 }) return false;
                using (var writer = new DataWriter(stream))
                {
                    writer.WriteBytes(bytes);
                    await writer.StoreAsync();
                    writer.DetachStream();
                }

                stream.Seek(0);
                data.SetBitmap(RandomAccessStreamReference.CreateFromStream(stream));
                break;
        }

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var before = Native.GetClipboardSequenceNumber();
                WinClipboard.SetContent(data);
                WinClipboard.Flush();
                _ownFrom = before;
                _ownTo = Native.GetClipboardSequenceNumber();
                return true;
            }
            catch (Exception ex) when (ex is COMException or UnauthorizedAccessException)
            {
                // Another app has the clipboard open: try again shortly.
                if (attempt >= 4)
                {
                    CrashLog.Trace("clipboard: busy, paste back failed: " + ex.Message);
                    return false;
                }

                await Task.Delay(50);
            }
        }
    }

    /// <summary>Brings <paramref name="hwnd"/> to the front and waits until Windows made it the foreground window.</summary>
    private static async Task<bool> FocusAsync(nint hwnd)
    {
        Native.SetForegroundWindow(hwnd);
        for (var i = 0; i < 25 && Native.GetForegroundWindow() != hwnd; i++)
        {
            await Task.Delay(20);
        }

        return Native.GetForegroundWindow() == hwnd;
    }

    private static async Task SendCtrlVAsync()
    {
        // Enter may still be held: Ctrl+Enter+V would reach the target as a different chord.
        for (var i = 0; i < 25 && Native.GetAsyncKeyState(VK_RETURN) < 0; i++)
        {
            await Task.Delay(20);
        }

        SendCtrlV();
    }

    private static unsafe void SendCtrlV()
    {
        var inputs = stackalloc Native.INPUT[4];
        inputs[0] = new Native.INPUT { type = 1, ki = new Native.KEYBDINPUT { wVk = VK_CONTROL } };
        inputs[1] = new Native.INPUT { type = 1, ki = new Native.KEYBDINPUT { wVk = VK_V } };
        inputs[2] = new Native.INPUT { type = 1, ki = new Native.KEYBDINPUT { wVk = VK_V, dwFlags = KEYEVENTF_KEYUP } };
        inputs[3] = new Native.INPUT { type = 1, ki = new Native.KEYBDINPUT { wVk = VK_CONTROL, dwFlags = KEYEVENTF_KEYUP } };
        Native.SendInput(4, inputs, sizeof(Native.INPUT));
    }

    /// <summary>
    /// True when the window's process runs elevated: UIPI then drops the Ctrl+V Pollux would send.
    /// A process Pollux may not even query is treated as elevated.
    /// </summary>
    private static unsafe bool IsElevated(nint hwnd)
    {
        Native.GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == 0) return false;
        var process = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process == 0) return true;
        try
        {
            if (!Native.OpenProcessToken(process, Native.TOKEN_QUERY, out var token)) return true;
            try
            {
                uint elevated = 0;
                return !Native.GetTokenInformation(token, Native.TokenElevation, &elevated, sizeof(uint), out _) || elevated != 0;
            }
            finally
            {
                Native.CloseHandle(token);
            }
        }
        finally
        {
            Native.CloseHandle(process);
        }
    }
}
