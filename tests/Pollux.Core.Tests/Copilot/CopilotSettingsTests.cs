using Pollux.Core.Settings;

namespace Pollux.Core.Tests.Copilot;

[TestClass]
public sealed class CopilotSettingsTests
{
    [TestMethod]
    public void CopilotIsOffByDefault()
    {
        Assert.IsFalse(SettingsStore.Parse("{}").Settings.Copilot.Enabled);
    }

    [TestMethod]
    public void CopilotCanBeEnabled()
    {
        var result = SettingsStore.Parse("""{ "copilot": { "enabled": true } }""");

        Assert.IsTrue(result.IsValid, result.Error);
        Assert.IsTrue(result.Settings.Copilot.Enabled);
    }

    [TestMethod]
    public void CliPathDefaultsToBundledCli()
    {
        var result = SettingsStore.Parse("{}");

        Assert.IsNull(result.Settings.Copilot.CliPath);
    }

    [TestMethod]
    public void CliPathOverridesBundledCli()
    {
        var result = SettingsStore.Parse("""{ "copilot": { "cliPath": "C:\\tools\\copilot.exe" } }""");

        Assert.IsTrue(result.IsValid, result.Error);
        Assert.AreEqual(@"C:\tools\copilot.exe", result.Settings.Copilot.CliPath);
    }

    [TestMethod]
    public void CopilotSectionWrittenAsNullFallsBackToDefaults()
    {
        var result = SettingsStore.Parse("""{ "copilot": null }""");

        Assert.IsNotNull(result.Settings.Copilot);
    }

    [TestMethod]
    public void ProxyAndCaCertificatesAreOptionalSettings()
    {
        Assert.IsNull(SettingsStore.Parse("{}").Settings.Copilot.Proxy);

        var c = SettingsStore.Parse("""{ "copilot": { "proxy": "http://p:8080", "caCertificates": "C:\\c.pem" } }""").Settings.Copilot;

        Assert.AreEqual("http://p:8080", c.Proxy);
        Assert.AreEqual(@"C:\c.pem", c.CaCertificates);
    }

    [TestMethod]
    public void NewChatsStartInteractiveAndManualByDefault()
    {
        var c = SettingsStore.Parse("{}").Settings.Copilot;

        Assert.AreEqual(CopilotAgentMode.Interactive, c.DefaultAgentMode);
        Assert.AreEqual(CopilotPermissionMode.Manual, c.DefaultPermissionMode);
    }

    [TestMethod]
    public void DefaultModesOfNewChatsAreConfigurable()
    {
        var result = SettingsStore.Parse("""{ "copilot": { "defaultAgentMode": "plan", "defaultPermissionMode": "allowAll" } }""");

        Assert.IsTrue(result.IsValid, result.Error);
        Assert.AreEqual(CopilotAgentMode.Plan, result.Settings.Copilot.DefaultAgentMode);
        Assert.AreEqual(CopilotPermissionMode.AllowAll, result.Settings.Copilot.DefaultPermissionMode);
    }
}
