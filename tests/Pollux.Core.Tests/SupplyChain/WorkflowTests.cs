using System.Text.RegularExpressions;

namespace Pollux.Core.Tests.SupplyChain;

/// <summary>
/// S-18: the GitHub workflows that build and publish releases. These checks read the committed workflow files,
/// so a change that reopens a hole (a tag-pinned action, a workflow-wide write token, an overwriting upload) fails here
/// before it reaches a release.
/// </summary>
[TestClass]
public sealed partial class WorkflowTests
{
    private static readonly string[] Workflows = ["ci.yml", "release.yml"];

    private static string Workflow(string name) =>
        File.ReadAllText(Path.Combine(RepoFiles.Root, ".github", "workflows", name)).Replace("\r\n", "\n", StringComparison.Ordinal);

    [TestMethod]
    public void EveryActionIsPinnedToAFullCommitSha()
    {
        // A tag (v4) can be moved by whoever controls the action's repo; a commit SHA cannot.
        foreach (var name in Workflows)
        {
            var uses = UsesLine().Matches(Workflow(name)).Select(m => m.Groups[1].Value).ToList();
            Assert.IsNotEmpty(uses, name);
            foreach (var reference in uses)
                Assert.IsTrue(PinnedReference().IsMatch(reference), $"{name}: '{reference}' is not pinned to a commit SHA with a version comment.");
        }
    }

    [TestMethod]
    public void NoJobGetsWriteAccessExceptTheReleaseUpload()
    {
        foreach (var name in Workflows)
        {
            var text = Workflow(name);
            var topLevel = Permissions(text, indent: 0);
            Assert.IsNotNull(topLevel, $"{name}: no top-level permissions block (the default token can write).");
            Assert.IsEmpty(topLevel, $"{name}: the workflow-wide token must have no permissions; grant them per job.");

            foreach (var (job, block) in Jobs(text))
            {
                var permissions = Permissions(block, indent: 4);
                Assert.IsNotNull(permissions, $"{name}/{job}: no job-level permissions block.");
                var writes = permissions.Where(p => p.Value == "write").Select(p => p.Key).ToList();
                if (name == "release.yml" && job == "release")
                    CollectionAssert.Contains(writes, "contents", "The release upload job needs contents: write.");
                else
                    Assert.IsEmpty(writes, $"{name}/{job} can write ({string.Join(", ", writes)}); it runs third-party build code.");
            }
        }
    }

    [TestMethod]
    public void TheBuildJobThatRunsTestsAndNuGetTargetsIsReadOnly()
    {
        var build = Jobs(Workflow("release.yml"))["build"];
        var permissions = Permissions(build, indent: 4)!;
        CollectionAssert.AreEquivalent(new[] { "contents" }, permissions.Keys.ToArray());
        Assert.AreEqual("read", permissions["contents"]);
    }

    [TestMethod]
    public void ReleasesPublishChecksumsAndBuildAttestations()
    {
        var release = Jobs(Workflow("release.yml"))["release"];

        Assert.Contains("sha256sum Pollux-*.zip > SHA256SUMS", release);
        Assert.Contains("actions/attest-build-provenance@", release);
        Assert.Contains("subject-path: Pollux-*.zip", release);
        var permissions = Permissions(release, indent: 4)!;
        Assert.AreEqual("write", permissions.GetValueOrDefault("id-token"), "Attestations are signed with the job's OIDC token.");
        Assert.AreEqual("write", permissions.GetValueOrDefault("attestations"));

        var upload = release.Split('\n').Single(l => l.TrimStart().StartsWith("run: gh release upload", StringComparison.Ordinal));
        Assert.Contains("SHA256SUMS", upload);
        Assert.Contains("Pollux-*.zip", upload);
    }

    [TestMethod]
    public void TheForgejoPublishWorkflowPinsItsActionsToo()
    {
        // publish-github.yml holds GH_PUBLISH_TOKEN, the most privileged token on the release path.
        var text = File.ReadAllText(Path.Combine(RepoFiles.Root, ".forgejo", "workflows", "publish-github.yml"));
        var uses = UsesLine().Matches(text).Select(m => m.Groups[1].Value).ToList();
        Assert.IsNotEmpty(uses);
        foreach (var reference in uses)
            Assert.IsTrue(PinnedForgejoReference().IsMatch(reference), $"publish-github.yml: '{reference}' is not pinned to a commit SHA with a version comment.");
    }

