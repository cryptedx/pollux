using System.Text.Json;
using Pollux.Copilot.Connection;
using Pollux.Copilot.Panel;

namespace Pollux.Copilot.Tests.Panel;

[TestClass]
public sealed class PanelActionTests
{
    [TestMethod]
    public void StateViewCarriesItsActionButton()
    {
        var view = CopilotConnectionState.For(CopilotConnectionIssue.NotSignedIn).ToView();
        using var doc = JsonDocument.Parse(PanelMessages.State(view));
        Assert.AreEqual("signIn", doc.RootElement.GetProperty("action").GetString());
        Assert.AreEqual("Sign in", doc.RootElement.GetProperty("actionLabel").GetString());
    }

    [TestMethod]
    public void ReadyStateIsTheNoStateView()
    {
        Assert.AreEqual(PanelStateView.None, CopilotConnectionState.Ready.ToView());
    }

    [TestMethod]
    public void PageActionsAreParsed()
    {
        Assert.AreEqual(new PanelEvent.Action(CopilotPanelAction.Recheck), PanelMessages.Parse("""{"type":"action","id":"recheck"}"""));
        Assert.AreEqual(new PanelEvent.Action(CopilotPanelAction.OpenSettings), PanelMessages.Parse("""{"type":"action","id":"openSettings"}"""));
    }

    [TestMethod]
    public void UnknownPageActionsAreIgnored()
    {
        Assert.IsNull(PanelMessages.Parse("""{"type":"action","id":"format-disk"}"""));
        Assert.IsNull(PanelMessages.Parse("""{"type":"action"}"""));
    }
}
