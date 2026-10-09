using Pollux.Copilot.Connection;

namespace Pollux.Copilot.Tests.Connection;

[TestClass]
public sealed class CopilotCliEnvironmentTests
{
    private static readonly Dictionary<string, string> Inherited = new() { ["PATH"] = "p" };

    [TestMethod]
    public void NothingConfiguredLeavesTheEnvironmentAlone()
    {
        Assert.IsNull(CopilotCliEnvironment.Build(null, null, Inherited));
        Assert.IsNull(CopilotCliEnvironment.Build("  ", "", Inherited));
    }

    [TestMethod]
    public void ProxyAndCertificatesAreAddedToTheInheritedEnvironment()
    {
        var env = CopilotCliEnvironment.Build("http://proxy:8080", @"C:\certs\corp.pem", Inherited)!;
        Assert.AreEqual("p", env["PATH"]);
        Assert.AreEqual("http://proxy:8080", env["HTTPS_PROXY"]);
        Assert.AreEqual("http://proxy:8080", env["HTTP_PROXY"]);
        Assert.AreEqual(@"C:\certs\corp.pem", env["NODE_EXTRA_CA_CERTS"]);
    }

    [TestMethod]
    public void CertificatesAlone()
    {
        var env = CopilotCliEnvironment.Build(null, @"C:\c.pem", Inherited)!;
        Assert.IsFalse(env.ContainsKey("HTTPS_PROXY"));
        Assert.AreEqual(@"C:\c.pem", env["NODE_EXTRA_CA_CERTS"]);
    }

    [TestMethod]
    public void OpenTelemetryVariablesAreRemovedEvenWithoutProxyOrCertificates()
    {
        var inherited = new Dictionary<string, string>
        {
            ["PATH"] = "p", ["OTEL_EXPORTER_OTLP_ENDPOINT"] = "http://x", ["otel_sdk_disabled"] = "false", ["COPILOT_OTEL_ENABLED"] = "true",
            ["COPILOT_OTEL_FILE_EXPORTER_PATH"] = "f", ["MYOTEL_X"] = "keep",
        };

        var env = CopilotCliEnvironment.Build(null, null, inherited)!;

        CollectionAssert.AreEquivalent(new[] { "PATH", "MYOTEL_X" }, env.Keys.ToArray());
    }

    [TestMethod]
    public void OpenTelemetryVariablesAreRemovedWhenProxyIsSet()
    {
        var env = CopilotCliEnvironment.Build("http://proxy:8080", null, new Dictionary<string, string> { ["OTEL_TRACES_EXPORTER"] = "otlp" })!;
        Assert.IsFalse(env.ContainsKey("OTEL_TRACES_EXPORTER"));
        Assert.AreEqual("http://proxy:8080", env["HTTPS_PROXY"]);
    }
}