    [GeneratedRegex(@"^(https://[\w.-]+/)?[\w.-]+/[\w./-]+@[0-9a-f]{40} # v\d+(\.\d+)*$")]
    private static partial Regex PinnedForgejoReference();

    [TestMethod]
    public void UploadsNeverReplaceExistingReleaseAssets()
    {
        // With --clobber a rerun (or a compromised build) silently swaps the zips users already verified.
        foreach (var name in Workflows)
            Assert.DoesNotContain("--clobber", Workflow(name), name);
    }

    [TestMethod]
    public void PublishTakesTheRidFromPlatformSoLockedRestoresStayValid()
    {
        // A -r on the command line becomes a global RuntimeIdentifier for Pollux.Core and Pollux.Copilot too, whose lock
        // files are written without one; locked mode then rejects the restore (NU1004).
        foreach (var name in Workflows)
        {
            var publish = Workflow(name).Split('\n').Where(l => l.Contains("dotnet publish", StringComparison.Ordinal)).ToList();
            Assert.IsNotEmpty(publish, name);
            foreach (var line in publish)
            {
                Assert.Contains("-p:Platform=", line);
                Assert.IsFalse(Regex.IsMatch(line, @"\s(-r|--runtime)\s"), $"{name}: {line.Trim()}");
            }
        }
    }

    /// <summary>The jobs under <c>jobs:</c>, each with the text of its block.</summary>
    internal static Dictionary<string, string> Jobs(string workflow)
    {
        var jobs = new Dictionary<string, string>();
        var lines = workflow.Split('\n');
        var start = Array.IndexOf(lines, "jobs:");
        Assert.IsGreaterThanOrEqualTo(0, start, "no jobs: section");
        string? current = null;
        var block = new List<string>();
        foreach (var line in lines.Skip(start + 1))
        {
            if (line.Length > 0 && !line.StartsWith(' ') && !line.StartsWith('#'))
                break;
            var header = JobHeader().Match(line);
            if (header.Success)
            {
                if (current != null) jobs[current] = string.Join('\n', block);
                current = header.Groups[1].Value;
                block.Clear();
            }
            else if (current != null)
                block.Add(line);
        }
        if (current != null) jobs[current] = string.Join('\n', block);
        return jobs;
    }

    /// <summary>
    /// The <c>permissions:</c> map at the given indent, or null when there is none. <c>permissions: {}</c> is an empty map.
    /// </summary>
    internal static Dictionary<string, string>? Permissions(string block, int indent)
    {
        var pad = new string(' ', indent);
        var lines = block.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = StripComment(lines[i]);
            if (!line.StartsWith(pad + "permissions:", StringComparison.Ordinal) || (line.Length > indent && line[indent] == ' '))
                continue;
            var inline = line[(pad.Length + "permissions:".Length)..].Trim();
            if (inline == "{}")
                return [];
            Assert.AreEqual("", inline, $"Use a permissions map, not '{inline}'.");
            var map = new Dictionary<string, string>();
            foreach (var next in lines.Skip(i + 1).Select(StripComment))
            {
                if (next.Trim().Length == 0) continue;
                if (next.Length - next.TrimStart().Length <= indent) break;
                var parts = next.Split(':', 2);
                map[parts[0].Trim()] = parts[1].Trim();
            }
            return map;
        }
        return null;
    }

    private static string StripComment(string line)
    {
        var hash = line.IndexOf(" #", StringComparison.Ordinal);
        return (hash >= 0 ? line[..hash] : line).TrimEnd();
    }

    [GeneratedRegex(@"^\s*(?:-\s*)?uses:\s*(.+?)\s*$", RegexOptions.Multiline)]
    private static partial Regex UsesLine();

    [GeneratedRegex(@"^[\w.-]+/[\w./-]+@[0-9a-f]{40} # v\d+\.\d+\.\d+$")]
    private static partial Regex PinnedReference();

    [GeneratedRegex(@"^  ([\w-]+):\s*$")]
    private static partial Regex JobHeader();
}
