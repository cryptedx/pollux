using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Pollux.App.Interop;
using Pollux.Core.Launch;
using Pollux.Core.Settings;

namespace Pollux.App.Services;

/// <summary>Everything that hands work to Windows: launching files and tools, and file operations through the shell.</summary>
internal static unsafe class Shell
{
    /// <summary>Quotes a path for a command line; a trailing backslash is doubled so it does not escape the closing quote.</summary>
    public static string Quote(string path) => "\"" + (path.EndsWith('\\') ? path + "\\" : path) + "\"";

    /// <summary>Opens an http(s) link in the default browser; anything else is ignored.</summary>
    public static void OpenUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)) return;
        try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
        catch (Win32Exception) { }
    }

    public static void Open(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(path) ?? "" });
        }
        catch (Win32Exception)
        {
            // No associated app: ask Windows to show "Open with".
            OpenWith(path);
        }
    }

    public static void OpenWith(string path) => Launch(new LaunchIntent.OpenWith(path));

    public static void ShowProperties(nint hwnd, string path) =>
        Native.SHObjectProperties(hwnd, Native.SHOP_FILEPATH, path, null);

    public static void RevealInExplorer(string path) => Launch(new LaunchIntent.RevealInExplorer(path));

    /// <summary>
    /// What tool plans depend on: a PATH lookup that never searches the current directory or Pollux's own folder,
    /// the system directory (SECURITY.md S-02, S-08), and whether this is the Admin Instance, whose plans ignore the
    /// command and terminal settings (S-09).
    /// </summary>
    public static LaunchContext LaunchContext { get; } = new(
        name => PathLookup.Find(name, Environment.GetEnvironmentVariable("PATH"), Environment.GetEnvironmentVariable("PATHEXT"),
            [AppContext.BaseDirectory, Environment.CurrentDirectory], File.Exists),
        Environment.SystemDirectory,
        Views.MainWindow.IsElevated,
        [Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)]);

    /// <summary>Opens a terminal in <paramref name="folder"/> as the terminal settings say; null, or a message to show.</summary>
    public static string? OpenTerminal(string folder, TerminalSettings settings) =>
        Launch(new LaunchIntent.OpenTerminal(folder, settings.App, settings.WindowsTerminalProfile, settings.FallbackShell));

    /// <summary>Runs the Copilot CLI login in a terminal window; false when there is no CLI to run.</summary>
    public static bool OpenCopilotLogin(Pollux.Copilot.CopilotCli cli)
    {
        var path = Pollux.Copilot.Connection.CopilotSignIn.CliPath(cli, File.Exists);
        return path is not null && Launch(new LaunchIntent.CopilotLogin(path)) is null;
    }

    /// <summary>Opens <paramref name="shell"/> ("pwsh", "powershell" or "cmd") in <paramref name="folder"/>; null, or a message to show.</summary>
    public static string? OpenShell(string folder, string shell) =>
        Launch(new LaunchIntent.OpenShell(folder, shell));

    /// <summary>Opens a file or folder in VS Code; null, or a message naming what is missing.</summary>
    public static string? OpenInVsCode(string path, string command) =>
        Launch(new LaunchIntent.OpenInVsCode(path, command));

    /// <summary>The Copilot app's command line is configurable because it changes between releases; "{path}" is replaced.</summary>
    public static string? OpenInCopilot(string path, string commandTemplate) =>
        Launch(new LaunchIntent.OpenInCopilotApp(path, commandTemplate));

    /// <summary>The embedded terminal's shell in this process's environment; there always is one.</summary>
    public static LaunchPlan.Run PlanEmbeddedTerminal(string folder, string shell) =>
        LaunchPlanner.PlanEmbeddedTerminal(new LaunchIntent.EmbeddedTerminal(folder, shell), LaunchContext);

    /// <summary>The launch plan for <paramref name="intent"/> in this process's environment.</summary>
    public static LaunchPlan Plan(LaunchIntent intent) => LaunchPlanner.Plan(intent, LaunchContext);

    /// <summary>Plans and executes <paramref name="intent"/>; null when the tool started, else a message naming the tool.</summary>
    public static string? Launch(LaunchIntent intent) => Execute(LaunchPlanner.Plan(intent, LaunchContext));

    /// <summary>Executes a launch plan; which program runs, with what, and where is decided by the plan alone.</summary>
    public static string? Execute(LaunchPlan plan)
    {
        if (plan is not LaunchPlan.Run run) return ((LaunchPlan.Missing)plan).Message;
        try
        {
            Process.Start(new ProcessStartInfo(run.Executable, run.Arguments)
            {
                UseShellExecute = !run.Hidden,
                CreateNoWindow = run.Hidden,
                WorkingDirectory = run.WorkingDirectory,
                Verb = run.Elevated ? "runas" : "",
            })?.Dispose();
            return null;
        }
        catch (Win32Exception ex) when (run.Elevated && ex.NativeErrorCode == 1223)
        {
            return "elevation cancelled";
        }
        catch (Win32Exception ex)
        {
            return $"Could not start {run.Tool} ({run.Executable}): {ex.Message}";
        }
    }

    /// <summary>
    /// Starts a helper whose output Pollux reads (git, winget, mklink): the plan's executable and folder, no shell, no
    /// window, standard output (and with <paramref name="redirectStdErr"/> standard error) redirected, decoded as
    /// <paramref name="encoding"/> (null: the console's code page). Null, with <paramref name="missing"/> saying why,
    /// when the plan has no tool or the process did not start; a Win32Exception from the start itself propagates.
    /// </summary>
    public static Process? StartHelper(LaunchIntent intent, bool redirectStdErr, Encoding? encoding, out string? missing,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        var plan = Plan(intent);
        if (plan is not LaunchPlan.Run run)
        {
            missing = (plan as LaunchPlan.Missing)?.Message;
            return null;
        }

        var psi = new ProcessStartInfo(run.Executable, run.Arguments)
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = run.WorkingDirectory,
            RedirectStandardOutput = true, RedirectStandardError = redirectStdErr,
            StandardOutputEncoding = encoding, StandardErrorEncoding = redirectStdErr ? encoding : null,
        };
        foreach (var (name, value) in environment ?? new Dictionary<string, string>()) psi.Environment[name] = value;
        var process = Process.Start(psi);
        missing = process is null ? $"Could not start {run.Tool}" : null;
        return process;
    }

    // ---- file operations (SHFileOperation: native progress/conflict dialogs, recycle bin, undo in Explorer) ----

    /// <param name="renameOnCollision">Give an entry whose name exists in the target a new name instead of asking whether to replace it.</param>
    public static bool Copy(nint hwnd, IReadOnlyList<string> sources, string targetDirectory, bool renameOnCollision = false) =>
        Run(hwnd, Native.FO_COPY, sources, targetDirectory, (ushort)(Native.FOF_ALLOWUNDO | Native.FOF_NOCONFIRMMKDIR
            | (renameOnCollision ? Native.FOF_RENAMEONCOLLISION : RenameOnCollisionIfSameFolder(sources, targetDirectory))));

    /// <summary>
    /// Copies or moves <paramref name="sources"/> into <paramref name="targetDirectory"/>, giving an entry whose name is
    /// taken there a new name (never replacing anything); <paramref name="renamed"/> lists those entries with their new
    /// path (from the shell's name mappings), so the caller can report where each entry really went.
    /// </summary>
    public static bool CopyOrMoveRenamingOnCollision(nint hwnd, bool move, IReadOnlyList<string> sources, string targetDirectory, out IReadOnlyList<(string OldPath, string NewPath)> renamed) =>
        Run(hwnd, move ? Native.FO_MOVE : Native.FO_COPY, sources, targetDirectory,
            (ushort)(Native.FOF_ALLOWUNDO | Native.FOF_NOCONFIRMMKDIR | Native.FOF_RENAMEONCOLLISION | Native.FOF_WANTMAPPINGHANDLE), out renamed);

    /// <param name="renameOnCollision">Give an entry whose name exists in the target a new name instead of asking whether to replace it.</param>
    public static bool Move(nint hwnd, IReadOnlyList<string> sources, string targetDirectory, bool renameOnCollision = false) =>
        Run(hwnd, Native.FO_MOVE, sources, targetDirectory, (ushort)(Native.FOF_ALLOWUNDO | Native.FOF_NOCONFIRMMKDIR
            | (renameOnCollision ? Native.FOF_RENAMEONCOLLISION : 0)));

    /// <summary>
    /// <see cref="Copy(nint, IReadOnlyList{string}, string, bool)"/> (renaming only within the same folder) that also reports
    /// the entries the shell gave a new name (<paramref name="renamed"/>), so undo knows what the copy created.
    /// </summary>
    public static bool Copy(nint hwnd, IReadOnlyList<string> sources, string targetDirectory, out IReadOnlyList<(string OldPath, string NewPath)> renamed) =>
        Run(hwnd, Native.FO_COPY, sources, targetDirectory, (ushort)(Native.FOF_ALLOWUNDO | Native.FOF_NOCONFIRMMKDIR | Native.FOF_WANTMAPPINGHANDLE
            | RenameOnCollisionIfSameFolder(sources, targetDirectory)), out renamed);

    /// <summary><see cref="Move(nint, IReadOnlyList{string}, string, bool)"/> that also reports the entries the shell gave a new name.</summary>
    public static bool Move(nint hwnd, IReadOnlyList<string> sources, string targetDirectory, out IReadOnlyList<(string OldPath, string NewPath)> renamed) =>
        Run(hwnd, Native.FO_MOVE, sources, targetDirectory, (ushort)(Native.FOF_ALLOWUNDO | Native.FOF_NOCONFIRMMKDIR | Native.FOF_WANTMAPPINGHANDLE), out renamed);

    public static bool Delete(nint hwnd, IReadOnlyList<string> paths, bool permanent, bool confirm)
    {
        // WANTNUKEWARNING also on recycle-bin deletes: items that cannot be recycled must never vanish silently.
        ushort flags = permanent ? Native.FOF_WANTNUKEWARNING : (ushort)(Native.FOF_ALLOWUNDO | Native.FOF_WANTNUKEWARNING);
        if (!confirm) flags = (ushort)(flags | Native.FOF_NOCONFIRMATION);
        return Run(hwnd, Native.FO_DELETE, paths, null, flags);
    }

    public static bool Rename(nint hwnd, string path, string newName)
    {
        var target = Path.Combine(Path.GetDirectoryName(path)!, newName);
        return Run(hwnd, Native.FO_RENAME, [path], target, Native.FOF_ALLOWUNDO);
    }

    private static ushort RenameOnCollisionIfSameFolder(IReadOnlyList<string> sources, string target) =>
        sources.Any(s => string.Equals(Path.GetDirectoryName(s), target.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            ? Native.FOF_RENAMEONCOLLISION
            : (ushort)0;

    private static bool Run(nint hwnd, uint func, IReadOnlyList<string> from, string? to, ushort flags) => Run(hwnd, func, from, to, flags, out _);

    private static bool Run(nint hwnd, uint func, IReadOnlyList<string> from, string? to, ushort flags, out IReadOnlyList<(string OldPath, string NewPath)> renamed)
    {
        renamed = [];
        if (from.Count == 0) return false;
        var pFrom = DoubleNullTerminated(from);
        var pTo = to is null ? 0 : DoubleNullTerminated([to]);
        var op = new Native.SHFILEOPSTRUCTW { hwnd = hwnd, wFunc = func, pFrom = pFrom, pTo = pTo, fFlags = flags };
        try
        {
            var result = Native.SHFileOperationW(&op);
            renamed = NameMappings(op.hNameMappings);
            return result == 0 && op.fAnyOperationsAborted == 0;
        }
        finally
        {
            if (op.hNameMappings != 0) Native.SHFreeNameMappings(op.hNameMappings);
            Marshal.FreeHGlobal(pFrom);
            if (pTo != 0) Marshal.FreeHGlobal(pTo);
        }
    }

    /// <summary>The entries the shell renamed on collision (old path, new path), from a FOF_WANTMAPPINGHANDLE handle.</summary>
    private static List<(string OldPath, string NewPath)> NameMappings(nint handle)
    {
        List<(string, string)> mappings = [];
        if (handle == 0) return mappings;
        var list = (Native.SHNAMEMAPPINGHANDLE*)handle;
        for (var i = 0; i < list->uNumberOfMappings; i++)
        {
            var m = list->lpSHNameMapping[i];
            mappings.Add((Text(m.pszOldPath, m.cchOldPath), Text(m.pszNewPath, m.cchNewPath)));
        }

        return mappings;

        static string Text(char* text, int length) =>
            text == null ? "" : (length > 0 ? new string(text, 0, length) : new string(text)).TrimEnd('\0');
    }

    private static nint DoubleNullTerminated(IEnumerable<string> paths)
    {
        var sb = new StringBuilder();
        foreach (var p in paths) sb.Append(p).Append('\0');
        sb.Append('\0');
        return Marshal.StringToHGlobalUni(sb.ToString());
    }

    public static string UniqueName(string directory, string baseName, string extension = "")
    {
        var name = baseName + extension;
        for (var i = 2; File.Exists(Path.Combine(directory, name)) || Directory.Exists(Path.Combine(directory, name)); i++)
        {
            name = $"{baseName} ({i}){extension}";
        }

        return name;
    }
}
