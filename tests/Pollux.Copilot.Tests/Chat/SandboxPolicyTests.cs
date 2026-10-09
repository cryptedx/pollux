using Pollux.Copilot.Chat;
using Pollux.Core.Settings;

namespace Pollux.Copilot.Tests.Chat;

[TestClass]
public sealed class SandboxPolicyTests
{
    private static readonly ChatModes Manual = new(CopilotAgentMode.Interactive, CopilotPermissionMode.Manual);

    [TestMethod]
    public void DefaultIsOffForInteractiveManualAndOnForAutopilotOrAllowAll()
    {
        Assert.IsFalse(SandboxPolicy.None.Resolve(Manual, null).Enabled);
        Assert.IsFalse(SandboxPolicy.None.Resolve(new(CopilotAgentMode.Plan, CopilotPermissionMode.Assisted), null).Enabled);
        Assert.IsTrue(SandboxPolicy.None.Resolve(Manual with { Agent = CopilotAgentMode.Autopilot }, null).Enabled);
        Assert.IsTrue(SandboxPolicy.None.Resolve(Manual with { Permission = CopilotPermissionMode.AllowAll }, null).Enabled);
    }

    [TestMethod]
    public void UsersChoiceBeatsTheDefault()
    {
        Assert.IsFalse(SandboxPolicy.None.Resolve(Manual with { Permission = CopilotPermissionMode.AllowAll }, false).Enabled);
        Assert.IsTrue(SandboxPolicy.None.Resolve(Manual, true).Enabled);
        Assert.IsFalse(SandboxPolicy.None.Resolve(Manual, true).Locked);
    }

    [TestMethod]
    public void ForcedByOrganizationIsOnAndLocked()
    {
        var state = new SandboxPolicy(ForcedByOrganization: true).Resolve(Manual, false);
        Assert.IsTrue(state.Enabled);
        Assert.IsTrue(state.Locked);
        Assert.Contains("organization", state.LockedReason!);
    }

    [TestMethod]
    public void UnsupportedIsOffAndLockedWithTheReason()
    {
        var state = new SandboxPolicy("No container support.").Resolve(Manual with { Agent = CopilotAgentMode.Autopilot }, true);
        Assert.IsFalse(state.Enabled);
        Assert.AreEqual("No container support.", state.LockedReason);
    }

    [TestMethod]
    public void FromCliAnswers()
    {
        Assert.AreEqual(new SandboxPolicy(SandboxPolicy.UnsupportedDefault, false), SandboxPolicy.From(false, " ", false));
        Assert.AreEqual(new SandboxPolicy("x", true), SandboxPolicy.From(false, "x", true));
        Assert.AreEqual(SandboxPolicy.None, SandboxPolicy.From(true, null, false));
    }
}
