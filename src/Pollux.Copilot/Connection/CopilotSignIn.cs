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
    /// The CLI to run <see cref="LoginArguments"/> with, or null when there is none. The bundled
    /// <c>copilot-runtime</c> wrapper has an interactive sibling named <c>copilot</c>; it is preferred when present.
    /// The launch plan turns the path into a terminal command line.
    /// </summary>
    public static string? CliPath(CopilotCli cli, Func<string, bool> fileExists) => cli switch
    {
        CopilotCli.Bundled b => InteractiveSibling(b.Path, fileExists),
        CopilotCli.Configured c => c.Path,
        _ => null,
    };

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
