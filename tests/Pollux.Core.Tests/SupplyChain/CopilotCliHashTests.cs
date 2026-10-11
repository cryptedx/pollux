using System.Security.Cryptography;
using System.Xml.Linq;

namespace Pollux.Core.Tests.SupplyChain;

/// <summary>
/// S-18: the Copilot SDK checks the downloaded CLI only against SHA256SUMS.txt from the same release, so whoever can
/// swap the archive (or point CopilotCliReleaseBaseUrl at a mirror) can swap the checksum too. Pollux records the
/// expected hash per CLI version and architecture in build/CopilotCliHashes.targets and fails the build on a mismatch.
/// The tests run that target in a scratch project with a stand-in archive.
/// </summary>
[TestClass]
public sealed class CopilotCliHashTests
{
    private static string HashesFile => Path.Combine(RepoFiles.Root, "build", "CopilotCliHashes.targets");

    private string _dir = "";

    [TestInitialize]
    public void Setup() => _dir = Directory.CreateTempSubdirectory("pollux-cli-hash-").FullName;

    [TestCleanup]
    public void Cleanup() => Directory.Delete(_dir, recursive: true);

    /// <summary>Writes a stand-in copilot.tgz (or none) into a cache folder like the SDK's and runs the check on it.</summary>
    private (int ExitCode, string Output, string CacheDir) Verify(string version, string platform, byte[]? archive, string extraItems = "")
    {
        var cache = Path.Combine(_dir, "obj", "copilot-cli", version, platform);
        Directory.CreateDirectory(cache);
        if (archive != null)
            File.WriteAllBytes(Path.Combine(cache, "copilot.tgz"), archive);
        File.WriteAllText(Path.Combine(cache, ".copilot-runtime-complete"), version);
        var project = Path.Combine(_dir, "check.proj");
        File.WriteAllText(project, $"""
            <Project>
              <Import Project="{HashesFile}" />
              <ItemGroup>{extraItems}</ItemGroup>
            </Project>
            """);
        var (code, output) = RepoFiles.Dotnet(_dir, null, "msbuild", project, "-nologo", "-t:PolluxVerifyCopilotCli",
            "-p:TargetFramework=net10.0", $"-p:CopilotCliVersion={version}", $"-p:_CopilotPlatform={platform}",
            $"-p:_CopilotCacheDir={cache}");
        return (code, output, cache);
    }

    [TestMethod]
    public void ATamperedCliArchiveFailsTheBuildAndIsThrownAway()
    {
        // The attacker controls both the archive and the release's SHA256SUMS.txt, so the SDK's own check passes.
        var (code, output, cache) = Verify("1.0.94", "win32-x64", "not the real copilot cli"u8.ToArray());

        Assert.AreNotEqual(0, code, output);
        Assert.Contains("POLLUX0018", output);
        Assert.IsFalse(Directory.Exists(cache), "The tampered bundle must not stay in the cache for the next build.");
    }

    [TestMethod]
    public void ACliVersionWithoutARecordedHashFailsTheBuild()
    {
        // An SDK bump that pulls a new CLI must come with the new hashes; it must not fall back to trusting the download.
        var (code, output, _) = Verify("9.9.9", "win32-arm64", "anything"u8.ToArray());

        Assert.AreNotEqual(0, code, output);
        Assert.Contains("POLLUX0019", output);
    }

    [TestMethod]
    public void AnArchiveMatchingTheRecordedHashPasses()
    {
        var archive = "a known archive"u8.ToArray();
        var hash = Convert.ToHexString(SHA256.HashData(archive)).ToLowerInvariant();

        var (code, output, cache) = Verify("0.0.1-test", "win32-x64", archive,
            $"""<PolluxCopilotCliHash Include="0.0.1-test/win32-x64" Sha256="{hash}" />""");

        Assert.AreEqual(0, code, output);
        Assert.IsTrue(File.Exists(Path.Combine(cache, "copilot.tgz")));
    }

    [TestMethod]
    public void AMissingArchiveFailsInsteadOfSkippingTheCheck()
    {
        // A cached bundle whose archive is gone cannot be checked; refuse rather than ship it unchecked.
        var (code, output, _) = Verify("1.0.94", "win32-x64", archive: null);

        Assert.AreNotEqual(0, code, output);
        Assert.Contains("POLLUX0020", output);
    }

    [TestMethod]
    public void HashesAreRecordedForBothWindowsArchitecturesOfTheBundledCli()
    {
        var recorded = XDocument.Load(HashesFile).Descendants("PolluxCopilotCliHash")
            .ToDictionary(e => (string)e.Attribute("Include")!, e => (string)e.Attribute("Sha256")!);
        var version = BundledCliVersion();

        foreach (var platform in new[] { "win32-x64", "win32-arm64" })
        {
            Assert.IsTrue(recorded.TryGetValue($"{version}/{platform}", out var hash), $"No hash for Copilot CLI {version} {platform}.");
            Assert.MatchesRegex("^[0-9a-f]{64}$", hash);
        }
    }

    /// <summary>The CLI version the referenced GitHub.Copilot.SDK package pins (its build/GitHub.Copilot.SDK.props).</summary>
    private static string BundledCliVersion()
    {
        var sdkVersion = (string)XDocument.Load(Path.Combine(RepoFiles.Root, "Directory.Packages.props"))
            .Descendants("PackageVersion").Single(e => (string?)e.Attribute("Include") == "GitHub.Copilot.SDK").Attribute("Version")!;
        var packages = Environment.GetEnvironmentVariable("NUGET_PACKAGES") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
        var props = Path.Combine(packages, "github.copilot.sdk", sdkVersion, "build", "GitHub.Copilot.SDK.props");
        if (!File.Exists(props))
            Assert.Inconclusive("GitHub.Copilot.SDK is not in the NuGet package folder: " + props);
        return XDocument.Load(props).Descendants("CopilotCliVersion").Single().Value;
    }
}
