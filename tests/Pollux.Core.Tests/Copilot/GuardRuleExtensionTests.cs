using Pollux.Core.Copilot.Guards;
using Pollux.Core.Settings;

namespace Pollux.Core.Tests.Copilot;

[TestClass]
public sealed class GuardRuleExtensionTests
{
    private static readonly GuardContext Context =
        new(@"C:\repos\app", false, name => name.Equals("USERPROFILE", StringComparison.OrdinalIgnoreCase) ? @"C:\Users\marko" : null);

    private static GuardRules Rules() => new(new GuardRuleSettings
    {
        BlockedCommands = ["shutdown", "Stop-Computer", "iwr", "git push --tags"],
        AskCommands = ["docker run", "npm test"],
        ProtectedPaths = ["*.kdbx", @"~\Documents\Vault", @"C:\secrets\*.txt"],
    });

    private static GuardDecision Shell(string command) => Rules().Evaluate(ToolRequest.Shell(command), Context).Decision;

    [TestMethod]
    [DataRow("shutdown /s /t 0")]
    [DataRow("SHUTDOWN.EXE -r")]
    [DataRow("stop-computer -Force")]
    [DataRow("Invoke-WebRequest https://example.com")]
    [DataRow("git push origin --tags")]
    [DataRow("echo bye && cmd /c shutdown /s")]
    public void ExtraBlockedCommandsAreDenied(string command) =>
        Assert.AreEqual(GuardDecision.Deny, Shell(command), command);

    [TestMethod]
    [DataRow("docker run alpine")]
    [DataRow("npm test")]
    [DataRow("git status; docker run -it alpine sh")]
    public void ExtraAskCommandsAsk(string command) =>
        Assert.AreEqual(GuardDecision.Ask, Shell(command), command);

    [TestMethod]
    [DataRow("docker ps")]
    [DataRow("npm run build")]
    [DataRow("git push origin main")]
    public void ExtensionsMatchOnlyWithTheirArguments(string command) =>
        Assert.AreNotEqual(GuardDecision.Deny, Shell(command), command);

    [TestMethod]
    public void ExtraAskCommandWithoutItsArgumentsIsAllowed() =>
        Assert.AreEqual(GuardDecision.Allow, Shell("docker ps"));

    [TestMethod]
    [DataRow(@"D:\vault\passwords.kdbx")]
    [DataRow(@"C:\Users\marko\Documents\Vault\note.md")]
    [DataRow(@"C:\secrets\a.txt")]
    public void ExtraProtectedPathsAreDenied(string path) =>
        Assert.AreEqual(GuardDecision.Deny, Rules().Evaluate(ToolRequest.Read(path), Context).Decision, path);

    [TestMethod]
    public void DefaultsStillApplyWithExtensions()
    {
        Assert.AreEqual(GuardDecision.Deny, Shell("format D:"));
        Assert.AreEqual(GuardDecision.Deny, Rules().Evaluate(ToolRequest.Read(@"C:\Users\marko\.ssh\id_rsa"), Context).Decision);
        Assert.AreEqual(GuardDecision.Allow, Rules().Evaluate(ToolRequest.Read(@"C:\secrets\a.md"), Context).Decision);
    }

    [TestMethod]
    public void ExtensionsAreReadFromSettingsJson()
    {
        var result = SettingsStore.Parse("""
            {
              "copilot": {
                "guardRules": {
                  "blockedCommands": ["shutdown"],
                  "askCommands": ["docker run"],
                  "protectedPaths": ["*.kdbx"],
                  "untrustedPaths": ["*.eml"],
                }
              }
            }
            """);

        Assert.IsTrue(result.IsValid, result.Error);
        var rules = new GuardRules(result.Settings.Copilot.GuardRules);
        Assert.AreEqual(GuardDecision.Deny, rules.Evaluate(ToolRequest.Shell("shutdown /s"), Context).Decision);
        Assert.AreEqual(GuardDecision.Ask, rules.Evaluate(ToolRequest.Shell("docker run x"), Context).Decision);
        Assert.AreEqual(GuardDecision.Deny, rules.Evaluate(ToolRequest.Read(@"C:\x\db.kdbx"), Context).Decision);
        Assert.IsNotNull(rules.Taint(ToolRequest.Read(@"C:\x\mail.eml"), Context));
    }

    [TestMethod]
    public void MissingOrNullGuardRuleSettingsFallBackToEmptyLists()
    {
        var missing = SettingsStore.Parse("{}").Settings.Copilot.GuardRules;
        Assert.AreEqual(0, missing.BlockedCommands.Count);

        var nulls = SettingsStore.Parse("""{ "copilot": { "guardRules": { "blockedCommands": null, "askCommands": null, "protectedPaths": null, "untrustedPaths": null } } }""").Settings;
        Assert.IsNotNull(nulls.Copilot.GuardRules.UntrustedPaths);
        Assert.IsNotNull(nulls.Copilot.GuardRules.BlockedCommands);
        Assert.IsNotNull(nulls.Copilot.GuardRules.AskCommands);
        Assert.IsNotNull(nulls.Copilot.GuardRules.ProtectedPaths);

        var nullSection = SettingsStore.Parse("""{ "copilot": null }""").Settings;
        Assert.IsNotNull(nullSection.Copilot.GuardRules.ProtectedPaths);
    }
}
