namespace Pollux.Core.Git;

/// <summary>
/// Arguments and environment of the hardened "git status" Pollux runs where the <see cref="GitGate"/> allows it
/// (SECURITY.md S-01, ADR 0006): fsmonitor and hooks off, no optional locks, no system config, and attributes taken
/// from the empty tree so a repository's own .gitattributes cannot select a filter or diff driver.
/// </summary>
public static class GitStatusCommand
{
    /// <summary>The well-known id of git's empty tree; git knows it without it being stored in the repository.</summary>
    public const string EmptyTree = "4b825dc642cb6eb9a060e54bf8d69288fbbd4904";

    /// <summary>The empty tree in a repository created with <c>--object-format=sha256</c>.</summary>
    public const string EmptyTreeSha256 = "6ef19b41225c5369f1c104d45d8d85efa9b057b53b14b4b9b939dd74decc5321";

    public static IReadOnlyList<string> Arguments { get; } =
    [
        "-c", "core.fsmonitor=false",
        "-c", "core.hooksPath=NUL",
        "-c", "core.quotepath=off",
        "status", "--porcelain=v1", "-z", "--ignored=matching", "--untracked-files=normal",
    ];

    /// <summary>Variables set on the git process. GIT_ATTR_SOURCE needs git 2.40 or later; older versions ignore it.</summary>
    public static IReadOnlyDictionary<string, string> Environment { get; } = new Dictionary<string, string>
    {
        ["GIT_OPTIONAL_LOCKS"] = "0",
        ["GIT_CONFIG_NOSYSTEM"] = "1",
        ["GIT_ATTR_SOURCE"] = EmptyTree,
    };

    /// <summary>
    /// <see cref="Environment"/> for a repository whose <c>extensions.objectFormat</c> is <paramref name="objectFormat"/>
    /// (<see cref="GitGateResult.ObjectFormat"/>): git rejects a SHA-1 id as attribute source in a SHA-256 repository.
    /// </summary>
    public static IReadOnlyDictionary<string, string> EnvironmentFor(string? objectFormat) =>
        string.Equals(objectFormat, "sha256", StringComparison.OrdinalIgnoreCase)
            ? new Dictionary<string, string>(Environment) { ["GIT_ATTR_SOURCE"] = EmptyTreeSha256 }
            : Environment;
}
