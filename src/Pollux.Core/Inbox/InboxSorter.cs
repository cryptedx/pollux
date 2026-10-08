using Pollux.Core.Files;

namespace Pollux.Core.Inbox;

public sealed record InboxMove(string Source, string TargetDirectory, string Category);

/// <summary>Plans how the Inbox (Desktop) is emptied: files go into category folders below a target. Nothing moves until the plan is confirmed.</summary>
public static class InboxSorter
{
    private static readonly Dictionary<string, string> Categories = Build(
        ("Images", "png jpg jpeg gif bmp webp svg heic ico tif tiff"),
        ("Documents", "pdf doc docx xls xlsx ppt pptx odt ods txt rtf md csv vsdx one msg eml"),
        ("Archives", "zip 7z rar gz tar tgz bz2 xz iso"),
        ("Installers", "exe msi msix msixbundle appx appinstaller"),
        ("Code", "cs js ts py ps1 json xml yaml yml sql sh bat cmd html css java go rs cpp h sln csproj"),
        ("Media", "mp4 mov mkv avi mp3 wav m4a flac"),
        ("Shortcuts", "lnk url"));

    public static string CategoryOf(FileEntry e) =>
        e.IsDirectory ? "Folders" : Categories.GetValueOrDefault(e.Extension.ToLowerInvariant(), "Other");

    public static IReadOnlyList<InboxMove> Plan(IEnumerable<FileEntry> entries, string targetRoot, bool includeShortcuts = false, bool includeFolders = false) =>
        entries
            .Where(e => !e.IsHidden && !e.IsSystem && !e.Name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase))
            .Select(e => (Entry: e, Category: CategoryOf(e)))
            .Where(x => (includeShortcuts || x.Category != "Shortcuts") && (includeFolders || x.Category != "Folders"))
            .Select(x => new InboxMove(x.Entry.FullPath, Path.Combine(targetRoot, x.Category), x.Category))
            .ToList();

    private static Dictionary<string, string> Build(params (string Category, string Extensions)[] map)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (category, exts) in map)
        {
            foreach (var ext in exts.Split(' ')) d[ext] = category;
        }

        return d;
    }
}
