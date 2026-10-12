using Pollux.App.Interop;
using Pollux.Core.ShellNew;

namespace Pollux.App.Services;

/// <summary>
/// The "New" menu's file types from the registry (Word document, Bitmap, ...), cached. Both calls do blocking I/O: await them (they run on the thread pool).
/// </summary>
internal sealed class ShellNewService
{
    private readonly ShellNewCatalog _catalog = new(new RegistryShellNewSource(), ShellIndirectString.Load, ResolveTemplate);

    /// <summary>The entries, ordered by display name. The registry is read on the first call and after <see cref="Refresh"/>.</summary>
    public async Task<IReadOnlyList<ShellNewEntry>> EntriesAsync() => Loaded = await Task.Run(_catalog.Entries);

    /// <summary>UI thread. The entries once <see cref="EntriesAsync"/> has completed, else null (the menu then offers only its built-in items).</summary>
    public IReadOnlyList<ShellNewEntry>? Loaded { get; private set; }

    /// <summary>Forgets the cached list (call when the "New" menu is about to open if a fresh list matters).</summary>
    public void Refresh()
    {
        _catalog.Invalidate();
        Loaded = null;
    }

    /// <summary>Creates the file for <paramref name="entry"/> in <paramref name="directory"/> and returns its full path; throws IOException/UnauthorizedAccessException.</summary>
    public Task<string> CreateAsync(string directory, ShellNewEntry entry) => ShellNewCreator.CreateAsync(directory, entry);

    private static string? ResolveTemplate(string fileName) => ShellNewTemplates.Resolve(
        fileName,
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "ShellNew"),
            Environment.GetFolderPath(Environment.SpecialFolder.Templates),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonTemplates),
        ],
        File.Exists);
}
