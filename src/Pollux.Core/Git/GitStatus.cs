namespace Pollux.Core.Git;

public enum GitMark { None, Modified, Added, Deleted, Renamed, Untracked, Conflict, Ignored }

/// <summary>Working-tree status from "git status --porcelain=v1 -z --ignored", queried per folder.</summary>
public sealed class GitStatus
{
    private readonly List<(string Path, GitMark Mark)> _entries;

    private GitStatus(List<(string, GitMark)> entries) => _entries = entries;

    public static GitStatus Empty { get; } = new([]);

    public static GitStatus Parse(string porcelainZ)
    {
        var entries = new List<(string, GitMark)>();
        var parts = porcelainZ.Split('\0');
        for (var i = 0; i < parts.Length; i++)
        {
            var p = parts[i];
            if (p.Length < 4) continue;
            var x = p[0];
            var y = p[1];
            var path = p[3..].TrimEnd('/');
            var mark = (x, y) switch
            {
                ('?', '?') => GitMark.Untracked,
                ('!', '!') => GitMark.Ignored,
                ('U', _) or (_, 'U') or ('A', 'A') or ('D', 'D') => GitMark.Conflict,
                ('R', _) or ('C', _) => GitMark.Renamed,
                ('A', _) => GitMark.Added,
                ('D', _) or (_, 'D') => GitMark.Deleted,
                _ => GitMark.Modified,
            };
            entries.Add((path, mark));
            if (x is 'R' or 'C') i++; // the original path follows a rename/copy
        }

        return new GitStatus(entries);
    }

    /// <summary>Mark for an entry named <paramref name="name"/> in a folder given relative to the repo root ("" = root, "/" separators).</summary>
    public GitMark MarkFor(string folderRelative, string name, bool isDirectory)
    {
        var rel = folderRelative.Length == 0 ? name : folderRelative.TrimEnd('/') + "/" + name;
        var best = GitMark.None;
        foreach (var (path, mark) in _entries)
        {
            if (string.Equals(path, rel, StringComparison.OrdinalIgnoreCase)) return mark;

            // Inside an ignored folder → ignored.
            if (mark == GitMark.Ignored && rel.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase)) return GitMark.Ignored;

            // A folder shows the most important change below it.
            if (isDirectory && mark != GitMark.Ignored && path.StartsWith(rel + "/", StringComparison.OrdinalIgnoreCase) && Rank(mark) > Rank(best)) best = mark;
        }

        return best;
    }

    private static int Rank(GitMark m) => m switch
    {
        GitMark.Conflict => 6,
        GitMark.Modified => 5,
        GitMark.Deleted => 4,
        GitMark.Renamed => 3,
        GitMark.Added => 2,
        GitMark.Untracked => 1,
        _ => 0,
    };
}
