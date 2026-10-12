namespace Pollux.Core.ShellNew;

/// <summary>Naming and creating the file of a <see cref="ShellNewEntry"/>.</summary>
public static class ShellNewCreator
{
    /// <summary>"New Name.ext" (the name alone when it starts with New or Neu), then "New Name (2).ext", ... until <paramref name="exists"/> (a name in the folder) is false.</summary>
    public static string UniqueName(ShellNewEntry entry, Func<string, bool> exists)
    {
        var display = Sanitize(entry.DisplayName);
        // Registered item names are often already "new" names ("Neues Office Open XML-Dokument"), like Explorer uses them.
        var stem = display.StartsWith("New", StringComparison.OrdinalIgnoreCase) || display.StartsWith("Neu", StringComparison.OrdinalIgnoreCase) ? display : "New " + display;
        var name = stem + entry.Extension;
        for (var i = 2; exists(name); i++) name = $"{stem} ({i}){entry.Extension}";
        return name;
    }

    /// <summary>Display names come from the registry: invalid file name characters and trailing dots/spaces go.</summary>
    internal static string Sanitize(string displayName)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string([.. displayName.Select(c => Array.IndexOf(invalid, c) >= 0 ? ' ' : c)]);
        cleaned = string.Join(' ', cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries)).TrimEnd('.', ' ');
        return cleaned.Length == 0 ? "File" : cleaned;
    }

    /// <summary>
    /// Creates the file in <paramref name="directory"/> under a unique name and returns its full path. Never overwrites: a name taken
    /// meanwhile moves on to the next number. Blocking I/O; call off the UI thread (<see cref="CreateAsync"/>).
    /// </summary>
    public static string Create(string directory, ShellNewEntry entry)
    {
        for (var attempt = 0; ; attempt++)
        {
            var name = UniqueName(entry, n => File.Exists(Path.Combine(directory, n)) || Directory.Exists(Path.Combine(directory, n)));
            var path = Path.Combine(directory, name);
            try
            {
                WriteNew(path, entry);
                return path;
            }
            catch (IOException) when (attempt < 20 && File.Exists(path))
            {
            }
        }
    }

    public static Task<string> CreateAsync(string directory, ShellNewEntry entry) => Task.Run(() => Create(directory, entry));

    private static void WriteNew(string path, ShellNewEntry entry)
    {
        var created = false;
        try
        {
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            created = true;
            if (entry.Kind == ShellNewKind.Template)
            {
                using var template = new FileStream(entry.TemplatePath!, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                template.CopyTo(stream);
            }
            else if (entry.Kind == ShellNewKind.Data)
            {
                stream.Write(entry.Data!);
            }
        }
        catch
        {
            // A half-written file would show up as a new item; a name someone else took (CreateNew failed) is not ours to delete.
            if (created) TryDelete(path);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}

/// <summary>Finding a ShellNew <c>FileName</c> template.</summary>
public static class ShellNewTemplates
{
    /// <summary>An absolute name as is, a relative one in the first of <paramref name="folders"/> that has it; null if none exists.</summary>
    public static string? Resolve(string fileName, IEnumerable<string> folders, Func<string, bool> fileExists)
    {
        if (Path.IsPathRooted(fileName)) return fileExists(fileName) ? fileName : null;
        foreach (var folder in folders)
        {
            if (string.IsNullOrEmpty(folder)) continue;
            var candidate = Path.Combine(folder, fileName);
            if (fileExists(candidate)) return candidate;
        }

        return null;
    }
}
