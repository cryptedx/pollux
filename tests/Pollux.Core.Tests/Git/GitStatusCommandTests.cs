using Pollux.Core.Git;

namespace Pollux.Core.Tests.Git;

/// <summary>The hardening of the git status invocation (SECURITY.md S-01, ADR 0006).</summary>
[TestClass]
public sealed class GitStatusCommandTests
{
    [TestMethod]
    public void AttributesComeFromTheEmptyTreeSoRepoFiltersAreNeverSelected()
    {
        // S-01: ".gitattributes" with "* filter=evil" plus a filter in .git/config ran the clean command on git status.
        Assert.AreEqual("4b825dc642cb6eb9a060e54bf8d69288fbbd4904", GitStatusCommand.Environment["GIT_ATTR_SOURCE"]);
    }

    [TestMethod]
    public void ASha256RepositoryGetsTheSha256EmptyTree()
    {
        Assert.AreEqual("6ef19b41225c5369f1c104d45d8d85efa9b057b53b14b4b9b939dd74decc5321", GitStatusCommand.EnvironmentFor("sha256")["GIT_ATTR_SOURCE"]);
        Assert.AreEqual("4b825dc642cb6eb9a060e54bf8d69288fbbd4904", GitStatusCommand.EnvironmentFor(null)["GIT_ATTR_SOURCE"]);
        Assert.AreEqual("4b825dc642cb6eb9a060e54bf8d69288fbbd4904", GitStatusCommand.EnvironmentFor("sha1")["GIT_ATTR_SOURCE"]);
        Assert.AreEqual("1", GitStatusCommand.EnvironmentFor("sha256")["GIT_CONFIG_NOSYSTEM"]);
    }

    [TestMethod]
    public void SystemConfigIsNotReadAndOptionalLocksStayOff()
    {
        Assert.AreEqual("1", GitStatusCommand.Environment["GIT_CONFIG_NOSYSTEM"]);
        Assert.AreEqual("0", GitStatusCommand.Environment["GIT_OPTIONAL_LOCKS"]);
    }

    [TestMethod]
    public void FsmonitorAndHooksStayOffAndStatusIsMachineReadable()
    {
        var args = string.Join(' ', GitStatusCommand.Arguments);

        StringAssert.Contains(args, "-c core.fsmonitor=false");
        StringAssert.Contains(args, "-c core.hooksPath=NUL");
        StringAssert.Contains(args, "status --porcelain=v1 -z");
    }
}
