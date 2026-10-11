using System.Text.Json.Nodes;

namespace Pollux.Core.Tests.SupplyChain;

/// <summary>
/// S-18: NuGet restores are locked to the package versions and content hashes recorded in packages.lock.json, and CI
/// restores in locked mode, so a changed or swapped dependency fails the build instead of slipping in.
/// </summary>
[TestClass]
public sealed class NuGetLockTests
{
    private static readonly Dictionary<string, string?> Ci = new() { ["CI"] = "true" };

    private string _dir = "";

    [TestInitialize]
    public void Setup() => _dir = Directory.CreateTempSubdirectory("pollux-lock-").FullName;

    [TestCleanup]
    public void Cleanup() => Directory.Delete(_dir, recursive: true);

    private static IEnumerable<string> Projects() =>
        new[] { "src", "tests" }.SelectMany(d => Directory.EnumerateFiles(Path.Combine(RepoFiles.Root, d), "*.csproj", SearchOption.AllDirectories));

    private string LockPath => Path.Combine(_dir, "packages.lock.json");

    /// <summary>
    /// A project that uses the repo's build settings (Directory.Build.props), restored once as a developer would,
    /// which writes its lock file. Returns the lock file's JSON.
    /// </summary>
    private JsonNode LockedFixture()
    {
        File.WriteAllText(Path.Combine(_dir, "Directory.Build.props"),
            $"""<Project><Import Project="{Path.Combine(RepoFiles.Root, "Directory.Build.props")}" /></Project>""");
        File.WriteAllText(Path.Combine(_dir, "Directory.Packages.props"), "<Project />");
        File.WriteAllText(Path.Combine(_dir, "Fixture.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
            </Project>
            """);
        var (code, output) = RepoFiles.Dotnet(_dir, null, "restore", "Fixture.csproj");
        Assert.AreEqual(0, code, output);
        Assert.IsTrue(File.Exists(LockPath), "Restore did not write packages.lock.json.\n" + output);
        return JsonNode.Parse(File.ReadAllText(LockPath))!;
    }

    /// <summary>Restores like a fresh CI checkout: no obj folder from earlier restores, CI=true.</summary>
    private (int ExitCode, string Output) CiRestore()
    {
        Directory.Delete(Path.Combine(_dir, "obj"), recursive: true);
        return RepoFiles.Dotnet(_dir, Ci, "restore", "Fixture.csproj");
    }

    private static JsonNode FirstPackage(JsonNode lockJson)
    {
        var packages = lockJson["dependencies"]!["net10.0"]!.AsObject();
        Assert.IsNotEmpty(packages, "The fixture has no locked packages to tamper with.\n" + lockJson);
        return packages.First().Value!;
    }

    [TestMethod]
    public void EveryProjectHasACommittedLockFile()
    {
        var projects = Projects().ToList();
        Assert.IsGreaterThanOrEqualTo(5, projects.Count);
        foreach (var project in projects)
            Assert.IsTrue(File.Exists(Path.Combine(Path.GetDirectoryName(project)!, "packages.lock.json")), $"No packages.lock.json next to {project}.");
    }

    [TestMethod]
    public void TheSdkIsPinnedExactlyBecauseLockFilesRecordItsImplicitPackages()
    {
        // ILLink comes from the SDK in the SDK's own version; a rolled-forward SDK would not match the lock files.
        var sdk = JsonNode.Parse(File.ReadAllText(Path.Combine(RepoFiles.Root, "global.json")))!["sdk"]!;
        Assert.AreEqual("disable", (string?)sdk["rollForward"]);
        Assert.MatchesRegex(@"^\d+\.\d+\.\d{3}$", (string?)sdk["version"]);
    }

    [TestMethod]
    public void CiAlwaysRestoresAgainstTheCommittedLockFile()
    {
        // Off Windows, developer restores keep their lock file in obj (see Directory.Build.targets); CI must not.
        var core = Path.Combine(RepoFiles.Root, "src", "Pollux.Core", "Pollux.Core.csproj");

        var (code, output) = RepoFiles.Dotnet(_dir, Ci, "msbuild", core, "-nologo",
            "-getProperty:NuGetLockFilePath", "-getProperty:RestoreLockedMode", "-getProperty:RestorePackagesWithLockFile");

        Assert.AreEqual(0, code, output);
        var properties = JsonNode.Parse(output)!["Properties"]!;
        var lockPath = (string?)properties["NuGetLockFilePath"];
        Assert.IsTrue(string.IsNullOrEmpty(lockPath), $"CI uses '{lockPath}' instead of the committed packages.lock.json.");
        Assert.AreEqual("true", (string?)properties["RestoreLockedMode"]);
        Assert.AreEqual("true", (string?)properties["RestorePackagesWithLockFile"]);
    }

    [TestMethod]
    public void CiRestoresAnUnchangedLockFile()
    {
        LockedFixture();

        var (code, output) = CiRestore();

        Assert.AreEqual(0, code, output);
    }

    [TestMethod]
    public void CiRefusesADependencyVersionTheLockFileDoesNotRecord()
    {
        // An unreviewed dependency change: what the build resolves differs from the committed lock file.
        var lockJson = LockedFixture();
        var package = FirstPackage(lockJson);
        package["resolved"] = "0.0.1";
        package["requested"] = "[0.0.1, )";
        var tampered = lockJson.ToJsonString();
        File.WriteAllText(LockPath, tampered);

        var (code, output) = CiRestore();

        Assert.AreNotEqual(0, code, output);
        Assert.Contains("NU1004", output);
        Assert.AreEqual(tampered, File.ReadAllText(LockPath), "Locked mode must not rewrite the lock file.");
    }

    [TestMethod]
    public void CiRefusesAPackageWhoseContentDiffersFromTheRecordedHash()
    {
        // A swapped package (same id and version, other content) no longer matches the recorded content hash.
        // Changing the recorded hash produces the same mismatch without having to forge a package.
        var lockJson = LockedFixture();
        FirstPackage(lockJson)["contentHash"] = Convert.ToBase64String(new byte[64]);
        File.WriteAllText(LockPath, lockJson.ToJsonString());

        var (code, output) = CiRestore();

        Assert.AreNotEqual(0, code, output);
        Assert.Contains("NU1403", output);
    }
}
