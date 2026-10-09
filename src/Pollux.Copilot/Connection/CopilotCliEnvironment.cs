namespace Pollux.Copilot.Connection;

/// <summary>
/// The environment of the Copilot CLI process when <c>copilot.proxy</c> / <c>copilot.caCertificates</c> are set. They apply
/// to that process only, never to Pollux itself (ADR 0001/0005).
/// </summary>
public static class CopilotCliEnvironment
{
    /// <summary>
    /// The inherited environment plus proxy and CA bundle, without any <c>OTEL_*</c> / <c>COPILOT_OTEL_*</c> variable (Pollux never
    /// opts in to OpenTelemetry export); null when there is nothing to add or remove (the SDK then inherits as usual).
    /// The SDK replaces the whole environment when one is given, hence the copy.
    /// </summary>
    /// <param name="caCertificates">Path of a PEM file with additional CA certificates (Node reads one file).</param>
    public static Dictionary<string, string>? Build(string? proxy, string? caCertificates, IReadOnlyDictionary<string, string> inherited)
    {
        var hasProxy = !string.IsNullOrWhiteSpace(proxy);
        var hasCerts = !string.IsNullOrWhiteSpace(caCertificates);
        var env = new Dictionary<string, string>(inherited, StringComparer.OrdinalIgnoreCase);
        var removed = env.Keys.Where(IsOpenTelemetry).ToList().Count(env.Remove);
        if (!hasProxy && !hasCerts && removed == 0)
        {
            return null;
        }

        if (hasProxy)
        {
            env["HTTPS_PROXY"] = proxy!.Trim();
            env["HTTP_PROXY"] = proxy.Trim();
        }

        if (hasCerts)
        {
            env["NODE_EXTRA_CA_CERTS"] = Environment.ExpandEnvironmentVariables(caCertificates!.Trim());
        }

        return env;
    }

    private static bool IsOpenTelemetry(string name) =>
        name.StartsWith("OTEL_", StringComparison.OrdinalIgnoreCase) || name.StartsWith("COPILOT_OTEL_", StringComparison.OrdinalIgnoreCase);

    /// <summary>The current process environment as a dictionary.</summary>
    public static Dictionary<string, string> Current()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())
        {
            if (e.Key is string k && e.Value is string v) result[k] = v;
        }

        return result;
    }
}
