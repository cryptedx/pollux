using System.Text;
using Pollux.Core.Navigation;

namespace Pollux.Core.Git;

/// <summary>What Pollux may do with git for a folder (ADR 0006).</summary>
public enum GitGateMode
{
    /// <summary>No repository, or one Pollux must not touch (network location, network <c>gitdir</c>).</summary>
    None,

    /// <summary>Show the branch read from HEAD by Pollux itself; no process is started.</summary>
    BranchOnly,

    /// <summary>Run git status at <see cref="GitGateResult.RepoRoot"/> (inside a Trusted Folder, git status switched on).</summary>
    RunStatus,
}

/// <summary>The git gate's verdict for one folder.</summary>
public sealed record GitGateResult(GitGateMode Mode, string? RepoRoot, string? Branch)
{
    public static GitGateResult None { get; } = new(GitGateMode.None, null, null);

    /// <summary>Whether <see cref="RepoRoot"/> lies in a Trusted Folder.</summary>
    public bool Trusted { get; init; }

    /// <summary>
    /// The repository config key that made the gate refuse git status inside a Trusted Folder (see
    /// <see cref="GitConfigScan"/>), or null. Only set when git status would otherwise have run.
    /// </summary>
    public string? RefusedConfigKey { get; init; }

    /// <summary>The repository's <c>extensions.objectFormat</c> ("sha256"), lower case; null when unset. Only read when git status may run.</summary>
    public string? ObjectFormat { get; init; }

    /// <summary>
    /// Whether the path bar offers "Trust this folder": a local repository outside every Trusted Folder. Trusting
    /// adds <see cref="RepoRoot"/>.
    /// </summary>
    public bool CanTrust => Mode != GitGateMode.None && RepoRoot is not null && !Trusted;
}

/// <summary>
/// Decides per folder whether git may run (SECURITY.md S-01, S-05; ADR 0006). It finds the repository root,
/// resolves <c>.git</c> files, refuses <c>gitdir</c> targets on UNC paths or network drives without touching them,
/// and matches Trusted Folders (including their subfolders) after canonicalisation. Network locations never count
/// as Trusted Folders. The gate itself only reads small local files; it never starts a process.
/// </summary>
public sealed class GitGate
{
    private const int MaxSmallFile = 4096;
    private const int MaxConfigFile = 1024 * 1024;

    private readonly List<string> _trusted;
    private readonly bool _gitStatusEnabled;
    private readonly Func<string, bool> _isNetworkDrive;

    /// <param name="trustedFolders">The Trusted Folders from the settings; network or relative entries are ignored.</param>
    /// <param name="gitStatusEnabled">The git-status switch (files.gitStatus), the master switch for running git.</param>
    /// <param name="isNetworkDrive">
    /// Whether a fully qualified local-looking path (such as <c>Z:\x</c>) lies on a network drive. UNC paths are
    /// always network locations regardless of this function. Defaults to <see cref="IsOnNetworkDrive"/>.
    /// </param>
    public GitGate(IEnumerable<string> trustedFolders, bool gitStatusEnabled, Func<string, bool>? isNetworkDrive = null)
    {
        _isNetworkDrive = isNetworkDrive ?? IsOnNetworkDrive;
        _gitStatusEnabled = gitStatusEnabled;
        _trusted = Sanitize(trustedFolders, _isNetworkDrive);
    }

