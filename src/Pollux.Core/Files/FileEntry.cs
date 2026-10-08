namespace Pollux.Core.Files;

public enum CloudState { None, CloudOnly, Local, AlwaysLocal }

public sealed record FileEntry(
    string Name,
    string FullPath,
    bool IsDirectory,
    long Size,
    DateTime Modified,
    FileAttributes Attributes)
{
    // OneDrive placeholder attributes (not in the FileAttributes enum).
    private const int Pinned = 0x00080000;
    private const int Unpinned = 0x00100000;
    private const int RecallOnDataAccess = 0x00400000;

    public bool IsHidden => Attributes.HasFlag(FileAttributes.Hidden);
    public bool IsSystem => Attributes.HasFlag(FileAttributes.System);

    public CloudState Cloud
    {
        get
        {
            var a = (int)Attributes;
            if ((a & Pinned) != 0) return CloudState.AlwaysLocal;
            if ((a & (RecallOnDataAccess | Unpinned)) != 0 || Attributes.HasFlag(FileAttributes.Offline)) return CloudState.CloudOnly;
            return Attributes.HasFlag(FileAttributes.ReparsePoint) && !IsDirectory ? CloudState.Local : CloudState.None;
        }
    }

    public string Extension
    {
        get
        {
            if (IsDirectory) return "";
            var i = Name.LastIndexOf('.');
            return i <= 0 ? "" : Name[(i + 1)..];
        }
    }
}
