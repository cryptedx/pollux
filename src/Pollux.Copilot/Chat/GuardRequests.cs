using System.Text.Json;
using GitHub.Copilot;
using Pollux.Core.Copilot.Guards;

namespace Pollux.Copilot.Chat;

/// <summary>
/// Turns what the CLI is about to do (a tool call seen by the pre-tool-use hook, or a permission request) into the
/// <see cref="ToolRequest"/> the Guard Rules judge. Built-in tool names and argument shapes are the CLI's; unknown tools
/// are inspected generically (every path-like and URL argument) and judged like a write when they name paths, so a new,
/// renamed, custom or MCP tool is still guarded (fail closed).
/// </summary>
public static class GuardRequests
{
    private static readonly HashSet<string> ShellTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "bash", "powershell", "pwsh", "shell", "cmd", "write_bash", "write_powershell",
    };

    private static readonly HashSet<string> WriteTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "edit", "create", "apply_patch", "str_replace", "str_replace_editor", "write", "write_file", "multi_edit", "delete", "move", "rename",
    };

    /// <summary>Tools known to only read what they name; any other tool with path arguments is judged like a write (fail closed).</summary>
    private static readonly HashSet<string> ReadTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "view", "read", "read_file", "show_file", "ls", "list_dir", "list_directory", "grep", "glob", "rg", "search", "find",
    };

    /// <summary>Read tools that search everything below their folder.</summary>
    private static readonly HashSet<string> RecursiveTools = new(StringComparer.OrdinalIgnoreCase) { "grep", "glob", "rg", "search", "find" };

    private static readonly HashSet<string> UrlTools = new(StringComparer.OrdinalIgnoreCase) { "web_fetch", "fetch", "web_search" };

    private static readonly string[] PatchFileMarkers = ["*** Update File:", "*** Add File:", "*** Delete File:", "*** Move to:"];

    /// <summary>A tool call as the pre-tool-use hook sees it: the tool's name and its arguments.</summary>
    public static ToolRequest FromHook(string toolName, JsonElement? args)
    {
        if (ShellTools.Contains(toolName))
        {
            var command = Text(args, "command") ?? Text(args, "input") ?? Text(args, "cmd") ?? (args is { ValueKind: JsonValueKind.String } s ? s.GetString() : null);
            return ToolRequest.Shell(command ?? "");
        }

        List<string> paths = [];
        List<string> urls = [];
        if (toolName.Equals("apply_patch", StringComparison.OrdinalIgnoreCase))
        {
            var patch = args is { ValueKind: JsonValueKind.String } p ? p.GetString() : Text(args, "input") ?? Text(args, "patch");
            paths.AddRange(PatchFiles(patch ?? ""));
        }

        if (args is { } a) Collect(a, null, paths, urls);

        // A search tool given no folder searches the current one (the Working Folder).
        if (RecursiveTools.Contains(toolName) && paths.Count == 0 && urls.Count == 0) paths.Add(".");
        var kind = UrlTools.Contains(toolName) ? ToolKind.Url
            : WriteTools.Contains(toolName) ? ToolKind.Write
            : paths.Count > 0 ? (ReadTools.Contains(toolName) ? ToolKind.Read : ToolKind.Write)
            : urls.Count > 0 ? ToolKind.Url
            : ToolKind.Other;
        return new ToolRequest(kind, null, paths.Distinct().ToList(), urls.Distinct().ToList())
        {
            Recursive = kind == ToolKind.Read && RecursiveTools.Contains(toolName),
        };
    }

    /// <summary>A CLI permission request (the fallback layer when the hook does not run).</summary>
    public static ToolRequest FromPermission(PermissionRequest request) => request switch
    {
        PermissionRequestShell s => new ToolRequest(ToolKind.Shell, s.FullCommandText, s.PossiblePaths ?? [], [.. (s.PossibleUrls ?? []).Select(u => u.Url)]),
        PermissionRequestRead r => ToolRequest.Read(r.Path),
        PermissionRequestWrite w => ToolRequest.Write(w.FileName),
        PermissionRequestUrl u => ToolRequest.Url(u.Url),
        PermissionRequestHook h => FromHook(h.ToolName, h.ToolArgs),
        PermissionRequestCustomTool c => FromHook(c.ToolName, c.Args),
        PermissionRequestMcp m => FromHook(m.ToolName, m.Args),
        _ => ToolRequest.Other(),
    };

    private static string? Text(JsonElement? args, string name) =>
        args is { ValueKind: JsonValueKind.Object } o && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static IEnumerable<string> PatchFiles(string patch)
    {
        foreach (var line in patch.Split('\n'))
        {
            var l = line.TrimEnd('\r');
            foreach (var marker in PatchFileMarkers)
            {
                if (l.StartsWith(marker, StringComparison.Ordinal) && l[marker.Length..].Trim() is { Length: > 0 } file)
                {
                    yield return file;
                }
            }
        }
    }

    /// <summary>Every URL anywhere in the arguments, and every string of a property named like a path or file.</summary>
    private static void Collect(JsonElement e, string? name, List<string> paths, List<string> urls)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var p in e.EnumerateObject()) Collect(p.Value, p.Name, paths, urls);
                break;
            case JsonValueKind.Array:
                foreach (var item in e.EnumerateArray()) Collect(item, name, paths, urls);
                break;
            case JsonValueKind.String:
                var value = e.GetString();
                if (string.IsNullOrWhiteSpace(value)) break;
                if (value.Contains("://", StringComparison.Ordinal) && !value.Contains('\n', StringComparison.Ordinal)) urls.Add(value);
                else if (name is not null && IsPathName(name)) paths.Add(value);
                break;
        }
    }

    private static bool IsPathName(string name) =>
        name.Contains("path", StringComparison.OrdinalIgnoreCase) || name.Contains("file", StringComparison.OrdinalIgnoreCase) && !name.Contains("text", StringComparison.OrdinalIgnoreCase)
        || name.Contains("dir", StringComparison.OrdinalIgnoreCase) || name.Equals("source", StringComparison.OrdinalIgnoreCase)
        || name.Equals("destination", StringComparison.OrdinalIgnoreCase) || name.Equals("target", StringComparison.OrdinalIgnoreCase);
}
