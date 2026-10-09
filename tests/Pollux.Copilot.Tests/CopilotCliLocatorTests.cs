namespace Pollux.Copilot.Tests;

[TestClass]
public sealed class CopilotCliLocatorTests
{
    private string _appDir = null!;

    [TestInitialize]
    public void CreateAppDirectory()
    {
        _appDir = Path.Combine(Path.GetTempPath(), "pollux-cli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_appDir);
    }

    [TestCleanup]
    public void DeleteAppDirectory() => Directory.Delete(_appDir, recursive: true);

    private string Bundle(string rid, string wrapper = "copilot-runtime.exe", string runtimeNodeContent = "node")
    {
        var native = Path.Combine(_appDir, "runtimes", rid, "native");
        Directory.CreateDirectory(native);
        File.WriteAllText(Path.Combine(native, wrapper), "exe");
        File.WriteAllText(Path.Combine(native, "runtime.node"), runtimeNodeContent);
        return Path.Combine(native, wrapper);
    }

    [TestMethod]
    public void BundledCliForTheAppArchitectureIsUsedWhenNoPathIsConfigured()
    {
        Bundle("win-x64");
        var arm = Bundle("win-arm64");

        var cli = CopilotCliLocator.Locate(configuredPath: null, _appDir, "win-arm64");

        Assert.AreEqual(new CopilotCli.Bundled(arm), cli);
    }

    [TestMethod]
    public void NonWindowsBundleHasNoExeSuffix()
    {
        var wrapper = Bundle("osx-arm64", wrapper: "copilot-runtime");

        var cli = CopilotCliLocator.Locate(null, _appDir, "osx-arm64");

        Assert.AreEqual(new CopilotCli.Bundled(wrapper), cli);
    }

    [TestMethod]
    public void BlankConfiguredPathMeansBundled()
    {
        var wrapper = Bundle("win-x64");

        var cli = CopilotCliLocator.Locate("  ", _appDir, "win-x64");

        Assert.AreEqual(new CopilotCli.Bundled(wrapper), cli);
    }

    [TestMethod]
    public void MissingBundleIsReportedWithTheSearchedPath()
    {
        var cli = CopilotCliLocator.Locate(null, _appDir, "win-x64");

        var missing = cli as CopilotCli.Missing;
        Assert.IsNotNull(missing);
        StringAssert.Contains(missing.Reason, Path.Combine("runtimes", "win-x64", "native", "copilot-runtime.exe"));
    }

    [TestMethod]
    public void BundleWithEmptyRuntimeNodeIsMissing()
    {
        Bundle("win-x64", runtimeNodeContent: "");

        var cli = CopilotCliLocator.Locate(null, _appDir, "win-x64");

        Assert.IsInstanceOfType<CopilotCli.Missing>(cli);
    }

    [TestMethod]
    public void ConfiguredPathOverridesTheBundledCli()
    {
        Bundle("win-x64");
        var own = Path.Combine(_appDir, "copilot.exe");
        File.WriteAllText(own, "exe");

        var cli = CopilotCliLocator.Locate(own, _appDir, "win-x64");

        Assert.AreEqual(new CopilotCli.Configured(own), cli);
    }

    [TestMethod]
    public void ConfiguredPathMayUseEnvironmentVariables()
    {
        var own = Path.Combine(_appDir, "copilot.exe");
        File.WriteAllText(own, "exe");
        Environment.SetEnvironmentVariable("POLLUX_TEST_CLI_DIR", _appDir);
        try
        {
            var cli = CopilotCliLocator.Locate(Path.Combine("%POLLUX_TEST_CLI_DIR%", "copilot.exe"), _appDir, "win-x64");

            Assert.AreEqual(new CopilotCli.Configured(own), cli);
        }
        finally
        {
            Environment.SetEnvironmentVariable("POLLUX_TEST_CLI_DIR", null);
        }
    }

    [TestMethod]
    public void ConfiguredPathThatDoesNotExistIsMissingAndNamesTheSetting()
    {
        Bundle("win-x64");
        var own = Path.Combine(_appDir, "nope", "copilot.exe");

        var cli = CopilotCliLocator.Locate(own, _appDir, "win-x64");

        var missing = cli as CopilotCli.Missing;
        Assert.IsNotNull(missing);
        StringAssert.Contains(missing.Reason, "copilot.cliPath");
        StringAssert.Contains(missing.Reason, own);
    }
}
