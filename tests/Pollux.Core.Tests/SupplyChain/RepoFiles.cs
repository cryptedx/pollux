using System.Diagnostics;

namespace Pollux.Core.Tests.SupplyChain;

/// <summary>Access to the repository checkout the tests run from, for checks on build and CI files.</summary>
internal static class RepoFiles
{
    private static readonly Lazy<string> RootPath = new(() =>
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Pollux.slnx")))
                return dir.FullName;
        throw new InvalidOperationException("Repository root (Pollux.slnx) not found above " + AppContext.BaseDirectory);
    });

    public static string Root => RootPath.Value;

    /// <summary>
    /// Runs the dotnet CLI in <paramref name="workingDirectory"/> with a clean MSBuild environment and returns its exit
    /// code and combined output. <paramref name="environment"/> entries set (or, with a null value, remove) variables.
    /// </summary>
    public static (int ExitCode, string Output) Dotnet(string workingDirectory, IReadOnlyDictionary<string, string?>? environment, params string[] args)
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host ? host : "dotnet")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args)
            start.ArgumentList.Add(arg);
        // The test host may run inside an MSBuild/dotnet test process whose variables would steer the child build.
        foreach (var key in start.Environment.Keys.ToList())
            if (key.StartsWith("MSBuild", StringComparison.OrdinalIgnoreCase) || key.StartsWith("MSBUILD", StringComparison.Ordinal)
                || key.Equals("CI", StringComparison.OrdinalIgnoreCase) || key.Equals("GITHUB_ACTIONS", StringComparison.OrdinalIgnoreCase))
                start.Environment.Remove(key);
        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        start.Environment["DOTNET_NOLOGO"] = "1";
        start.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en";
        if (environment != null)
            foreach (var (key, value) in environment)
                if (value == null) start.Environment.Remove(key);
                else start.Environment[key] = value;

        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(TimeSpan.FromMinutes(3)))
        {
            process.Kill(entireProcessTree: true);
            Assert.Fail("dotnet " + string.Join(' ', args) + " timed out");
        }
        return (process.ExitCode, stdout.Result + stderr.Result);
    }
}
