using System.Text.Json;
using Pollux.Copilot.Chat;
using Pollux.Core.Settings;

namespace Pollux.Copilot.Tests.Chat;

/// <summary>Which Permission Modes an organization policy (the CLI's managed settings) blocks, and why.</summary>
[TestClass]
public sealed class ModePolicyTests
{
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [TestMethod]
    public void WithoutManagedSettingsEveryModeIsAllowed()
    {
        var policy = ModePolicy.FromManagedSettings(bypassPermissionsDisabled: false, failClosed: false, settings: null);

        foreach (var mode in Enum.GetValues<CopilotPermissionMode>()) Assert.IsNull(policy.BlockedReason(mode));
    }

    [TestMethod]
    public void DisabledBypassPermissionsBlocksAllowAll()
    {
        var policy = ModePolicy.FromManagedSettings(true, false, null);

        StringAssert.Contains(policy.BlockedReason(CopilotPermissionMode.AllowAll), "organization");
        Assert.IsNull(policy.BlockedReason(CopilotPermissionMode.Assisted));
        Assert.IsNull(policy.BlockedReason(CopilotPermissionMode.Manual));
    }

    [TestMethod]
    public void BypassPolicyThatOnlyAllowsAutoBlocksAllowAll()
    {
        var policy = ModePolicy.FromManagedSettings(false, false, Json("""{ "permissions": { "disableBypassPermissionsMode": "allow-auto-only" } }"""));

        Assert.IsNotNull(policy.BlockedReason(CopilotPermissionMode.AllowAll));
    }

    [TestMethod]
    public void DisabledAssistedPermissionsBlocksAssisted()
    {
        var policy = ModePolicy.FromManagedSettings(false, false, Json("""{ "permissions": { "disableAssistedPermissionsMode": true } }"""));

        StringAssert.Contains(policy.BlockedReason(CopilotPermissionMode.Assisted), "organization");
        Assert.IsNull(policy.BlockedReason(CopilotPermissionMode.AllowAll));
    }

    [TestMethod]
    public void UndeterminedPolicyBlocksAllowAllAndSaysSo()
    {
        var policy = ModePolicy.FromManagedSettings(true, failClosed: true, null);

        StringAssert.Contains(policy.BlockedReason(CopilotPermissionMode.AllowAll), "could not be checked");
    }

    [TestMethod]
    public void ManualIsNeverBlocked()
    {
        var policy = ModePolicy.None.Block(CopilotPermissionMode.Manual, "no");

        Assert.IsNull(policy.BlockedReason(CopilotPermissionMode.Manual));
    }

    [TestMethod]
    public void ARefusedModeIsBlockedWithTheGivenReason()
    {
        var policy = ModePolicy.None.Block(CopilotPermissionMode.Assisted, "Assisted Permissions are disabled by your administrator.");

        Assert.AreEqual("Assisted Permissions are disabled by your administrator.", policy.BlockedReason(CopilotPermissionMode.Assisted));
    }
}
