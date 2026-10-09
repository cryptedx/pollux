using Pollux.Core.Copilot;
using Pollux.Core.Settings;

namespace Pollux.Core.Tests.Copilot;

[TestClass]
public sealed class CopilotAvailabilityTests
{
    [TestMethod]
    public void AvailableWhenEnabledInANormalInstance()
    {
        Assert.IsTrue(CopilotAvailability.IsAvailable(new CopilotSettings { Enabled = true }, adminInstance: false));
    }

    [TestMethod]
    public void UnavailableWhileDisabled()
    {
        Assert.IsFalse(CopilotAvailability.IsAvailable(new CopilotSettings(), adminInstance: false));
    }

    [TestMethod]
    public void NeverAvailableInTheAdminInstance()
    {
        Assert.IsFalse(CopilotAvailability.IsAvailable(new CopilotSettings { Enabled = true }, adminInstance: true));
    }

    [TestMethod]
    [DataRow("copilot.togglePanel", true)]
    [DataRow("copilot.explain", true)]
    [DataRow("integration.copilot", false)]
    [DataRow("tab.new", false)]
    [DataRow("copilotish.other", false)]
    public void CopilotCommandsAreRecognisedByTheirId(string id, bool isCopilotCommand)
    {
        Assert.AreEqual(isCopilotCommand, CopilotAvailability.IsCopilotCommand(id));
    }

    [TestMethod]
    public void CopilotCommandsAreAllowedOnlyWhileAvailable()
    {
        Assert.IsFalse(CopilotAvailability.AllowsCommand("copilot.togglePanel", available: false));
        Assert.IsTrue(CopilotAvailability.AllowsCommand("copilot.togglePanel", available: true));
        Assert.IsTrue(CopilotAvailability.AllowsCommand("tab.new", available: false));
        Assert.IsTrue(CopilotAvailability.AllowsCommand("integration.copilot", available: false));
    }

    [TestMethod]
    [DataRow("statusBar.copilotQuota", false, false)]
    [DataRow("statusBar.copilotQuota", true, true)]
    [DataRow("statusBar.cpu", false, true)]
    [DataRow("copilot.enabled", false, true)]
    public void TheQuotaSettingIsShownOnlyWhileCopilotIsAvailable(string key, bool available, bool shown)
    {
        Assert.AreEqual(shown, CopilotAvailability.ShowsSetting(key, available));
    }

    [TestMethod]
    public void TheSelfTestIsRefusedInTheAdminInstance()
    {
        Assert.IsTrue(CopilotAvailability.AllowsSelfTest(adminInstance: false, ciRun: false));
        Assert.IsFalse(CopilotAvailability.AllowsSelfTest(adminInstance: true, ciRun: false));
    }

    [TestMethod]
    public void TheSelfTestRunsOnCiRunnersWhoseProcessesAreElevated() =>
        Assert.IsTrue(CopilotAvailability.AllowsSelfTest(adminInstance: true, ciRun: true));
}
