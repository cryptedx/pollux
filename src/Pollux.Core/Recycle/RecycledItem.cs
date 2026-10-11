using Pollux.Core.Files;
using Pollux.Core.Navigation;

namespace Pollux.Core.Recycle;

/// <summary>One entry of the Recycle Bin: where it was deleted from, when, and the "$R…" file that holds it now.</summary>
/// <param name="OriginalPath">Full path before deletion; just the name when Windows' record of it could not be read.</param>
/// <param name="RecycledPath">The file or folder inside $Recycle.Bin; identifies the entry for restore and delete.</param>
public sealed record RecycledItem(string OriginalPath, DateTime DeletedUtc, long Size, bool IsDirectory, string RecycledPath)
{
    public string Name => WinPath.Name(OriginalPath);

    public string OriginalFolder => WinPath.Parent(OriginalPath) ?? "";

    /// <summary>The row the Pane lists: the original name, the deletion time as its date, the recycled path as its identity.</summary>
    public FileEntry ToEntry() =>
        new(Name, RecycledPath, IsDirectory, Size, DeletedUtc.ToLocalTime(), IsDirectory ? FileAttributes.Directory : FileAttributes.Normal)
        {
            OriginalLocation = OriginalFolder,
        };
}
