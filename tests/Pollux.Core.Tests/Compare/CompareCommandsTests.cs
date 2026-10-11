using Pollux.Core.Commands;
using Pollux.Core.Compare;
using Pollux.Core.Copilot;
using Pollux.Core.Settings;

namespace Pollux.Core.Tests.Compare;

[TestClass]
public sealed class CompareCommandsTests
{
    [TestMethod]
    public void CompareCommandsAreInThePaletteAndCompareHasAKey()
    {
        var ids = CommandRegistry.Commands(new CopilotSettings(), copilotAvailable: false).Select(c => c.Id).ToList();
        var keys = CommandRegistry.Keybindings(new CopilotSettings(), []);

        foreach (var id in new[] { "pane.compare", "pane.compareRecursive", "pane.compareContent", "pane.selectDifferences", "pane.syncToOther" })
        {
            CollectionAssert.Contains(ids, id);
        }

        Assert.AreEqual("pane.compare", keys.Resolve(KeyChord.Parse("ctrl+alt+d"), Context.Empty));
    }

    [TestMethod]
    public void CompareSettingsDefaultToAFlatComparisonByDateAndSize()
    {
        var options = new PolluxSettings().Compare.Options(includeHidden: false);

        Assert.AreEqual(new CompareOptions(Recursive: false, CompareContent: false, IncludeHidden: false), options);
    }

    [TestMethod]
    public void CompareSettingsAreReadFromSettingsJson()
    {
        var settings = SettingsStore.Parse("""{ "compare": { "recursive": true, "content": true } }""").Settings;

        Assert.AreEqual(new CompareOptions(Recursive: true, CompareContent: true, IncludeHidden: true), settings.Compare.Options(includeHidden: true));
    }
}
