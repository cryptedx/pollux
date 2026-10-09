using Pollux.Core.Copilot;
using Pollux.Core.Settings;

namespace Pollux.Core.Tests.Copilot;

[TestClass]
public sealed class SelectionActionsTests
{
    [TestMethod]
    public void BuiltInActionsComeFirst()
    {
        var actions = SelectionActions.For(new CopilotSettings());

        CollectionAssert.AreEqual(
            new[] { "Explain", "Summarize", "Rename by content" },
            actions.Select(a => a.Title).ToArray());
        Assert.IsTrue(actions.All(a => a.Prompt.Length > 0));
    }

    [TestMethod]
    public void UserTemplatesFollowBuiltInActionsWithTheirOwnCommandIds()
    {
        var settings = SettingsStore.Parse("""
            { "copilot": { "promptTemplates": [ { "name": "Find bugs", "prompt": "Look for bugs in the selection." } ] } }
            """).Settings.Copilot;

        var actions = SelectionActions.For(settings);

        Assert.AreEqual(4, actions.Count);
        Assert.AreEqual("Find bugs", actions[3].Title);
        Assert.AreEqual("Look for bugs in the selection.", actions[3].Prompt);
        Assert.AreEqual(actions[3], SelectionActions.Find(settings, actions[3].CommandId));
        Assert.AreEqual(actions.Count, actions.Select(a => a.CommandId).Distinct().Count());
        Assert.IsTrue(actions.All(a => CopilotAvailability.IsCopilotCommand(a.CommandId)));
    }

    [TestMethod]
    public void IncompleteTemplatesAreIgnored()
    {
        var settings = SettingsStore.Parse("""
            { "copilot": { "promptTemplates": [ { "name": "", "prompt": "x" }, { "name": "No prompt" }, null ] } }
            """).Settings.Copilot;

        Assert.AreEqual(3, SelectionActions.For(settings).Count);
    }

    [TestMethod]
    public void UnknownCommandFindsNothing()
    {
        Assert.IsNull(SelectionActions.Find(new CopilotSettings(), "copilot.action.nope"));
        Assert.IsNull(SelectionActions.Find(new CopilotSettings(), "file.open"));
    }
}
