using System.Security.Principal;
using Pollux.Core.Operations;

namespace Pollux.App.Services;

/// <summary>
/// Connects the undo history (Core) to Windows: deleting goes through the shell's Recycle Bin, restoring reads the current
/// user's Recycle Bin folder of each drive ("X:\$Recycle.Bin\&lt;SID&gt;") directly.
/// </summary>
internal static class FileUndo
{
    private static readonly string? UserSid = WindowsIdentity.GetCurrent().User?.Value;

    /// <summary>The disk as undo and redo see it; <paramref name="hwnd"/> owns the shell's dialogs (e.g. "cannot be recycled").</summary>
    public static DiskUndoTarget Target(nint hwnd) =>
        new(paths => Shell.Delete(hwnd, paths, permanent: false, confirm: false), RecycleBinOf);

    /// <summary>The current user's Recycle Bin folder on the drive of <paramref name="path"/>; null for network paths.</summary>
    private static string? RecycleBinOf(string path)
    {
        if (UserSid is null || path.StartsWith(@"\\", StringComparison.Ordinal)) return null;
        var root = Path.GetPathRoot(path);
        return string.IsNullOrEmpty(root) ? null : Path.Combine(root, "$Recycle.Bin", UserSid);
    }
}
