using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;

namespace Pollux.Copilot;

/// <summary>
/// Proves an installed Pollux can drive its Copilot CLI: starts it over stdio, completes the protocol handshake,
/// pings it and runs a source-generated tool. Run by <c>Pollux.exe --copilot-selftest &lt;report&gt;</c> on the
/// Native AOT build (ADR 0002), so CI and installs verify what unit tests cannot. Needs no sign-in.
/// </summary>
public static class CopilotSelfTest
{
    /// <returns>Exit code: 0 passed, 1 failed.</returns>
    public static async Task<int> RunAsync(CopilotCli cli, TextWriter report, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(report);
        await report.WriteLineAsync($"cli: {cli}").ConfigureAwait(false);
        try
        {
            var handshake = await CopilotCliProbe.HandshakeAsync(cli, cancellationToken).ConfigureAwait(false);
            await report.WriteLineAsync($"handshake: CLI {handshake.CliVersion}, protocol {handshake.ProtocolVersion}").ConfigureAwait(false);

            var tool = CopilotTools.Define(() => handshake, "pollux_self_test", "Reports the CLI handshake", SelfTestJson.Default);
            var result = await tool.InvokeAsync(new AIFunctionArguments(), cancellationToken).ConfigureAwait(false);
            await report.WriteLineAsync($"tool: {JsonSerializer.Serialize(result, tool.JsonSerializerOptions.GetTypeInfo(typeof(object)))}").ConfigureAwait(false);

            await report.WriteLineAsync("PASSED").ConfigureAwait(false);
            return 0;
        }
#pragma warning disable CA1031 // A self-test reports every failure instead of crashing.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            await report.WriteLineAsync($"FAILED: {ex.GetType().Name}: {ex.Message}").ConfigureAwait(false);
            return 1;
        }
    }
}

[JsonSerializable(typeof(CopilotHandshake))]
internal sealed partial class SelfTestJson : JsonSerializerContext;
