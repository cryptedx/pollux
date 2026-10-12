using Pollux.Core.Files;

namespace Pollux.Core.ShellNew;

/// <summary>
/// The registered "New" file types, selected and named from raw ShellNew keys, cached until <see cref="Invalidate"/>.
/// Entries that only have a <c>Command</c>/<c>Handler</c>/<c>Directory</c> (shortcut, library, briefcase...) or no payload are left out,
/// as are template files that do not exist: they cannot be created from data alone. Folders are Pollux's own "New folder".
/// Templates and resource strings on another machine (UNC, URL) are never touched: the list is read at startup (ADR 0001).
/// </summary>
public sealed class ShellNewCatalog(IShellNewSource source, Func<string, string?> resolveIndirect, Func<string, string?> resolveTemplate)
{
    private readonly Lock _gate = new();
    private IReadOnlyList<ShellNewEntry>? _cache;

    /// <summary>The entries, ordered by display name. Reads the registry on first use (call off the UI thread).</summary>
    public IReadOnlyList<ShellNewEntry> Entries()
    {
        lock (_gate) return _cache ??= Build(source.Read(), resolveIndirect, resolveTemplate);
    }

    /// <summary>Drops the cache; the next <see cref="Entries"/> reads again.</summary>
    public void Invalidate()
    {
        lock (_gate) _cache = null;
    }

    /// <summary>Selection and naming without caching. The first usable key per extension wins.</summary>
    public static IReadOnlyList<ShellNewEntry> Build(IEnumerable<ShellNewRaw> raws, Func<string, string?> resolveIndirect, Func<string, string?> resolveTemplate)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entries = new List<ShellNewEntry>();
        foreach (var raw in raws)
        {
            if (raw.HasCommand || raw.Extension.Length < 2 || seen.Contains(raw.Extension)) continue;
            var entry = ToEntry(raw, resolveIndirect, resolveTemplate);
            if (entry is null) continue;
            seen.Add(raw.Extension);
            entries.Add(entry);
        }

        return [.. entries.OrderBy(e => e.DisplayName, StringComparer.CurrentCultureIgnoreCase).ThenBy(e => e.Extension, StringComparer.OrdinalIgnoreCase)];
    }

    private static ShellNewEntry? ToEntry(ShellNewRaw raw, Func<string, string?> resolveIndirect, Func<string, string?> resolveTemplate)
    {
        ShellNewKind kind;
        string? template = null;
        if (!string.IsNullOrWhiteSpace(raw.FileName))
        {
            if (!IsLocal(raw.FileName)) return null;
            template = resolveTemplate(raw.FileName);
            if (template is null) return null;
            kind = ShellNewKind.Template;
        }
        else if (raw.Data is { Length: > 0 })
        {
            kind = ShellNewKind.Data;
        }
        else if (raw.NullFile)
        {
            kind = ShellNewKind.Empty;
        }
        else
        {
            return null;
        }

        return new ShellNewEntry(raw.Extension, DisplayName(raw, resolveIndirect), kind, template, kind == ShellNewKind.Data ? raw.Data : null);
    }

    /// <summary>ItemName, FriendlyTypeName, the ProgID's description, then the extension in capitals.</summary>
    internal static string DisplayName(ShellNewRaw raw, Func<string, string?> resolveIndirect)
    {
        foreach (var candidate in new[] { raw.ItemName, raw.FriendlyTypeName, raw.ProgIdDescription })
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            var text = !candidate.StartsWith('@') ? candidate : IsLocal(candidate[1..].Split(',')[0]) ? resolveIndirect(candidate) : null;
            if (!string.IsNullOrWhiteSpace(text)) return text.Trim();
        }

        return raw.Extension[1..].ToUpperInvariant() + " File";
    }

    private static bool IsLocal(string location) => IconKeys.IsLocalLocation(location, static _ => false);
}
