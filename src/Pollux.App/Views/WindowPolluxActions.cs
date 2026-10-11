using Microsoft.UI.Dispatching;
using Pollux.App.Services;
using Pollux.Copilot.Tools;
using Pollux.Core.Navigation;

namespace Pollux.App.Views;

/// <summary>
/// The Pollux tools of Copilot (ticket 16) over the Panes of one window. Copilot calls them on SDK threads; every Pane
/// access is marshalled to the UI thread, and slow disk checks stay off it.
/// </summary>
/// <param name="panes">The window's two Panes (left, right).</param>
/// <param name="activePane">Index of the active Pane.</param>
/// <param name="isDual">Whether the right Pane is shown.</param>
/// <param name="hwnd">Owner of the shell's progress and error dialogs.</param>
/// <param name="addBookmark">Adds a Bookmark (path, name, group); called on the UI thread.</param>
/// <remarks>
/// File operations (ticket 17) use the Windows shell like Pollux's own commands (SHFileOperation, which runs on
/// IFileOperation): undoable in Explorer, deleting only into the recycle bin (an entry that cannot be recycled gets the
/// shell's warning instead of vanishing), and a name clash gets a new name instead of replacing anything. A copy or
/// move reports where each entry really went: the shell's name mappings give the new names of renamed entries.
/// </remarks>
internal sealed class WindowPolluxActions(
    DispatcherQueue ui, IReadOnlyList<PaneView> panes, Func<int> activePane, Func<bool> isDual, nint hwnd, Action<string, string?, string?> addBookmark)
    : IPolluxFileActions
{
    public Task<IReadOnlyList<PaneSnapshot>> ReadPanesAsync(CancellationToken cancellationToken) =>
        OnUi<IReadOnlyList<PaneSnapshot>>(() =>
        {
            var active = activePane();
            var shown = isDual() ? panes.Count : 1;
            return Task.FromResult<IReadOnlyList<PaneSnapshot>>(
                [.. Enumerable.Range(0, shown).Select(i => Snapshot(i, i == active))]);
        }, cancellationToken);

    public async Task<NavigateResult> NavigateAsync(string pane, string location, bool newTab, CancellationToken cancellationToken)
    {
        var target = location.Equals("home", StringComparison.OrdinalIgnoreCase) ? WinPath.Home : WinPath.Normalize(location);
        if (target != WinPath.Home)
        {
            var kind = await Task.Run(() => Directory.Exists(target) ? 1 : File.Exists(target) ? 2 : 0, cancellationToken).ConfigureAwait(false);
            if (kind == 2) return new(false, null, 0, $"{target} is a file, not a folder.");
            if (kind == 0) return new(false, null, 0, $"The folder {target} does not exist.");
        }

        return await OnUi(async () =>
        {
            var p = Resolve(pane);
            if (p is null) return new NavigateResult(false, null, 0, $"There is no {pane} Pane in this window.");
            if (newTab) p.NewTab(target);
            else p.Navigate(target);
            await p.ListingLoaded;
            return new NavigateResult(true, p.Folder ?? "home", p.VisibleItems.Count, null);
        }, cancellationToken).ConfigureAwait(false);
    }

    public Task<int> SelectAsync(string pane, IReadOnlyList<string> names, CancellationToken cancellationToken) =>
        OnUi(() =>
        {
            var p = Resolve(pane) ?? throw new InvalidOperationException($"There is no {pane} Pane in this window.");
            return Task.FromResult(p.SelectNames(names));
        }, cancellationToken);

    public async Task<FileOperationResult> RunAsync(FileOperation operation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (await Task.Run(() => operation.Precheck(File.Exists, Directory.Exists), cancellationToken).ConfigureAwait(false) is { } problem)
        {
            return new(false, [], problem);
        }

        if (operation.Kind == FileOperationKind.NewFolder)
        {
            var folder = operation.Targets[0];
            await Task.Run(() => Directory.CreateDirectory(folder), cancellationToken).ConfigureAwait(false);
            return new(true, [folder], null);
        }

        return await OnUi(() => Task.FromResult(operation.Kind switch
        {
            FileOperationKind.Copy or FileOperationKind.Move => CopyOrMove(operation),
            FileOperationKind.Rename => Done(Shell.Rename(hwnd, operation.Paths[0], operation.Name!), operation.Targets),
            FileOperationKind.Recycle => Done(Shell.Delete(hwnd, operation.Paths, permanent: false, confirm: false), operation.Paths),
            FileOperationKind.AddBookmark => Bookmark(operation),
            _ => new FileOperationResult(false, [], $"{operation.Kind} is not supported."),
        }), cancellationToken).ConfigureAwait(false);
    }

    private FileOperationResult CopyOrMove(FileOperation operation)
    {
        var ok = Shell.CopyOrMoveRenamingOnCollision(hwnd, operation.Kind == FileOperationKind.Move, operation.Paths, operation.Destination!, out var renamed);
        return Done(ok, operation.TargetsAfter([.. renamed.Select(r => new RenamedEntry(r.OldPath, r.NewPath))]));
    }

    private FileOperationResult Bookmark(FileOperation operation)
    {
        addBookmark(operation.Paths[0], operation.Name, operation.Group);
        return new(true, [operation.Paths[0]], null);
    }

    private static FileOperationResult Done(bool ok, IReadOnlyList<string> paths) =>
        ok ? new(true, paths, null) : new(false, [], "Windows did not complete the operation (it failed or was cancelled).");

    private PaneSnapshot Snapshot(int index, bool active)
    {
        var p = panes[index];
        return new(
            index == 0 ? PaneTargets.Left : PaneTargets.Right,
            active,
            p.Folder,
            [.. p.Tabs.Select(t => t.IsHome ? "home" : t.Location)],
            p.ActiveTabIndex,
            p.SelectedPaths,
            [.. p.VisibleItems.Select(i => new PaneEntry(i.Entry.Name, i.Entry.IsDirectory))]);
    }

    private PaneView? Resolve(string target) =>
        PaneTargets.ResolveIndex(target, activePane(), isDual()) is { } index ? panes[index] : null;

    private Task<T> OnUi<T>(Func<Task<T>> work, CancellationToken cancellationToken)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        cancellationToken.Register(() => done.TrySetCanceled(cancellationToken));
        if (!ui.TryEnqueue(async () =>
            {
                try
                {
                    done.TrySetResult(await work());
                }
#pragma warning disable CA1031 // Any failure on the UI thread goes back to the caller (Copilot) as the tool's error.
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    done.TrySetException(ex);
                }
            }))
        {
            done.TrySetException(new InvalidOperationException("The window is closing."));
        }

        return done.Task;
    }
}
