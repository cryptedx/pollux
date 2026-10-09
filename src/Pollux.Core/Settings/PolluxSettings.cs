namespace Pollux.Core.Settings;

public enum ThemeMode { System, Light, Dark }

public enum Density { Compact, Comfortable }

public enum SortColumn { Name, Modified, Type, Size }

/// <summary>Everything a user can configure in settings.json. Every property has a sensible default.</summary>
public sealed class PolluxSettings
{
    public AppearanceSettings Appearance { get; set; } = new();
    public AppSettings App { get; set; } = new();
    public FilesSettings Files { get; set; } = new();
    public TabsSettings Tabs { get; set; } = new();
    public SidebarSettings Sidebar { get; set; } = new();
    public RecentSettings Recent { get; set; } = new();
    public StatusBarSettings StatusBar { get; set; } = new();
    public TerminalSettings Terminal { get; set; } = new();
    public IntegrationSettings Integration { get; set; } = new();
    public FilterSettings Filter { get; set; } = new();
    public SearchSettings Search { get; set; } = new();
    public NotesSettings Notes { get; set; } = new();
    public DownloadsSettings Downloads { get; set; } = new();
    public UpdatesSettings Updates { get; set; } = new();
    public InboxSettings Inbox { get; set; } = new();
    public PreviewSettings Preview { get; set; } = new();
}

public sealed class PreviewSettings
{
    /// <summary>Show the preview pane next to the Panes (Alt+P).</summary>
    public bool PaneVisible { get; set; }
    public double PaneWidth { get; set; } = 380;
    /// <summary>Files larger than this are shown as hex instead of text.</summary>
    public int MaxTextMegabytes { get; set; } = 4;
}

public sealed class AppearanceSettings
{
    public ThemeMode Theme { get; set; } = ThemeMode.System;
    /// <summary>Hex color like "#0f6cbd"; null follows the Windows accent color.</summary>
    public string? AccentColor { get; set; }
    public Density Density { get; set; } = Density.Compact;
    public string UiFont { get; set; } = "Segoe UI Variable Text";
    public string MonoFont { get; set; } = "Cascadia Mono";
    public double FontSize { get; set; } = 13;
    public bool Mica { get; set; } = true;
}

public sealed class AppSettings
{
    /// <summary>Keep a background process with tray icon so new windows open instantly (ADR 0003).</summary>
    public bool Resident { get; set; } = true;
    public bool StartWithWindows { get; set; } = true;
    public bool RestoreWindows { get; set; } = true;
    public bool StartupLog { get; set; } = true;
    public bool TakeOverWinE { get; set; }
    public string GlobalHotkey { get; set; } = "win+alt+e";
}

public sealed class FilesSettings
{
    public bool ShowHidden { get; set; }
    public bool ShowSystem { get; set; }
    public bool ShowExtensions { get; set; } = true;
    public bool FoldersFirst { get; set; } = true;
    public SortColumn SortBy { get; set; } = SortColumn.Name;
    public bool SortDescending { get; set; }
    public string DateFormat { get; set; } = "yyyy-MM-dd HH:mm";
    public bool ConfirmDelete { get; set; }
    public bool ConfirmPermanentDelete { get; set; } = true;
    public bool SingleClickOpen { get; set; }
    /// <summary>Show git status letters (M/A/U/…) and dim ignored files in repositories. Runs git.exe locally.</summary>
    public bool GitStatus { get; set; } = true;
    /// <summary>"pollux": own transfer queue with progress in the status bar. "windows": the shell's copy dialogs.</summary>
    public string OperationEngine { get; set; } = "pollux";
    /// <summary>When a file already exists at the target (Pollux engine only).</summary>
    public Operations.ConflictPolicy ConflictPolicy { get; set; } = Operations.ConflictPolicy.Ask;
    /// <summary>Seconds before a slow (e.g. network) folder shows a timeout hint.</summary>
    public int SlowFolderSeconds { get; set; } = 5;
}

public sealed class TabsSettings
{
    /// <summary>"home" or an absolute folder path.</summary>
    public string NewTabLocation { get; set; } = "home";
    public bool DualPaneOnStart { get; set; } = true;
    public bool ShowTabCloseButtons { get; set; } = true;
}

public sealed class SidebarSettings
{
    public bool Visible { get; set; } = true;
    public double Width { get; set; } = 220;
    public bool ShowRecent { get; set; } = true;
    public bool ShowDrives { get; set; } = true;
    public int RecentCount { get; set; } = 5;
}

public sealed class RecentSettings
{
    public int MaxItems { get; set; } = 50;
    public List<string> Exclude { get; set; } = [];
}

public sealed class StatusBarSettings
{
    public bool Visible { get; set; } = true;
    public int RefreshSeconds { get; set; } = 2;
    public bool Cpu { get; set; } = true;
    public bool Gpu { get; set; } = true;
    public bool Vram { get; set; } = true;
    public bool Ram { get; set; } = true;
    public bool Disk { get; set; } = true;
    public bool Selection { get; set; } = true;
}

public sealed class TerminalSettings
{
    /// <summary>"wt" (Windows Terminal), "pwsh", "powershell" or "cmd".</summary>
    public string App { get; set; } = "wt";
    public string WindowsTerminalProfile { get; set; } = "";
    public string FallbackShell { get; set; } = "pwsh";
    /// <summary>Shell of the embedded terminal: "pwsh" (falls back to Windows PowerShell), "powershell" or "cmd".</summary>
    public string EmbeddedShell { get; set; } = "pwsh";
    public double PanelHeight { get; set; } = 260;
    public double FontSize { get; set; } = 13;
    /// <summary>cd the embedded terminal into the active pane's folder whenever it changes.</summary>
    public bool FollowPane { get; set; }
}

public sealed class IntegrationSettings
{
    public string VsCodeCommand { get; set; } = "code";
    /// <summary>Command used to open a folder in the GitHub Copilot app; "{path}" is replaced.</summary>
    public string CopilotAppCommand { get; set; } = "";
}

public sealed class FilterSettings
{
    public bool MatchInitials { get; set; } = true;
}

public sealed class SearchSettings
{
    public List<string> IndexRoots { get; set; } = [];
    public bool IndexBookmarks { get; set; } = true;
    public bool IndexDownloads { get; set; } = true;
    public bool IndexDesktop { get; set; } = true;
    public bool IndexDocuments { get; set; } = true;
    public List<string> ExcludeNames { get; set; } = ["node_modules", ".git", "bin", "obj", ".vs", "packages", "__pycache__", ".venv"];
    public int MaxResults { get; set; } = 50;
    public bool IndexNetworkPaths { get; set; }
}

public sealed class NotesSettings
{
    /// <summary>Empty means Documents\Pollux\Notes.</summary>
    public string Folder { get; set; } = "";
    public string GlobalHotkey { get; set; } = "win+alt+n";
    public double EditorFontSize { get; set; } = 14;
    public bool WordWrap { get; set; } = true;
}

public sealed class DownloadsSettings
{
    public string GlobalHotkey { get; set; } = "win+alt+d";
    public int MaxItems { get; set; } = 30;
    public bool GridView { get; set; } = true;
}

public sealed class UpdatesSettings
{
    /// <summary>Opt-in: runs winget, which talks to the network (ADR 0001).</summary>
    public bool CheckAutomatically { get; set; }
    public int CheckIntervalHours { get; set; } = 24;
    public List<string> IgnoredPackages { get; set; } = [];
}

public sealed class InboxSettings
{
    public bool HideDesktopIcons { get; set; }
    public string SortTarget { get; set; } = "";
}
