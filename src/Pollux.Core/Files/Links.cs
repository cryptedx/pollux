namespace Pollux.Core.Files;

public enum LinkKind { Shortcut, Symlink, Junction }

/// <summary>Naming, validation and display text for links (.lnk shortcuts, symlinks, junctions). No file system access.</summary>
public static class Links
{
    private const int ErrorPrivilegeNotHeld = 1314;
    private static readonly string[] Reserved = ["CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"];

    /// <summary>"name - Shortcut.lnk", "name - Link.ext"; a number is added until <paramref name="exists"/> is false.</summary>
    public static string SuggestName(LinkKind kind, string sourceName, bool isDirectory, Func<string, bool> exists)
    {
        string stem, suffix, extension;
        if (kind == LinkKind.Shortcut)
        {
            (stem, suffix, extension) = (sourceName, " - Shortcut", ".lnk");
        }
        else
        {
            var dot = isDirectory ? -1 : sourceName.LastIndexOf('.');
            (stem, suffix, extension) = dot > 0 ? (sourceName[..dot], " - Link", sourceName[dot..]) : (sourceName, " - Link", "");
        }

        var name = stem + suffix + extension;
        for (var i = 2; exists(name); i++) name = $"{stem}{suffix} ({i}){extension}";
        return name;
    }

    /// <summary>Null if the name is usable as a file name, otherwise the reason.</summary>
    public static string? ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Name is empty";
        if (name.IndexOfAny(['\\', '/', ':', '*', '?', '"', '<', '>', '|']) >= 0) return "Name contains invalid characters";
        if (name.EndsWith('.') || name.EndsWith(' ')) return "Name must not end with a dot or space";
        var stem = name.Split('.')[0].TrimEnd();
        if (Reserved.Contains(stem, StringComparer.OrdinalIgnoreCase)) return "Name is reserved by Windows";
        return null;
    }

    /// <summary>Null if a link to <paramref name="sourcePath"/> may be created in <paramref name="destFolder"/>, otherwise the reason.</summary>
    public static string? Validate(LinkKind kind, string sourcePath, bool sourceIsDirectory, string destFolder)
    {
        if (string.IsNullOrWhiteSpace(sourcePath)) return "Nothing selected";
        if (string.IsNullOrWhiteSpace(destFolder)) return "No target folder";
        if (kind == LinkKind.Junction)
        {
            if (!sourceIsDirectory) return "Junctions can only point to folders";
            if (IsUnc(sourcePath) || IsUnc(destFolder)) return "Junctions only work with local folders";
        }

        if (kind != LinkKind.Shortcut && sourceIsDirectory && IsInside(sourcePath, destFolder)) return "Cannot create a link to a folder inside that folder";
        return null;
    }

    /// <summary>Message for a failed link creation; explains the Developer Mode requirement for symlinks.</summary>
    public static string ExplainError(LinkKind kind, int win32Error, string message) =>
        kind == LinkKind.Symlink && win32Error == ErrorPrivilegeNotHeld
            ? "Symlinks need Developer Mode (Windows Settings > System > For developers) or an elevated Pollux"
            : message;

    /// <summary>Tooltip text such as "Symlink → C:\x"; empty if the entry is not a link.</summary>
    public static string Describe(LinkKind? kind, string? target) =>
        kind is null || string.IsNullOrEmpty(target) ? "" : $"{kind} → {CleanTarget(target)}";

    /// <summary>Removes the NT namespace prefix junction targets may carry.</summary>
    public static string CleanTarget(string target) => target.StartsWith(@"\??\", StringComparison.Ordinal) ? target[4..] : target;

    private static bool IsUnc(string path) => path.StartsWith(@"\\", StringComparison.Ordinal) && !path.StartsWith(@"\\?\", StringComparison.Ordinal);

    private static bool IsInside(string folder, string candidate)
    {
        var a = folder.TrimEnd('\\', '/');
        var b = candidate.TrimEnd('\\', '/');
        return b.Equals(a, StringComparison.OrdinalIgnoreCase)
            || b.StartsWith(a + "\\", StringComparison.OrdinalIgnoreCase);
    }
}
