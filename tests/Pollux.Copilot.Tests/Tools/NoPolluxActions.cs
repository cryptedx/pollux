using Pollux.Copilot.Tools;

namespace Pollux.Copilot.Tests.Tools;

/// <summary>Pollux actions for tests that do not use the Pollux tools: no Panes at all.</summary>
internal sealed class NoPolluxActions : IPolluxActions
{
    public Task<IReadOnlyList<PaneSnapshot>> ReadPanesAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<PaneSnapshot>>([]);

    public Task<NavigateResult> NavigateAsync(string pane, string location, bool newTab, CancellationToken cancellationToken) =>
        Task.FromResult(new NavigateResult(false, null, 0, "No Panes."));

    public Task<int> SelectAsync(string pane, IReadOnlyList<string> names, CancellationToken cancellationToken) =>
        Task.FromResult(0);
}

/// <summary>Pollux actions that can perform file operations (so the file tools are offered) but have no Panes and change nothing.</summary>
internal sealed class NoPolluxFileActions : IPolluxFileActions
{
    private readonly NoPolluxActions _none = new();

    public Task<IReadOnlyList<PaneSnapshot>> ReadPanesAsync(CancellationToken cancellationToken) => _none.ReadPanesAsync(cancellationToken);

    public Task<NavigateResult> NavigateAsync(string pane, string location, bool newTab, CancellationToken cancellationToken) =>
        _none.NavigateAsync(pane, location, newTab, cancellationToken);

    public Task<int> SelectAsync(string pane, IReadOnlyList<string> names, CancellationToken cancellationToken) =>
        _none.SelectAsync(pane, names, cancellationToken);

    public Task<FileOperationResult> RunAsync(FileOperation operation, CancellationToken cancellationToken) =>
        Task.FromResult(new FileOperationResult(false, [], "No file operations in tests."));
}
