namespace Pollux.Core.Operations;

/// <summary>
/// Undo and redo on the real disk. Deleting to the Recycle Bin is the app's (<paramref name="recycle"/>: the Windows
/// shell); restoring is done here, in the Recycle Bin folder <paramref name="recycleBinOf"/> gives for a path (null: that
/// drive has none).
/// </summary>
public sealed class DiskUndoTarget(Action<IReadOnlyList<string>> recycle, Func<string, string?> recycleBinOf) : IUndoTarget
{
    public DateTime UtcNow => DateTime.UtcNow;

    public bool FileExists(string path) => File.Exists(path);

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public bool IsEmptyFolder(string path) => Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any();

    public FileStamp? Stamp(string path) => FileStamp.Of(path);

    public IReadOnlySet<string> InRecycleBin(IReadOnlyList<string> paths, DateTime sinceUtc)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in paths.GroupBy(recycleBinOf))
        {
            if (group.Key is null) continue;
            var entries = RecycleBin.Read(group.Key, sinceUtc);
            foreach (var path in group.Where(p => RecycleBin.Find(entries, p, sinceUtc) is not null)) found.Add(path);
        }

        return found;
    }

    public void Move(string source, string destination)
    {
        if (Path.GetDirectoryName(destination) is { Length: > 0 } parent) Directory.CreateDirectory(parent);
        if (!Directory.Exists(source))
        {
            File.Move(source, destination);
        }
        else if (UndoEntry.SameDrive(source, destination))
        {
            Directory.Move(source, destination);
        }
        else
        {
            throw new IOException($"{UndoEntry.Name(source)} is a folder on another drive than {destination}; move it back by hand.");
        }
    }

    public void CreateFolder(string path) => Directory.CreateDirectory(path);

    public void RemoveFolder(string path) => Directory.Delete(path, recursive: false);

    public void Recycle(IReadOnlyList<string> paths) => recycle(paths);

    public void Restore(string path, DateTime sinceUtc)
    {
        var entry = recycleBinOf(path) is { } bin ? RecycleBin.Find(RecycleBin.Read(bin, sinceUtc), path, sinceUtc) : null;
        RecycleBin.Restore(entry ?? throw new IOException($"{UndoEntry.Name(path)} is no longer in the Recycle Bin"));
    }
}
