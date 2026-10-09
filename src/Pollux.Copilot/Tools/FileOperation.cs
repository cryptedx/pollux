using System.Buffers;
using System.Text.Json;
using Pollux.Core.Copilot.Guards;
using Pollux.Core.Navigation;

namespace Pollux.Copilot.Tools;

/// <summary>What a Pollux file tool does. There is deliberately no permanent delete: removing goes to the recycle bin.</summary>
public enum FileOperationKind
{
    Copy,
    Move,
    Rename,
    Recycle,
    NewFolder,
    AddBookmark,
}

/// <summary>
/// One call of a Pollux file tool, validated: full Windows paths only (no relative paths, no "." or ".." segments), plain
/// names (no separators or characters Windows forbids). <see cref="Paths"/> are the sources (Copy, Move, Recycle), the
/// entry (Rename, AddBookmark) or the parent folder (NewFolder); <see cref="Destination"/> is the target folder of Copy
/// and Move; <see cref="Name"/> the new name (Rename), the folder name (NewFolder) or the Bookmark's name.
/// </summary>
public sealed record FileOperation(FileOperationKind Kind, IReadOnlyList<string> Paths, string? Destination = null, string? Name = null, string? Group = null)
{
    /// <summary>The tool behind each kind.</summary>
    public static IReadOnlyDictionary<string, FileOperationKind> Tools { get; } = new Dictionary<string, FileOperationKind>(StringComparer.Ordinal)
    {
        ["pollux_copy"] = FileOperationKind.Copy,
        ["pollux_move"] = FileOperationKind.Move,
        ["pollux_rename"] = FileOperationKind.Rename,
        ["pollux_recycle"] = FileOperationKind.Recycle,
        ["pollux_new_folder"] = FileOperationKind.NewFolder,
        ["pollux_add_bookmark"] = FileOperationKind.AddBookmark,
    };

    private static readonly SearchValues<char> InvalidNameChars = SearchValues.Create("\\/:*?\"<>|");
    private static readonly SearchValues<char> WildcardChars = SearchValues.Create("*?\"<>|");

    /// <summary>The tool's name.</summary>
    public string Tool => Tools.First(t => t.Value == Kind).Key;

    /// <summary>The paths the operation creates or changes on disk (empty for a Bookmark).</summary>
    public IReadOnlyList<string> Targets => Kind switch
    {
        FileOperationKind.Copy or FileOperationKind.Move => [.. Paths.Select(p => WinPath.Combine(Destination!, WinPath.Name(p)))],
        FileOperationKind.Rename => [WinPath.Combine(WinPath.Parent(Paths[0])!, Name!)],
        FileOperationKind.NewFolder => [WinPath.Combine(Paths[0], Name!)],
        _ => [],
    };

