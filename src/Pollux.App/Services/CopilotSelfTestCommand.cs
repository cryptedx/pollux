using Pollux.Copilot;
using Pollux.Core;
using System.Security.Principal;
using Pollux.Core.Copilot;
using Pollux.Core.Settings;

namespace Pollux.App.Services;

/// <summary>
/// <c>Pollux.exe --copilot-selftest &lt;report file&gt;</c>: starts the Copilot CLI (bundled, or <c>copilot.cliPath</c>),
/// completes the handshake and exits with 0/1, without UI or the single-instance redirect. CI runs it on the
/// Native AOT publish to prove the SDK works trimmed (ADR 0002). Independent of the "Enable Copilot" setting, but refused
/// (exit code 2) in the Admin Instance like every other Copilot entry point.
/// </summary>
internal static class CopilotSelfTestCommand
{
    public const string Flag = "--copilot-selftest";

    public static int Run(string[] args)
    {
        if (!CopilotAvailability.AllowsSelfTest(IsElevated(), Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true"))
        {
            Console.Error.WriteLine("Pollux: the Copilot self-test does not run in the Admin Instance.");
            return 2;
        }

        var settingsFile = Path.Combine(PolluxPaths.Current().ConfigDirectory, "settings.json");
        var cliPath = File.Exists(settingsFile) ? SettingsStore.Load(settingsFile).Settings.Copilot.CliPath : null;
        var cli = CopilotCliLocator.Locate(cliPath);
        var next = Array.IndexOf(args, Flag) + 1;
        var reportPath = next < args.Length ? args[next] : "copilot-selftest.txt";

        using var report = new StreamWriter(reportPath);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        return CopilotSelfTest.RunAsync(cli, report, timeout.Token).GetAwaiter().GetResult();
    }

    private static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