    /// <summary>The verdict for <paramref name="folder"/>: run git status at the repository root, show only the branch, or nothing.</summary>
    public GitGateResult Evaluate(string folder)
    {
        try
        {
            if (Canonical(folder, _isNetworkDrive) is not { } start) return GitGateResult.None;
            for (var dir = start; dir is not null; dir = Path.GetDirectoryName(dir))
            {
                var dotGit = Path.Combine(dir, ".git");
                if (!TryGetAttributes(dotGit, out var attributes)) continue;

                // A .git that is a link to a network location is not followed (S-05).
                if (LinksToNetwork(dotGit, attributes)) return GitGateResult.None;
                var gitDir = attributes.HasFlag(FileAttributes.Directory) ? dotGit : ResolveGitFile(dir, dotGit);
                if (gitDir is null) return GitGateResult.None;

                var branch = ReadBranch(gitDir);
                var trusted = IsTrusted(dir);
                var branchOnly = new GitGateResult(GitGateMode.BranchOnly, dir, branch) { Trusted = trusted };
                if (!_gitStatusEnabled || !trusted || CommonDir(gitDir) is not { } commonDir) return branchOnly;

                // Second line of defence: a hostile repo cloned into a trusted root is still refused.
                if (RefusedConfigKey(gitDir, commonDir, out var objectFormat) is { } key) return branchOnly with { RefusedConfigKey = key };
                return branchOnly with { Mode = GitGateMode.RunStatus, ObjectFormat = objectFormat };
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return GitGateResult.None;
        }

        return GitGateResult.None;
    }

    /// <summary>Whether <paramref name="folder"/> is a Trusted Folder or lies below one. Network locations never are.</summary>
    public bool IsTrusted(string folder)
    {
        if (Canonical(folder, _isNetworkDrive) is not { } path) return false;
        return _trusted.Exists(root => IsSameOrUnder(path, root));
    }

    /// <summary>
    /// The entries that can be Trusted Folders, canonicalised and without duplicates: UNC paths, network drives,
    /// relative paths and empty lines are dropped (S-01: remote content never drives local git).
    /// </summary>
    public static List<string> Sanitize(IEnumerable<string> trustedFolders, Func<string, bool>? isNetworkDrive = null)
    {
        var result = new List<string>();
        // An entry holding several lines (a TextBox separates them with a bare CR) is several entries.
        foreach (var entry in trustedFolders.SelectMany(e => (e ?? "").Split(['\r', '\n'])))
        {
            if (Canonical(entry, isNetworkDrive ?? IsOnNetworkDrive) is { } path && !result.Exists(p => SamePath(p, path)))
                result.Add(path);
        }

        return result;
    }

    /// <summary>
    /// The Trusted Folders after the path bar's "Trust this folder" on <paramref name="folder"/> (the repository root):
    /// sanitized, with the folder added unless an existing entry already covers it. Null when the folder is a
    /// network location, which can never be trusted.
    /// </summary>
    public static List<string>? AddTrustedFolder(IEnumerable<string> trustedFolders, string folder, Func<string, bool>? isNetworkDrive = null)
    {
        var isNetwork = isNetworkDrive ?? IsOnNetworkDrive;
        if (Canonical(folder, isNetwork) is not { } path) return null;
        var result = Sanitize(trustedFolders, isNetwork);
        if (!result.Exists(root => IsSameOrUnder(path, root))) result.Add(path);
        return result;
    }

    /// <summary>True for UNC-style paths in any spelling (<c>\\server</c>, <c>//server</c>, <c>\\?\UNC\</c>, <c>\??\UNC\</c>, device paths).</summary>
    public static bool IsUncLike(string path)
    {
        var p = path.TrimStart();
        if (p.Length >= 2 && IsSeparator(p[0]) && IsSeparator(p[1])) return true;
        return p.StartsWith(@"\??\", StringComparison.Ordinal) || p.StartsWith(@"UNC\", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Default network-drive check (<see cref="NetworkDrive.IsNetwork(string)"/>): unknown or unreadable counts as network (fail closed).</summary>
    public static bool IsOnNetworkDrive(string path) => NetworkDrive.IsNetwork(path);

    /// <summary>The absolute, canonical form of a local path, or null for empty, relative or network paths.</summary>
    internal static string? Canonical(string? path, Func<string, bool> isNetworkDrive)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var p = path.Trim().Trim('"');
        if (IsUncLike(p) || !Path.IsPathFullyQualified(p)) return null;
        string full;
        try { full = Path.GetFullPath(p); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }

        if (IsUncLike(full) || isNetworkDrive(full)) return null;
        var root = Path.GetPathRoot(full) ?? "";
        return full.Length > root.Length ? full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : full;
    }

    private static bool IsSeparator(char c) => c is '\\' or '/';

    private static bool SamePath(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    internal static bool IsSameOrUnder(string path, string root)
    {
        if (SamePath(path, root)) return true;
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return false;
        return IsSeparator(root[^1]) || IsSeparator(path[root.Length]);
    }

    private static bool TryGetAttributes(string path, out FileAttributes attributes)
    {
        try
        {
            attributes = File.GetAttributes(path);
            return true;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            attributes = default;
            return false;
        }
    }

    /// <summary>Whether a symbolic link at <paramref name="path"/> points to a network location (the target is read, never followed).</summary>
    private bool LinksToNetwork(string path, FileAttributes attributes)
    {
        if (!attributes.HasFlag(FileAttributes.ReparsePoint)) return false;
        FileSystemInfo info = attributes.HasFlag(FileAttributes.Directory) ? new DirectoryInfo(path) : new FileInfo(path);
        if (info.LinkTarget is not { } target) return false;
        return !IsLocalTarget(Path.GetDirectoryName(path)!, target, out _);
    }

    /// <summary>Resolves <paramref name="target"/> against <paramref name="baseDir"/>; false (without touching it) when it is not local.</summary>
    private bool IsLocalTarget(string baseDir, string target, out string resolved)
    {
        resolved = "";
        var t = target.Trim();
        if (t.Length == 0 || IsUncLike(t)) return false;
        string full;
        try { full = Path.GetFullPath(Path.IsPathRooted(t) ? t : Path.Combine(baseDir, t)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }

        if (IsUncLike(full) || _isNetworkDrive(full)) return false;
        resolved = full;
        return true;
    }

    /// <summary>The git dir named by a <c>.git</c> file ("gitdir: …", used by worktrees and submodules), or null when it is not local (S-05).</summary>
    private string? ResolveGitFile(string dir, string dotGitFile)
    {
        var text = ReadSmall(dotGitFile);
        if (text is null) return null;
        var line = text.Split('\n')[0].Trim();
        if (!line.StartsWith("gitdir:", StringComparison.Ordinal)) return null;
        return IsLocalTarget(dir, line[7..], out var gitDir) ? gitDir : null;
    }

    /// <summary>
    /// The shared git dir: worktrees name it in "commondir", otherwise it is the git dir itself. Null when it is not
    /// local, since git would read it.
    /// </summary>
    private string? CommonDir(string gitDir)
    {
        var file = Path.Combine(gitDir, "commondir");
        if (!TryGetAttributes(file, out var attributes)) return gitDir;
        if (LinksToNetwork(file, attributes)) return null;
        var text = ReadSmall(file);
        return text is not null && IsLocalTarget(gitDir, text.Split('\n')[0], out var commonDir) ? commonDir : null;
    }

    /// <summary>
    /// The first refused key in the repository's own config files (the shared "config" and the per-worktree
    /// "config.worktree"), <see cref="GitConfigScan.Unreadable"/> when one cannot be read completely, or null.
    /// <paramref name="objectFormat"/> is the last <c>extensions.objectFormat</c> they set.
    /// </summary>
    private string? RefusedConfigKey(string gitDir, string commonDir, out string? objectFormat)
    {
        objectFormat = null;
        string[] files =
        [
            Path.Combine(commonDir, "config"), Path.Combine(commonDir, "config.worktree"),
            Path.Combine(gitDir, "config"), Path.Combine(gitDir, "config.worktree"),
        ];
        foreach (var file in files.Distinct(StringComparer.Ordinal))
        {
            if (!TryGetAttributes(file, out var attributes)) continue;
            if (attributes.HasFlag(FileAttributes.Directory) || LinksToNetwork(file, attributes)) return GitConfigScan.Unreadable;
            string text;
            try { text = ReadConfig(file); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return GitConfigScan.Unreadable; }

            if (GitConfigScan.FindRefusedKey(text) is { } key) return key;
            objectFormat = GitConfigScan.ObjectFormat(text) ?? objectFormat;
        }

        return null;
    }

    /// <summary>A whole config file up to <see cref="MaxConfigFile"/>; a larger one is refused rather than scanned in part.</summary>
    private static string ReadConfig(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length > MaxConfigFile) throw new IOException("config too large to scan");
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false);
        return reader.ReadToEnd();
    }

    /// <summary>The branch from HEAD ("ref: refs/heads/x" → "x"; detached → the short hash), or null.</summary>
    private string? ReadBranch(string gitDir)
    {
        var head = Path.Combine(gitDir, "HEAD");
        if (!TryGetAttributes(head, out var attributes) || attributes.HasFlag(FileAttributes.Directory) || LinksToNetwork(head, attributes)) return null;
        var text = ReadSmall(head)?.Trim();
        if (string.IsNullOrEmpty(text)) return null;
        const string prefix = "ref: refs/heads/";
        return text.StartsWith(prefix, StringComparison.Ordinal) ? text[prefix.Length..].Split('\n')[0].Trim() : text[..Math.Min(7, text.Length)];
    }

    private static string? ReadSmall(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var buffer = new byte[MaxSmallFile];
        var read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
        return Encoding.UTF8.GetString(buffer, 0, read);
    }
}
