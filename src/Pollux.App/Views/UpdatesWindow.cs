using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Pollux.App.Services;
using Pollux.Core.Updates;

namespace Pollux.App.Views;

/// <summary>
/// App updates through winget: check, pick which packages to update, watch winget's output.
/// winget is the only part of Pollux that talks to the internet, and only after the user opts in (ADR 0001).
/// </summary>
internal sealed partial class UpdatesWindow : Window
{
    private static Microsoft.UI.Dispatching.DispatcherQueueTimer? _scheduler;
    private static int _scheduledHours;
    private static App? _scheduleApp;
    private readonly App _app;
    private readonly Grid _root = new() { Padding = new Thickness(18, 8, 18, 14), RowSpacing = 10 };
    private readonly StackPanel _list = new() { Spacing = 2 };
    private readonly TextBlock _status = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBox _output = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, FontSize = 11.5, MinHeight = 150 };
    private readonly Button _check = new() { Content = "Check now" };
    private readonly Button _update = new() { Content = "Update selected", IsEnabled = false };
    private readonly Button _updateAdmin = new() { Content = "Update selected as admin", IsEnabled = false };
    private readonly Button _ignore = new() { Content = "Ignore selected", IsEnabled = false };
    private readonly List<(CheckBox Box, WingetPackage Package)> _rows = [];
    private bool _busy;

    public static IReadOnlyList<WingetPackage> LastResult { get; private set; } = [];
    public static event Action? ResultChanged;

    public UpdatesWindow(App app)
    {
        _app = app;
        Title = "Pollux Updates";
        ExtendsContentIntoTitleBar = true;
        SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop();
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "pollux.ico"));
        var scale = Interop.Native.GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
        AppWindow.Resize(new Windows.Graphics.SizeInt32((int)(860 * scale), (int)(680 * scale)));
        Build();
        Content = _root;
    }

    public void ShowWindow()
    {
        _root.RequestedTheme = Theme.Current;
        Activate();
        Render(LastResult);
        if (LastResult.Count == 0 && !_busy) _ = CheckAsync();
    }

    /// <summary>Background check every N hours, only if the user opted in.</summary>
    public static void Schedule(App app)
    {
        var s = app.Services.Settings.Updates;
        var hours = s.CheckAutomatically ? Math.Max(1, s.CheckIntervalHours) : 0;
        if (hours == _scheduledHours) return; // unrelated settings change: keep the running schedule
        _scheduledHours = hours;
        _scheduleApp = app;
        _scheduler?.Stop();
        if (hours == 0) return;
        if (_scheduler is null)
        {
            _scheduler = app.Dispatcher.CreateTimer();
            _scheduler.Tick += (_, _) => _ = SafeCheckAsync();
        }

        _scheduler.Interval = TimeSpan.FromHours(hours);
        _scheduler.Start();
        _ = SafeCheckAsync();
    }

    private static async Task SafeCheckAsync()
    {
        try
        {
            if (_scheduleApp is not null) await RunCheckAsync(_scheduleApp);
        }
        catch (Win32Exception)
        {
            // winget missing: nothing to report in the background.
        }
    }

    private static async Task<string> RunCheckAsync(App app)
    {
        var (_, output) = await RunWingetAsync("upgrade --include-unknown --accept-source-agreements --disable-interactivity", null);
        var ignored = new HashSet<string>(app.Services.Settings.Updates.IgnoredPackages, StringComparer.OrdinalIgnoreCase);
        LastResult = WingetParser.ParseUpgrades(output).Where(p => !ignored.Contains(p.Id)).ToList();
        ResultChanged?.Invoke();
        return output;
    }

    private void Build()
    {
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(26) });
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(170) });

        var title = new TextBlock { Text = "pollux_ updates", FontFamily = Ui.Mono, FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        _root.Children.Add(title);
        SetTitleBar(title);

        var top = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        top.Children.Add(_check);
        _status.FontFamily = Ui.Mono;
        top.Children.Add(_status);
        Grid.SetRow(top, 1);
        _root.Children.Add(top);

        var scroll = new ScrollViewer { Content = _list };
        Grid.SetRow(scroll, 2);
        _root.Children.Add(scroll);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var all = new Button { Content = "Select all" };
        all.Click += (_, _) => { foreach (var r in _rows) r.Box.IsChecked = true; };
        actions.Children.Add(all);
        actions.Children.Add(_update);
        actions.Children.Add(_updateAdmin);
        actions.Children.Add(_ignore);
        Grid.SetRow(actions, 3);
        _root.Children.Add(actions);

        _output.FontFamily = Ui.Mono;
        Grid.SetRow(_output, 4);
        _root.Children.Add(_output);

        _check.Click += async (_, _) => await CheckAsync();
        _update.Click += async (_, _) => await UpdateSelectedAsync(elevated: false);
        _updateAdmin.Click += async (_, _) => await UpdateSelectedAsync(elevated: true);
        _ignore.Click += (_, _) =>
        {
            foreach (var p in Selected()) _app.Services.Settings.Updates.IgnoredPackages.Add(p.Id);
            _app.Services.SaveSettings();
            LastResult = LastResult.Where(p => !_app.Services.Settings.Updates.IgnoredPackages.Contains(p.Id)).ToList();
            Render(LastResult);
        };
    }

    private List<WingetPackage> Selected() => _rows.Where(r => r.Box.IsChecked == true).Select(r => r.Package).ToList();

    private void Render(IReadOnlyList<WingetPackage> packages)
    {
        _rows.Clear();
        _list.Children.Clear();
        foreach (var p in packages)
        {
            var box = new CheckBox { MinWidth = 0, IsEnabled = IsSafeId(p.Id) };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(box, $"{p.Name} ({p.Id})");
            if (!box.IsEnabled) ToolTipService.SetToolTip(box, "winget shortened this id; update it from a terminal: winget upgrade");
            box.Checked += (_, _) => UpdateButtons();
            box.Unchecked += (_, _) => UpdateButtons();
            var grid = new Grid { ColumnSpacing = 12, Padding = new Thickness(8, 4, 8, 4), Background = Theme.Brush("PolluxSurfaceBrush"), CornerRadius = new CornerRadius(4) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.Children.Add(box);
            var name = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            name.Children.Add(new TextBlock { Text = p.Name, FontSize = 13 });
            name.Children.Add(new TextBlock { Text = p.Id, FontFamily = Ui.Mono, FontSize = 11, Foreground = Theme.Brush("PolluxMutedBrush") });
            Grid.SetColumn(name, 1);
            grid.Children.Add(name);
            var version = new TextBlock { Text = $"{p.Version}  →  {p.Available}", FontFamily = Ui.Mono, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(version, 2);
            grid.Children.Add(version);
            _rows.Add((box, p));
            _list.Children.Add(grid);
        }

        if (packages.Count == 0) _list.Children.Add(new TextBlock { Text = _busy ? "" : "Everything is up to date (or not checked yet).", Foreground = Theme.Brush("PolluxMutedBrush"), Margin = new Thickness(4) });
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        var any = _rows.Any(r => r.Box.IsChecked == true) && !_busy;
        _update.IsEnabled = any;
        _updateAdmin.IsEnabled = any;
        _ignore.IsEnabled = any;
        _check.IsEnabled = !_busy;
    }

    private async Task CheckAsync()
    {
        _busy = true;
        UpdateButtons();
        _status.Text = "checking with winget…";
        _output.Text = "";
        try
        {
            var output = await RunCheckAsync(_app);
            _output.Text = Clean(output);
            _status.Text = $"{LastResult.Count} update(s) available · {DateTime.Now:HH:mm}";
        }
        catch (Win32Exception)
        {
            _status.Text = "winget not found. Install 'App Installer' from Microsoft.";
        }
        finally
        {
            _busy = false;
            Render(LastResult);
        }
    }

    private async Task UpdateSelectedAsync(bool elevated)
    {
        var selected = Selected();
        if (selected.Count == 0) return;
        _busy = true;
        UpdateButtons();
        if (elevated)
        {
            // One elevated PowerShell runs all selected upgrades; Windows (or Delinea) asks for consent once.
            // Ids are validated (no quotes or shell syntax) and the script is passed encoded, so nothing can break out of it.
            var script = string.Join("; ", selected.Where(p => IsSafeId(p.Id)).Select(p => $"winget upgrade --id '{p.Id}' --exact --accept-source-agreements --accept-package-agreements")) + "; Read-Host 'Done. Press Enter to close'";
            var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            try
            {
                Process.Start(new ProcessStartInfo("powershell.exe", $"-NoProfile -EncodedCommand {encoded}") { UseShellExecute = true, Verb = "runas" });
                _status.Text = "running elevated in a separate window…";
            }
            catch (Win32Exception)
            {
                _status.Text = "elevation cancelled";
            }

            _busy = false;
            UpdateButtons();
            return;
        }

        foreach (var p in selected.Where(p => IsSafeId(p.Id)))
        {
            _status.Text = $"updating {p.Name}…";
            var (code, _) = await RunWingetAsync($"upgrade --id \"{p.Id}\" --exact --accept-source-agreements --accept-package-agreements --disable-interactivity", line => DispatcherQueue.TryEnqueue(() => Append(line)));
            Append(code == 0 ? $"✓ {p.Id} updated" : $"✗ {p.Id} failed (exit {code}) — try 'Update selected as admin'");
        }

        _busy = false;
        await CheckAsync();
    }

    private void Append(string line)
    {
        var clean = Clean(line);
        if (clean.Trim().Length == 0) return;
        _output.Text += clean + Environment.NewLine;
        _output.SelectionStart = _output.Text.Length;
    }

    private static string Clean(string s)
    {
        var sb = new StringBuilder();
        foreach (var raw in s.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = raw.Contains('\r') ? raw[(raw.LastIndexOf('\r') + 1)..] : raw;
            if (line.Trim() is "-" or "\\" or "|" or "/" || line.Contains('█') || line.Contains('▒')) continue;
            sb.AppendLine(line);
        }

        return sb.ToString().TrimEnd();
    }

    private static async Task<(int Code, string Output)> RunWingetAsync(string args, Action<string>? onLine)
    {
        var psi = new ProcessStartInfo("winget", args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        using var p = Process.Start(psi)!;
        var sb = new StringBuilder();
        p.OutputDataReceived += (_, e) => { if (e.Data is null) return; lock (sb) sb.AppendLine(e.Data); onLine?.Invoke(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data is null) return; lock (sb) sb.AppendLine(e.Data); onLine?.Invoke(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        await p.WaitForExitAsync();
        lock (sb) return (p.ExitCode, sb.ToString());
    }

    /// <summary>winget ids are dotted names; anything else (shortened "…" ids, quotes, spaces) is not passed to a command line.</summary>
    private static bool IsSafeId(string id) =>
        id.Length > 0 && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' or '+');
}
