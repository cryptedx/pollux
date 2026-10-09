namespace Pollux.Core;

/// <summary>Writes via a temp file and a rename so a crash never leaves a half-written file behind.</summary>
public static class AtomicFile
{
    public static void WriteAllText(string path, string contents)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, contents);
        File.Move(temp, path, overwrite: true);
    }

    public static void WriteAllBytes(string path, byte[] contents)
    {
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, contents);
        File.Move(temp, path, overwrite: true);
    }
}
