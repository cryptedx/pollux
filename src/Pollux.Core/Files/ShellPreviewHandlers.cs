using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Pollux.Core.Files;

/// <summary>The registry views the preview handler lookup reads (tests pass a fake).</summary>
public interface IAssociationRegistry
{
    /// <summary>A value of a key under HKEY_CLASSES_ROOT (null <paramref name="name"/> = the default value); null when missing.</summary>
    string? ClassesRoot(string key, string? name = null);

    /// <summary>Whether a key under HKEY_CLASSES_ROOT exists.</summary>
    bool ClassesRootHasKey(string key);

    /// <summary>The ProgID the user picked for <paramref name="extension"/> (with dot) in "Open with"; null when none.</summary>
    string? UserChoice(string extension);
}

/// <summary>
/// Finds the Windows preview handler (IPreviewHandler) Explorer would use for a file extension (ADR 0009): the
/// <c>ShellEx\{8895b1c6-…}</c> entry of the user's chosen ProgID, the default ProgID, the extension itself, then
/// <c>SystemFileAssociations</c> for the extension and its perceived type. Only handlers that can run out of process
/// in the prevhost.exe surrogate count; Pollux never loads handler code into its own process. Results are cached per
/// extension; a handler that failed or hung is skipped for the rest of the session. Thread-safe.
/// </summary>
public sealed class ShellPreviewHandlers(IAssociationRegistry registry)
{
    /// <summary>The IID of IPreviewHandler, which is also the ShellEx key a preview handler registers under.</summary>
    public const string ShellExKey = "{8895b1c6-b41f-4c1c-a562-0d564250836f}";

    private readonly Lock _gate = new();
    private readonly Dictionary<string, Guid?> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<Guid> _failed = [];

    /// <summary>The handler CLSID for <paramref name="extension"/> (without dot); null when there is none or it failed before.</summary>
    public Guid? Find(string extension)
    {
        if (extension.Length == 0) return null;
        Guid? clsid;
        lock (_gate)
        {
            if (!_cache.TryGetValue(extension, out clsid))
            {
                clsid = Lookup(extension);
                _cache[extension] = clsid;
            }

            if (clsid is { } c && _failed.Contains(c)) return null;
        }

        return clsid;
    }

    /// <summary>Remembers that <paramref name="clsid"/> failed or hung: Pollux's own preview is used for its types from now on.</summary>
    public void MarkFailed(Guid clsid)
    {
        lock (_gate) _failed.Add(clsid);
    }

    /// <summary>Forgets cached lookups (handlers installed meanwhile) but keeps the failed handlers.</summary>
    public void Refresh()
    {
        lock (_gate) _cache.Clear();
    }

    private Guid? Lookup(string extension)
    {
        var ext = "." + extension;
        var keys = new List<string>();
        if (registry.UserChoice(ext) is { Length: > 0 } chosen) keys.Add(chosen);
        if (registry.ClassesRoot(ext) is { Length: > 0 } progId) keys.Add(progId);
        keys.Add(ext);
        keys.Add(@"SystemFileAssociations\" + ext);
        if (registry.ClassesRoot(ext, "PerceivedType") is { Length: > 0 } perceived) keys.Add(@"SystemFileAssociations\" + perceived);

        foreach (var key in keys)
        {
            if (registry.ClassesRoot($@"{key}\ShellEx\{ShellExKey}") is not { } value) continue;
            if (!Guid.TryParse(value, out var clsid) || clsid == Guid.Empty) continue;
            // The first registration decides, like Explorer: an unusable handler is not replaced by a less specific one.
            return RunsOutOfProcess(clsid) ? clsid : null;
        }

        return null;
    }

    /// <summary>
    /// Whether the handler is activated in a surrogate (an AppID, normally prevhost's) or is a local server, and has not
    /// opted out of process isolation (<c>DisableProcessIsolation</c>, which would need loading it into Pollux).
    /// </summary>
    private bool RunsOutOfProcess(Guid clsid)
    {
        var key = $@"CLSID\{clsid:B}";
        if (registry.ClassesRoot(key, "DisableProcessIsolation") is "1") return false;
        return registry.ClassesRoot(key, "AppID") is { Length: > 0 } || registry.ClassesRootHasKey(key + @"\LocalServer32");
    }
}

/// <summary>The real registry. Nothing on other systems.</summary>
public sealed class SystemAssociationRegistry : IAssociationRegistry
{
    public static SystemAssociationRegistry Instance { get; } = new();

    public string? ClassesRoot(string key, string? name = null) =>
        OperatingSystem.IsWindows() ? Read(Registry.ClassesRoot, key, name) : null;

    public bool ClassesRootHasKey(string key)
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            using var k = Registry.ClassesRoot.OpenSubKey(key);
            return k is not null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }

    /// <summary>Windows 11 24H2 keeps the choice in <c>UserChoiceLatest</c>, earlier versions in <c>UserChoice</c>.</summary>
    public string? UserChoice(string extension)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var exts = $@"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\{extension}";
        return Read(Registry.CurrentUser, exts + @"\UserChoiceLatest", "ProgId") ?? Read(Registry.CurrentUser, exts + @"\UserChoice", "ProgId");
    }

    [SupportedOSPlatform("windows")]
    private static string? Read(RegistryKey root, string key, string? name)
    {
        try
        {
            using var k = root.OpenSubKey(key);
            return k?.GetValue(name) as string;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }
}