    /// <summary>
    /// Where the entries of a copy or move really went: <see cref="Targets"/>, except that an entry the shell gave a new
    /// name because its name was taken (<paramref name="renamed"/>, matched by its source or its intended target) is
    /// reported under that name.
    /// </summary>
    public IReadOnlyList<string> TargetsAfter(IReadOnlyList<RenamedEntry> renamed)
    {
        ArgumentNullException.ThrowIfNull(renamed);
        if (Kind is not (FileOperationKind.Copy or FileOperationKind.Move)) return Targets;
        return [.. Paths.Zip(Targets, (source, target) =>
            renamed.FirstOrDefault(r => Same(r.OldPath, source) || Same(r.OldPath, target)) is { } r ? WinPath.Normalize(r.NewPath) : target)];

        static bool Same(string a, string b) => string.Equals(WinPath.Normalize(a), b, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// What the Guard Rules judge: the sources a copy reads; every path a move, rename or recycle changes (sources and
    /// targets), and what copy and new folder create, as writes (Working Folder, Tainted Chat, protected secrets); a
    /// Bookmark changes no file, only its path is checked against the protected secrets.
    /// </summary>
    public IReadOnlyList<ToolRequest> GuardRequests => Kind switch
    {
        FileOperationKind.Copy => [ToolRequest.Read([.. Paths]), ToolRequest.Write([.. Targets])],
        FileOperationKind.Move or FileOperationKind.Rename => [ToolRequest.Write([.. Paths, .. Targets])],
        FileOperationKind.Recycle => [ToolRequest.Write([.. Paths])],
        FileOperationKind.NewFolder => [ToolRequest.Write([.. Targets])],
        _ => [new ToolRequest(ToolKind.Other, null, Paths, [])],
    };

    /// <summary>A one-line title for the approval prompt.</summary>
    public string Title => Kind switch
    {
        FileOperationKind.Copy => $"Copy {Count(Paths.Count)}",
        FileOperationKind.Move => $"Move {Count(Paths.Count)}",
        FileOperationKind.Rename => "Rename an entry",
        FileOperationKind.Recycle => $"Move {Count(Paths.Count)} to the recycle bin",
        FileOperationKind.NewFolder => "Create a folder",
        _ => "Add a Bookmark",
    };

    /// <summary>Exactly what happens, for the approval prompt and the action log.</summary>
    public string Describe() => Kind switch
    {
        FileOperationKind.Copy => $"copy {string.Join(", ", Paths)} -> {Destination}",
        FileOperationKind.Move => $"move {string.Join(", ", Paths)} -> {Destination}",
        FileOperationKind.Rename => $"rename {Paths[0]} -> {Name}",
        FileOperationKind.Recycle => $"recycle {string.Join(", ", Paths)}",
        FileOperationKind.NewFolder => $"new folder {Targets[0]}",
        _ => $"bookmark {Paths[0]}" + (string.IsNullOrEmpty(Name) ? "" : $" as {Name}") + (string.IsNullOrEmpty(Group) ? "" : $" in {Group}"),
    };

    private static string Count(int n) => n == 1 ? "1 entry" : $"{n} entries";

    /// <summary>Validates a tool call; <paramref name="error"/> says what is wrong (for Copilot) when it returns null.</summary>
    public static FileOperation? Create(FileOperationKind kind, IReadOnlyList<string>? paths, string? destination, string? name, string? group, out string? error)
    {
        error = null;
        var list = (paths ?? []).Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        if (list.Count == 0) return Fail("No path given.", out error);
        if (list.FirstOrDefault(p => !IsFullPath(p)) is { } relative) return Fail($"\"{relative}\" is not a full Windows path (like C:\\folder\\file.txt).", out error);
        list = [.. list.Select(WinPath.Normalize).Distinct(StringComparer.OrdinalIgnoreCase)];
        if (list.FirstOrDefault(p => WinPath.Parent(p) is null) is { } root && kind != FileOperationKind.AddBookmark && kind != FileOperationKind.NewFolder)
        {
            return Fail($"{root} is a drive or share root; it cannot be copied, moved, renamed or recycled.", out error);
        }

        switch (kind)
        {
            case FileOperationKind.Copy or FileOperationKind.Move:
                if (string.IsNullOrWhiteSpace(destination)) return Fail("No destination folder given.", out error);
                if (!IsFullPath(destination)) return Fail($"\"{destination}\" is not a full Windows folder path.", out error);
                destination = WinPath.Normalize(destination);
                if (list.FirstOrDefault(p => WinPath.IsSameOrUnder(destination, p)) is { } into)
                {
                    return Fail($"{into} cannot be {(kind == FileOperationKind.Copy ? "copied" : "moved")} into itself.", out error);
                }

                if (kind == FileOperationKind.Move && list.FirstOrDefault(p => string.Equals(WinPath.Parent(p), destination, StringComparison.OrdinalIgnoreCase)) is { } already)
                {
                    return Fail($"{already} is already in {destination}.", out error);
                }

                return new(kind, list, destination);
            case FileOperationKind.Rename:
                if (list.Count != 1) return Fail("Rename takes exactly one path.", out error);
                if (!IsPlainName(name, out error)) return null;
                return new(kind, list, null, name!.Trim());
            case FileOperationKind.NewFolder:
                if (list.Count != 1) return Fail("Give exactly one parent folder.", out error);
                if (!IsPlainName(name, out error)) return null;
                return new(kind, list, null, name!.Trim());
            case FileOperationKind.AddBookmark:
                if (list.Count != 1) return Fail("Bookmark exactly one path.", out error);
                return new(kind, list, null, string.IsNullOrWhiteSpace(name) ? null : name.Trim(), string.IsNullOrWhiteSpace(group) ? null : group.Trim());
            default:
                return new(kind, list);
        }
    }

    /// <summary>
    /// The operation of a Pollux file tool call as the CLI reports it (pre-tool-use hook, permission request), or null
    /// when the tool is not a Pollux file tool or its arguments are invalid (the tool itself refuses those).
    /// </summary>
    public static FileOperation? FromToolCall(string toolName, JsonElement? args)
    {
        if (!Tools.TryGetValue(toolName, out var kind) || args is not { ValueKind: JsonValueKind.Object } a) return null;
        IReadOnlyList<string>? paths = kind switch
        {
            FileOperationKind.Copy or FileOperationKind.Move => Strings(a, "sources"),
            FileOperationKind.Recycle => Strings(a, "paths"),
            FileOperationKind.NewFolder => Text(a, "parent") is { } parent ? [parent] : null,
            _ => Text(a, "path") is { } path ? [path] : null,
        };
        var name = kind == FileOperationKind.Rename ? Text(a, "newName") : Text(a, "name");
        return Create(kind, paths, Text(a, "destination"), name, Text(a, "group"), out _);
    }

    private static string? Text(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static List<string>? Strings(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? [.. v.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!)]
            : null;

    private static bool IsFullPath(string path)
    {
        var p = path.Trim().Trim('"').Replace('/', '\\');
        var rooted = WinPath.IsUnc(p) ? p.Length > 2 : p.Length >= 3 && char.IsLetter(p[0]) && p[1] == ':' && p[2] == '\\';
        return rooted && !p.Split('\\').Any(s => s is "." or "..") && p.AsSpan().IndexOfAny(WildcardChars) < 0;
    }

    private static bool IsPlainName(string? name, out string? error)
    {
        var n = name?.Trim();
        error = string.IsNullOrEmpty(n) ? "No name given."
            : n is "." or ".." || n.EndsWith('.') ? $"\"{n}\" is not a valid name."
            : n.AsSpan().IndexOfAny(InvalidNameChars) >= 0 ? $"\"{n}\" is not a plain name (no \\ / : * ? \" < > |)."
            : null;
        return error is null;
    }

    private static FileOperation? Fail(string message, out string? error)
    {
        error = message;
        return null;
    }
}

/// <summary>An entry the shell gave a new name during a copy or move because its name was taken in the destination.</summary>
public sealed record RenamedEntry(string OldPath, string NewPath);

/// <summary>What a Pollux file tool reports: whether it worked, the paths it produced (or changed) and the error.</summary>
public sealed record FileOperationResult(bool Ok, IReadOnlyList<string> Paths, string? Error);

/// <summary>A Pollux file tool call the gate decides: the tool, the prompt text and what the Guard Rules judge.</summary>
public sealed record PolluxToolCall(string Tool, string Title, string Detail, IReadOnlyList<ToolRequest> Requests)
{
    public static PolluxToolCall Of(FileOperation operation) =>
        new(operation.Tool, operation.Title, operation.Describe(), operation.GuardRequests);
}

/// <summary>
/// Decides a Pollux file tool call right before Pollux performs it (the Guard Rules of the Chat: blocked, asked or
/// allowed). Implemented by <see cref="Chat.ToolApprovals"/>.
/// </summary>
public interface IPolluxToolGate
{
    /// <summary>Null when the call may go ahead; otherwise why not (told to Copilot).</summary>
    Task<string?> AuthorizeAsync(PolluxToolCall toolCall, CancellationToken cancellationToken);

    /// <summary>Why no Pollux tool may change anything now (the Chat is in Plan mode); null when they may.</summary>
    string? ReadOnlyReason => null;
}

/// <summary>
/// The file operations Pollux performs for Copilot, implemented by the app (Windows shell: recycle bin and Explorer undo,
/// never a permanent delete).
/// </summary>
public interface IPolluxFileActions : IPolluxActions
{
    /// <summary>Performs a validated, already authorized operation.</summary>
    Task<FileOperationResult> RunAsync(FileOperation operation, CancellationToken cancellationToken);
}
