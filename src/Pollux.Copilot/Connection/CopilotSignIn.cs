namespace Pollux.Copilot.Connection;

/// <summary>
/// "Sign in" in the Copilot Panel runs the CLI's own login in a terminal window; the CLI stores the credential and
/// Pollux never handles a token (ADR 0005).
/// </summary>
public static class CopilotSignIn
{
    /// <summary>The arguments of the CLI that start its login flow.</summary>
    public const string LoginArguments = "login";

    /// <summary>
    /// The command line for a <c>cmd.exe /k</c> terminal, or null when there is no CLI to run. The bundled
    /// <c>copilot-runtime</c> wrapper has an interactive sibling named <c>copilot</c>; it is preferred when present.
    /// </summary>
    public static string? TerminalCommandLine(CopilotCli cli, Func<string, bool> fileExists)
    {
        var path = cli switch
        {
            CopilotCli.Bundled b => InteractiveSibling(b.Path, fileExists),
            CopilotCli.Configured c => c.Path,
            _ => null,
        };
        return path is null ? null : $"/k \"\"{path}\" {LoginArguments}\"";
    }

    private static string InteractiveSibling(string wrapperPath, Func<string, bool> fileExists)
    {
        var dir = Path.GetDirectoryName(wrapperPath);
        if (dir is not null)
        {
            var sibling = Path.Combine(dir, Path.GetFileName(wrapperPath).EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? "copilot.exe" : "copilot");
            if (fileExists(sibling)) return sibling;
        }

        return wrapperPath;
    }
}
