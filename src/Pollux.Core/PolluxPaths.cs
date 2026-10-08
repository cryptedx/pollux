namespace Pollux.Core;

/// <summary>
/// Where Pollux keeps its files: config roams with the user (%AppData%), data and logs stay on the machine (%LocalAppData%).
/// </summary>
public sealed record PolluxPaths(string ConfigDirectory, string DataDirectory, string LogsDirectory)
{
    private const string AppFolder = "Pollux";

    public static PolluxPaths FromEnvironment(Func<string, string?> getEnvironmentVariable)
    {
        var roaming = Require(getEnvironmentVariable, "APPDATA");
        var local = Require(getEnvironmentVariable, "LOCALAPPDATA");
        var data = Path.Combine(local, AppFolder);

        return new PolluxPaths(Path.Combine(roaming, AppFolder), data, Path.Combine(data, "logs"));
    }

    public static PolluxPaths Current() => FromEnvironment(Environment.GetEnvironmentVariable);

    private static string Require(Func<string, string?> getEnvironmentVariable, string name) =>
        getEnvironmentVariable(name) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"Environment variable {name} is not set.");
}
