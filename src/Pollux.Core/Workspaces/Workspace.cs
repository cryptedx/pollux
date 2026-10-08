using System.Text.Json.Serialization;
using Pollux.Core.Places;
using Pollux.Core.Session;

namespace Pollux.Core.Workspaces;

/// <summary>A saved working state: Pane layout, Tabs, Workspace-scoped Bookmarks and terminal start folder.</summary>
public sealed class Workspace
{
    public string Name { get; set; } = "";
    public WindowSession Window { get; set; } = new();
    public BookmarkList Bookmarks { get; set; } = new();
    public string TerminalFolder { get; set; } = "";
    public DateTime LastUsed { get; set; }
}

/// <summary>One JSON file per Workspace in the workspaces folder.</summary>
public sealed class WorkspaceStore(string directory)
{
    public string Directory { get; } = directory;

    public IReadOnlyList<Workspace> List()
    {
        if (!System.IO.Directory.Exists(Directory)) return [];
        return System.IO.Directory.EnumerateFiles(Directory, "*.json")
            .Select(f => JsonFile.Load(f, WorkspaceJson.Default.Workspace))
            .Where(w => w.Name.Length > 0)
            .OrderByDescending(w => w.LastUsed)
            .ToList();
    }

    public Workspace? Get(string name) =>
        List().FirstOrDefault(w => string.Equals(w.Name, name, StringComparison.OrdinalIgnoreCase));

    public void Save(Workspace workspace)
    {
        if (string.IsNullOrWhiteSpace(workspace.Name)) throw new ArgumentException("Workspace needs a name.", nameof(workspace));
        JsonFile.Save(FileFor(workspace.Name), workspace, WorkspaceJson.Default.Workspace);
    }

    public void Delete(string name)
    {
        var f = FileFor(name);
        if (File.Exists(f)) File.Delete(f);
    }

    public void Rename(string oldName, string newName)
    {
        var w = Get(oldName) ?? throw new InvalidOperationException($"No workspace '{oldName}'.");
        Delete(oldName);
        w.Name = newName;
        Save(w);
    }

    /// <summary>File for a Workspace. Names that needed sanitizing get a short hash, so "a:b" and "a?b" never share a file.</summary>
    public string FileFor(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = string.Concat(name.Select(c => invalid.Contains(c) || c is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|' ? '_' : c)).Trim();
        if (!string.Equals(safe, name.Trim(), StringComparison.Ordinal))
        {
            var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(name.ToLowerInvariant()));
            safe += "-" + Convert.ToHexString(hash, 0, 4).ToLowerInvariant();
        }

        return Path.Combine(Directory, safe + ".json");
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true, ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip, AllowTrailingCommas = true)]
[JsonSerializable(typeof(Workspace))]
public sealed partial class WorkspaceJson : JsonSerializerContext;
