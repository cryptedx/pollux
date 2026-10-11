using Pollux.Core.Git;
using Pollux.Core.Launch;
using Pollux.Core.Settings;

namespace Pollux.App.Services;

/// <summary>
/// git in the file lists, always through the <see cref="GitGate"/> (ADR 0006): git status only in Trusted Folders,
/// elsewhere the branch read from HEAD without starting a process.
/// </summary>
internal static class GitInfo
{
    /// <summary>The git gate for the current settings (Trusted Folders and the git-status switch).</summary>
    public static GitGate Gate(PolluxSettings settings) => new(settings.Files.TrustedFolders, settings.Files.GitStatus);

    /// <summary>The branch to show for a folder, or null. Needs no trust: the gate only reads HEAD and never follows network gitdirs.</summary>
    public static string? BranchFor(string folder) => new GitGate([], gitStatusEnabled: false).Evaluate(folder).Branch;

    /// <summary>Runs "git status" for a repository the gate allowed (local only, 3 s timeout). Returns null when git is unavailable.</summary>
    public static async Task<GitStatus?> StatusAsync(GitGateResult gate, CancellationToken ct)
    {
        if (gate is not { Mode: GitGateMode.RunStatus, RepoRoot: { } root }) return null;
        try
        {
            using var p = Shell.StartHelper(new LaunchIntent.Git(root, GitStatusCommand.Arguments), redirectStdErr: false, System.Text.Encoding.UTF8, out _,
                GitStatusCommand.EnvironmentFor(gate.ObjectFormat));
            if (p is null) return null;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            try
            {
                var output = await p.StandardOutput.ReadToEndAsync(timeout.Token).ConfigureAwait(false);
                await p.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                return p.ExitCode == 0 ? GitStatus.Parse(output) : null;
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
}
