using System.Text;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Pollux.App.Services;

namespace Pollux.App.Views;

/// <summary>The embedded terminal panel: xterm.js in WebView2 talking to a ConPTY shell (ADR 0004).</summary>
internal sealed class TerminalController(Border host, Func<string> folder, Action toggle)
{
    private readonly StringBuilder _pending = new();
    private readonly object _gate = new();
    private WebView2? _web;
    private CoreWebView2? _core;
    private PseudoConsole? _pty;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _flush;
    private short _cols = 120, _rows = 30;
    private bool _isCmd;
    private bool _ready;

    public bool IsStarted => _web is not null;

    public async Task EnsureStartedAsync(string shell, double fontSize)
    {
        if (_web is not null)
        {
            Focus();
            return;
        }

        _web = new WebView2();
        host.Child = _web;
        _flush = host.DispatcherQueue.CreateTimer();
        _flush.Interval = TimeSpan.FromMilliseconds(16);
        _flush.Tick += (_, _) => Flush();
        _flush.Start();
        _core = await WebHost.InitializeAsync(_web, "terminal.html");
        _core.PermissionRequested += (_, e) =>
        {
            if (e.PermissionKind == CoreWebView2PermissionKind.ClipboardRead) e.State = CoreWebView2PermissionState.Allow;
        };
        _core.WebMessageReceived += (_, e) => OnMessage(e.TryGetWebMessageAsString(), shell, fontSize);
    }

    private void OnMessage(string json, string shell, double fontSize)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        switch (root.GetProperty("type").GetString())
        {
            case "ready":
                _ready = true;
                _cols = (short)root.GetProperty("cols").GetInt32();
                _rows = (short)root.GetProperty("rows").GetInt32();
                Post(WebHost.Json(("type", "theme"), ("dark", Theme.Current == ElementTheme.Dark)));
                Post(WebHost.Json(("type", "fontSize"), ("size", fontSize)));
                StartShell(shell);
                Focus();
                break;
            case "input":
                _pty?.Write(root.GetProperty("data").GetString() ?? "");
                break;
            case "resize":
                _cols = (short)root.GetProperty("cols").GetInt32();
                _rows = (short)root.GetProperty("rows").GetInt32();
                _pty?.Resize(_cols, _rows);
                break;
            case "toggle":
                toggle();
                break;
        }
    }

    private void StartShell(string shell)
    {
        _pty?.Dispose();
        _isCmd = shell == "cmd";
        var exe = shell switch
        {
            "cmd" => "cmd.exe",
            "powershell" => "powershell.exe -NoLogo",
            _ => ExistsOnPath("pwsh.exe") ? "pwsh.exe -NoLogo" : "powershell.exe -NoLogo",
        };
        var cwd = folder();
        if (!Directory.Exists(cwd)) cwd = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        try
        {
            _pty = PseudoConsole.Start(exe, cwd, _cols, _rows);
            _pty.Output += text => { lock (_gate) _pending.Append(text); };
            var pty = _pty;
            _pty.Exited += () => host.DispatcherQueue.TryEnqueue(() =>
            {
                if (_pty != pty) return;
                _pty.Dispose();
                _pty = null;
                Post(WebHost.Json(("type", "data"), ("data", "\r\n\x1b[90m[process exited — press Ctrl+ö twice to restart]\x1b[0m\r\n")));
            });
        }
        catch (InvalidOperationException ex)
        {
            Post(WebHost.Json(("type", "data"), ("data", "Could not start shell: " + ex.Message + "\r\n")));
        }
    }

    private static bool ExistsOnPath(string exe) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';').Any(p => p.Length > 0 && File.Exists(Path.Combine(p, exe)));

    private void Flush()
    {
        string text;
        lock (_gate)
        {
            if (_pending.Length == 0) return;
            text = _pending.ToString();
            _pending.Clear();
        }

        Post(WebHost.Json(("type", "data"), ("data", text)));
    }

    /// <summary>Changes the shell's directory, quoted for the running shell so $, `, ' and % in folder names stay literal.</summary>
    public void ChangeDirectory(string path)
    {
        if (_pty is null) return;
        var command = _isCmd ? $"cd /d \"{path}\"" : $"Set-Location -LiteralPath '{EscapePowerShellSingleQuoted(path)}'";
        _pty.Write(command + "\r");
    }

    /// <summary>PowerShell treats ' and the curly quotes U+2018–U+201B as single quotes; each is escaped by doubling.</summary>
    private static string EscapePowerShellSingleQuoted(string s)
    {
        var sb = new StringBuilder(s.Length + 8);
        foreach (var c in s)
        {
            sb.Append(c);
            if (c is '\'' or '‘' or '’' or '‚' or '‛') sb.Append(c);
        }

        return sb.ToString();
    }

    public void Focus()
    {
        _web?.Focus(FocusState.Programmatic);
        Post(WebHost.Json(("type", "focus")));
    }

    public void SetTheme() => Post(WebHost.Json(("type", "theme"), ("dark", Theme.Current == ElementTheme.Dark)));

    /// <summary>Restarts the shell if it exited.</summary>
    public void RestartIfExited(string shell)
    {
        if (_ready && _pty is null) StartShell(shell);
    }

    private void Post(string json)
    {
        if (_ready) _core?.PostWebMessageAsString(json);
    }

    public void Dispose()
    {
        _flush?.Stop();
        _pty?.Dispose();
        _pty = null;
    }
}
