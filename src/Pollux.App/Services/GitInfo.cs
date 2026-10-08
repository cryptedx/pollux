using Pollux.Core.Navigation;

namespace Pollux.App.Services;

/// <summary>Current git branch of a folder, read straight from .git/HEAD (no git.exe, no process start).</summary>
internal static class GitInfo
{
    /// <summary>Repository root containing <paramref name="folder"/>, or null.</summary>
    public static string? RootFor(string folder)
    {
        if (WinPath.IsUnc(folder)) return null;
        for (var dir = folder; dir is not null; dir = WinPath.Parent(dir))
        {
            if (Directory.Exists(Path.Combine(dir, ".git")) || File.Exists(Path.Combine(dir, ".git"))) return dir;
        }

        return null;
    }

    /// <summary>Runs "git status" for a repository (local only, 3 s timeout). Returns null when git is unavailable.</summary>
    public static async Task<Core.Git.GitStatus?> StatusAsync(string root, CancellationToken ct)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("git", "-c core.fsmonitor=false -c core.hooksPath=NUL -c core.quotepath=off status --porcelain=v1 -z --ignored=matching --untracked-files=normal")
            {
                WorkingDirectory = root,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
            };
            psi.Environment["GIT_OPTIONAL_LOCKS"] = "0";
            using var p = System.Diagnostics.Process.Start(psi);
            if (p is null) return null;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            try
            {
                var output = await p.StandardOutput.ReadToEndAsync(timeout.Token).ConfigureAwait(false);
                await p.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                return p.ExitCode == 0 ? Core.Git.GitStatus.Parse(output) : null;
            }
            catch (OperationCanceledException)
            {
                try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                return null;
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or OperationCanceledException or InvalidOperationException)
        {
            return null;
        }
    }

    public static string? BranchFor(string folder)
    {
        if (WinPath.IsUnc(folder)) return null;
        try
        {
            for (var dir = folder; dir is not null; dir = WinPath.Parent(dir))
            {
                var git = Path.Combine(dir, ".git");
                string? head = null;
                if (Directory.Exists(git)) head = Path.Combine(git, "HEAD");
                else if (File.Exists(git))
                {
                    // Worktree or submodule: ".git" is a file pointing at the real git dir.
                    var line = File.ReadAllText(git).Trim();
                    if (line.StartsWith("gitdir:", StringComparison.Ordinal))
                    {
                        var gd = line[7..].Trim();
                        head = Path.Combine(Path.IsPathRooted(gd) ? gd : Path.Combine(dir, gd), "HEAD");
                    }
                }

                if (head is not null && File.Exists(head))
                {
                    var text = File.ReadAllText(head).Trim();
                    return text.StartsWith("ref: refs/heads/", StringComparison.Ordinal) ? text[16..] : text[..Math.Min(7, text.Length)];
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        return null;
    }
}
