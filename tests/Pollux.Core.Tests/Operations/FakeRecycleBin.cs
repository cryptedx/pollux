using System.Text;

namespace Pollux.Core.Tests.Operations;

/// <summary>
/// Recycles like Windows does into one folder: the entry becomes "$R&lt;id&gt;&lt;ext&gt;" and an "$I&lt;id&gt;&lt;ext&gt;"
/// file (format version 2) remembers its original path and the deletion time.
/// </summary>
internal sealed class FakeRecycleBin(string folder)
{
    private int _next;

    public string Folder { get; } = folder;

    public void Recycle(IReadOnlyList<string> paths)
    {
        Directory.CreateDirectory(Folder);
        foreach (var path in paths)
        {
            var isDir = Directory.Exists(path);
            if (!isDir && !File.Exists(path)) continue;
            var id = $"X{_next++:00000}" + (isDir ? "" : Path.GetExtension(path));
            var size = isDir ? 0 : new FileInfo(path).Length;
            if (isDir) Directory.Move(path, Path.Combine(Folder, "$R" + id));
            else File.Move(path, Path.Combine(Folder, "$R" + id));
            File.WriteAllBytes(Path.Combine(Folder, "$I" + id), InfoV2(path, size, DateTime.UtcNow));
        }
    }

    public static byte[] InfoV2(string originalPath, long size, DateTime deletedUtc)
    {
        var name = Encoding.Unicode.GetBytes(originalPath + "\0");
        var bytes = new byte[28 + name.Length];
        BitConverter.GetBytes(2L).CopyTo(bytes, 0);
        BitConverter.GetBytes(size).CopyTo(bytes, 8);
        BitConverter.GetBytes(deletedUtc.ToFileTimeUtc()).CopyTo(bytes, 16);
        BitConverter.GetBytes(originalPath.Length + 1).CopyTo(bytes, 24);
        name.CopyTo(bytes, 28);
        return bytes;
    }

    public static byte[] InfoV1(string originalPath, long size, DateTime deletedUtc)
    {
        var bytes = new byte[24 + 520];
        BitConverter.GetBytes(1L).CopyTo(bytes, 0);
        BitConverter.GetBytes(size).CopyTo(bytes, 8);
        BitConverter.GetBytes(deletedUtc.ToFileTimeUtc()).CopyTo(bytes, 16);
        Encoding.Unicode.GetBytes(originalPath).CopyTo(bytes, 24);
        return bytes;
    }
}
