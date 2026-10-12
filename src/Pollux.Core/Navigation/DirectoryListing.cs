namespace Pollux.Core.Navigation;

/// <summary>One entry of a folder as the path bar needs it; <see cref="IsHidden"/> covers hidden and system entries.</summary>
public sealed record DirectoryItem(string Name, bool IsDirectory, bool IsHidden);

/// <summary>Lists a folder's direct children. Implementations return an empty list instead of throwing for folders they cannot read.</summary>
public interface IDirectoryLister
{
    /// <summary>Blocking; call it off the UI thread. Throws <see cref="OperationCanceledException"/> only when <paramref name="cancellationToken"/> is cancelled.</summary>
    IReadOnlyList<DirectoryItem> List(string directory, CancellationToken cancellationToken);
}
