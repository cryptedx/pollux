namespace Pollux.Core.Tests;

[TestClass]
public sealed class PolluxPathsTests
{
    private static readonly Dictionary<string, string> Env = new()
    {
        ["APPDATA"] = Path.Combine("C:", "Users", "marko", "AppData", "Roaming"),
        ["LOCALAPPDATA"] = Path.Combine("C:", "Users", "marko", "AppData", "Local"),
    };

    [TestMethod]
    public void ConfigLivesInRoamingAppData()
    {
        var paths = PolluxPaths.FromEnvironment(name => Env.GetValueOrDefault(name));

        Assert.AreEqual(Path.Combine(Env["APPDATA"], "Pollux"), paths.ConfigDirectory);
    }

    [TestMethod]
    public void DataAndLogsLiveInLocalAppData()
    {
        var paths = PolluxPaths.FromEnvironment(name => Env.GetValueOrDefault(name));

        Assert.AreEqual(Path.Combine(Env["LOCALAPPDATA"], "Pollux"), paths.DataDirectory);
        Assert.AreEqual(Path.Combine(Env["LOCALAPPDATA"], "Pollux", "logs"), paths.LogsDirectory);
    }

    [TestMethod]
    public void MissingEnvironmentVariableFailsLoudly()
    {
        var ex = Assert.ThrowsExactly<InvalidOperationException>(
            () => PolluxPaths.FromEnvironment(name => name == "APPDATA" ? null : Env.GetValueOrDefault(name)));

        StringAssert.Contains(ex.Message, "APPDATA");
    }
}
