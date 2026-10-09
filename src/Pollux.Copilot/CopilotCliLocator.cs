using System.Runtime.InteropServices;

namespace Pollux.Copilot;

/// <summary>Which Copilot CLI Pollux runs: the one bundled next to Pollux.exe, the one from <c>copilot.cliPath</c>, or none.</summary>
public abstract record CopilotCli
{
    private CopilotCli() { }

    /// <summary>The pinned CLI shipped with Pollux (ADR 0005); its runtime wrapper path.</summary>
    public sealed record Bundled(string Path) : CopilotCli;

    /// <summary>The CLI the user configured in <c>copilot.cliPath</c>.</summary>
    public sealed record Configured(string Path) : CopilotCli;

    /// <summary>No usable CLI; <paramref name="Reason"/> says what was looked for.</summary>
    public sealed record Missing(string Reason) : CopilotCli;
}

public static class CopilotCliLocator
{
    /// <summary>Finds the CLI for this process: <c>copilot.cliPath</c> wins, otherwise the bundle for the running architecture.</summary>
    public static CopilotCli Locate(string? configuredPath) =>
        Locate(configuredPath, AppContext.BaseDirectory, CurrentRid());

    /// <summary>
    /// Mirrors the SDK's own bundle lookup (<c>runtimes/&lt;rid&gt;/native/copilot-runtime[.exe]</c> plus a non-empty
    /// <c>runtime.node</c> next to it) so a broken install is reported before the SDK tries to start it.
    /// </summary>
    public static CopilotCli Locate(string? configuredPath, string baseDirectory, string rid)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            var path = Environment.ExpandEnvironmentVariables(configuredPath.Trim());
            return File.Exists(path)
                ? new CopilotCli.Configured(path)
                : new CopilotCli.Missing($"copilot.cliPath points to '{path}', which does not exist.");
        }

        var wrapperName = rid.StartsWith("win", StringComparison.Ordinal) ? "copilot-runtime.exe" : "copilot-runtime";
        var wrapper = Path.Combine(baseDirectory, "runtimes", rid, "native", wrapperName);
        var runtimeNode = Path.Combine(Path.GetDirectoryName(wrapper)!, "runtime.node");
        if (!IsNonEmptyFile(wrapper))
        {
            return new CopilotCli.Missing($"The bundled Copilot CLI is missing: '{wrapper}' not found.");
        }

        return IsNonEmptyFile(runtimeNode)
            ? new CopilotCli.Bundled(wrapper)
            : new CopilotCli.Missing($"The bundled Copilot CLI is incomplete: '{runtimeNode}' is missing or empty.");
    }

    /// <summary>The portable RID the bundle was built for (win-x64, win-arm64, ...).</summary>
    public static string CurrentRid()
    {
        var os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux";
        var arch = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x64";
        return $"{os}-{arch}";
    }

    private static bool IsNonEmptyFile(string path) => File.Exists(path) && new FileInfo(path).Length > 0;
}
