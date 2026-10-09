using GitHub.Copilot;
using Pollux.Copilot.Connection;

namespace Pollux.Copilot;

/// <summary>What the CLI reported when it accepted the SDK's protocol handshake.</summary>
public sealed record CopilotHandshake(string CliVersion, int ProtocolVersion);

/// <summary>Starts a CLI over stdio, completes the protocol handshake and pings it; used to verify an install.</summary>
public static class CopilotCliProbe
{
    /// <summary>Throws <see cref="InvalidOperationException"/> when the CLI is missing or speaks another protocol,
    /// <see cref="IOException"/> when the process dies.</summary>
    public static async Task<CopilotHandshake> HandshakeAsync(CopilotCli cli, CancellationToken cancellationToken)
    {
        await using var client = new CopilotClient(new CopilotClientOptions
        {
            Connection = RuntimeConnection.ForStdio(PathOf(cli)),
            LogLevel = CopilotLogLevel.Error,
            Environment = CopilotCliEnvironment.Build(null, null, CopilotCliEnvironment.Current()),
        });
        await client.StartAsync(cancellationToken);
        await client.PingAsync(cancellationToken: cancellationToken);
        var status = await client.GetStatusAsync(cancellationToken);
        await client.StopAsync();
        return new CopilotHandshake(status.Version, status.ProtocolVersion);
    }

    /// <summary>The executable the SDK launches; always explicit, so a stray COPILOT_CLI_PATH cannot swap the CLI.</summary>
    internal static string PathOf(CopilotCli cli) => cli switch
    {
        CopilotCli.Bundled b => b.Path,
        CopilotCli.Configured c => c.Path,
        CopilotCli.Missing m => throw new InvalidOperationException(m.Reason),
        _ => throw new ArgumentOutOfRangeException(nameof(cli)),
    };
}
