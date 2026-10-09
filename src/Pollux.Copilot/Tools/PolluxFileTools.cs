using Microsoft.Extensions.AI;

namespace Pollux.Copilot.Tools;

/// <summary>
/// The Pollux tools that change files (copy, move, rename, recycle bin, new folder) and add Bookmarks. Unlike the read
/// and navigation tools they go through the CLI's permission flow (the Chat's Permission Mode decides whether to ask), and
/// right before Pollux acts every call passes the Chat's Guard Rules (<see cref="IPolluxToolGate"/>), whatever the CLI
/// did before. There is no permanent-delete tool.
/// </summary>
public static class PolluxFileTools
{
    private const string PathHint = "Paths are full Windows paths (C:\\folder\\name).";

    internal static IReadOnlyList<AIFunction> Create(IPolluxFileActions actions, IPolluxToolGate gate) =>
    [
        CopilotTools.Define(
            (string[] sources, string destination, CancellationToken ct = default) =>
                RunAsync(actions, gate, FileOperationKind.Copy, sources, destination, null, null, ct),
            "pollux_copy",
            $"Copies files and folders into a destination folder with Pollux (Windows shell copy; a name that exists already gets a new name, nothing is overwritten). {PathHint}",
            PolluxToolJson.Default),
        CopilotTools.Define(
            (string[] sources, string destination, CancellationToken ct = default) =>
                RunAsync(actions, gate, FileOperationKind.Move, sources, destination, null, null, ct),
            "pollux_move",
            $"Moves files and folders into a destination folder with Pollux (undoable in Explorer). {PathHint}",
            PolluxToolJson.Default),
        CopilotTools.Define(
            (string path, string newName, CancellationToken ct = default) =>
                RunAsync(actions, gate, FileOperationKind.Rename, [path], null, newName, null, ct),
            "pollux_rename",
            $"Renames a file or folder in place with Pollux. newName is the new name only, not a path. {PathHint}",
            PolluxToolJson.Default),
        CopilotTools.Define(
            (string[] paths, CancellationToken ct = default) =>
                RunAsync(actions, gate, FileOperationKind.Recycle, paths, null, null, null, ct),
            "pollux_recycle",
            $"Deletes files and folders by moving them to the Windows recycle bin with Pollux (they can be restored). There is no permanent delete. {PathHint}",
            PolluxToolJson.Default),
        CopilotTools.Define(
            (string parent, string name, CancellationToken ct = default) =>
                RunAsync(actions, gate, FileOperationKind.NewFolder, [parent], null, name, null, ct),
            "pollux_new_folder",
            $"Creates a new folder named name inside the folder parent with Pollux. {PathHint}",
            PolluxToolJson.Default),
        CopilotTools.Define(
            (string path, string? name = null, string? group = null, CancellationToken ct = default) =>
                RunAsync(actions, gate, FileOperationKind.AddBookmark, [path], null, name, group, ct),
            "pollux_add_bookmark",
            $"Adds a file or folder to the user's Pollux Bookmarks (sidebar), optionally with a display name and a group. {PathHint}",
            PolluxToolJson.Default),
    ];

    private static async Task<FileOperationResult> RunAsync(
        IPolluxFileActions actions, IPolluxToolGate gate, FileOperationKind kind,
        IReadOnlyList<string>? paths, string? destination, string? name, string? group, CancellationToken ct)
    {
        if (FileOperation.Create(kind, paths, destination, name, group, out var error) is not { } operation)
        {
            return new(false, [], error);
        }

        if (await gate.AuthorizeAsync(PolluxToolCall.Of(operation), ct).ConfigureAwait(false) is { } refusal)
        {
            return new(false, [], refusal);
        }

        try
        {
            return await actions.RunAsync(operation, ct).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Any failure goes back to Copilot as the tool's error.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            return new(false, [], ex.Message);
        }
    }
}
