using Pollux.App.Interop;
using Pollux.Core.Files;
using Pollux.Core.Launch;

namespace Pollux.App.Services;

/// <summary>Creates and reads shortcuts, symbolic links and junctions. Pure naming and validation live in <see cref="Links"/>.</summary>
internal static class LinkTools
{
    /// <summary>Creates the link; returns an error text for the user, or null on success.</summary>
    public static string? Create(LinkKind kind, string sourcePath, bool sourceIsDirectory, string linkPath)
    {
        try
        {
            switch (kind)
            {
                case LinkKind.Shortcut:
                    return ShellLink.Create(linkPath, sourcePath, sourceIsDirectory ? sourcePath : Path.GetDirectoryName(sourcePath) ?? "");
                case LinkKind.Symlink:
                    if (sourceIsDirectory) Directory.CreateSymbolicLink(linkPath, sourcePath);
                    else File.CreateSymbolicLink(linkPath, sourcePath);
                    return null;
                default:
                    return CreateJunction(linkPath, sourcePath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Links.ExplainError(kind, ex.HResult & 0xFFFF, ex.Message);
        }
    }

    /// <summary>Tooltip text for a shortcut, symlink or junction entry; empty for everything else. Best effort, never throws.</summary>
    public static string Describe(FileEntry entry)
    {
        try
        {
            if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                FileSystemInfo info = entry.IsDirectory ? new DirectoryInfo(entry.FullPath) : new FileInfo(entry.FullPath);
                if (info.LinkTarget is { } raw)
                {
                    // Junction targets may carry the NT prefix, symlinks are stored as given (best-effort distinction).
                    var kind = entry.IsDirectory && raw.StartsWith(@"\??\", StringComparison.Ordinal) ? LinkKind.Junction : LinkKind.Symlink;
                    return Links.Describe(kind, raw);
                }
            }

            if (!entry.IsDirectory && entry.Extension.Equals("lnk", StringComparison.OrdinalIgnoreCase))
            {
                return DescribeShortcut(entry);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
        }

        return "";
    }

    // The row template asks for the tooltip of every shortcut row it realizes, on the UI thread, each time the list is built
    // again. Reading a .lnk is a COM object plus a file read, so the text is kept until the file changes.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(string Path, DateTime Modified, long Size), string> ShortcutTips = new();

    private static string DescribeShortcut(FileEntry entry)
    {
        // Never reach out to a network share just to fill a tooltip.
        if (Pollux.Core.Navigation.WinPath.IsUnc(entry.FullPath) || Drives.For(entry.FullPath)?.Type == DriveType.Network) return "";
        var key = (entry.FullPath, entry.Modified, entry.Size);
        if (ShortcutTips.TryGetValue(key, out var cached)) return cached;
        var text = Links.Describe(LinkKind.Shortcut, ShellLink.ReadTarget(entry.FullPath));
        if (ShortcutTips.Count > 4000) ShortcutTips.Clear();
        ShortcutTips[key] = text;
        return text;
    }

    // There is no managed API for junctions; mklink is a cmd builtin and runs locally.
    private static string? CreateJunction(string linkPath, string targetPath)
    {
        using var process = Shell.StartHelper(new LaunchIntent.CreateJunction(linkPath, targetPath), redirectStdErr: true, encoding: null, out var missing);
        if (process is null) return missing ?? "Could not start cmd.exe";
        var output = process.StandardError.ReadToEnd() + process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode == 0 ? null : output.Trim();
    }
}
