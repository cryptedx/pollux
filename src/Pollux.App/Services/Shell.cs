using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Pollux.App.Interop;
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

    public static void OpenWith(string path) =>
        Start("rundll32.exe", $"shell32.dll,OpenAs_RunDLL {path}");

    public static void ShowProperties(nint hwnd, string path) =>
        Native.SHObjectProperties(hwnd, Native.SHOP_FILEPATH, path, null);

    public static void RevealInExplorer(string path) =>
        Start("explorer.exe", $"/select,\"{path}\"");

    public static void OpenTerminal(string folder, TerminalSettings settings)
    {
        var app = settings.App.ToLowerInvariant();
        if (app == "wt" && TryStart("wt.exe", string.IsNullOrWhiteSpace(settings.WindowsTerminalProfile)
                ? $"-d {Quote(folder)}"
                : $"-p \"{settings.WindowsTerminalProfile}\" -d {Quote(folder)}", folder))
        {
            return;
        }

        var shell = app is "pwsh" or "powershell" or "cmd" ? app : settings.FallbackShell;
        OpenShell(folder, shell);
    }

    /// <summary>Runs the Copilot CLI login in a terminal window; false when there is no CLI to run.</summary>
    public static bool OpenCopilotLogin(Pollux.Copilot.CopilotCli cli)
    {
        var line = Pollux.Copilot.Connection.CopilotSignIn.TerminalCommandLine(cli, File.Exists);
        return line is not null && TryStart("cmd.exe", line);
    }

    public static void OpenShell(string folder, string shell)
    {
        if (shell == "pwsh" && TryStart("pwsh.exe", "-NoExit", folder)) return;
        if (shell == "cmd" && TryStart("cmd.exe", "", folder)) return;
        TryStart("powershell.exe", "-NoExit", folder);
    }

    public static bool OpenInVsCode(string path, string command) =>
        TryStart("cmd.exe", $"/c {command} {Quote(path)}", Path.GetDirectoryName(path) ?? path, hidden: true);

    /// <summary>The Copilot app's command line is configurable because it changes between releases; "{path}" is replaced.</summary>
    public static bool OpenInCopilot(string path, string commandTemplate)
    {
        if (string.IsNullOrWhiteSpace(commandTemplate)) return false;
        var cmd = commandTemplate.Contains("{path}", StringComparison.Ordinal)
            ? commandTemplate.Replace("{path}", Quote(path), StringComparison.Ordinal)
            : $"{commandTemplate} {Quote(path)}";
        return TryStart("cmd.exe", $"/c start \"\" {cmd}", path, hidden: true);
    }

    public static bool TryStart(string file, string args, string workingDirectory = "", bool hidden = false)
    {
        try
        {
            Process.Start(new ProcessStartInfo(file, args)
            {
                UseShellExecute = !hidden,
                CreateNoWindow = hidden,
                WorkingDirectory = workingDirectory,
            });
            return true;
        }
        catch (Win32Exception)
        {
            return false;
        }
    }

    private static void Start(string file, string args) => TryStart(file, args);

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
