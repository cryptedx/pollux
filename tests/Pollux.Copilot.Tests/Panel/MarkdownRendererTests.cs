using System.Diagnostics;
using System.Text.Json;

namespace Pollux.Copilot.Tests.Panel;

/// <summary>
/// The Copilot Panel's Markdown renderer (src/Pollux.App/Assets/web/markdown.js), run in node. S-16: Copilot output
/// with forged inline-code placeholders (NUL + index + NUL) must not break or hijack Chat rendering.
/// Without node on PATH the tests are inconclusive locally but fail on CI (env <c>CI</c> set).
/// </summary>
[TestClass]
public sealed class MarkdownRendererTests
{
    [TestMethod]
    public void InlineCodeAndEmphasisRender() =>
        Assert.AreEqual("<p><code>a &lt; b</code> and <strong>bold</strong></p>", Render("`a < b` and **bold**"));

    [TestMethod]
    public void PlaceholderWithoutCodeDoesNotThrow()
    {
        // Before: codes[7] is undefined, esc(undefined) throws and the streaming render loop stays stuck.
        var html = Render("answer \u00007\u0000 end");

        Assert.IsFalse(html.Contains('\u0000', StringComparison.Ordinal));
        Assert.IsFalse(html.Contains("undefined", StringComparison.Ordinal));
        StringAssert.StartsWith(html, "<p>answer ");
    }

    [TestMethod]
    public void ForgedPlaceholderDoesNotDuplicateOtherCode()
    {
        // Before: the forged "\0 0 \0" was replaced by the first code span, so text the model did not put in backticks
        // came out as code (and as a second copy of it).
        var html = Render("`rm -rf /` then \u00000\u0000");

        Assert.AreEqual(1, html.Split("<code>").Length - 1, html);
        StringAssert.Contains(html, "<code>rm -rf /</code>");
    }

    [TestMethod]
    public void NulInsideCodeSpanIsDropped() =>
        Assert.AreEqual("<p><code>ab</code></p>", Render("`a\u0000b`"));

    private static string Render(string markdown)
    {
        var node = FindNode();
        var renderer = Path.Combine(RepoRoot(), "src", "Pollux.App", "Assets", "web", "markdown.js");
        var start = new ProcessStartInfo(node)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add("-e");
        start.ArgumentList.Add("""
            const render = require(process.argv[1]);
            let input = '';
            process.stdin.setEncoding('utf8');
            process.stdin.on('data', d => input += d);
            process.stdin.on('end', () => process.stdout.write(JSON.stringify(render(JSON.parse(input)))));
            """);
        start.ArgumentList.Add(renderer);

        using var process = Process.Start(start)!;
        process.StandardInput.Write(JsonString(markdown));
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.AreEqual(0, process.ExitCode, error);
        using var doc = JsonDocument.Parse(output);
        return doc.RootElement.GetString()!;
    }

    private static string JsonString(string value)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms)) w.WriteStringValue(value);
        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
    }

    private static string FindNode()
    {
        var name = OperatingSystem.IsWindows() ? "node.exe" : "node";
        var node = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(dir => Path.Combine(dir, name))
            .FirstOrDefault(File.Exists);
        if (node is not null) return node;
        if (Environment.GetEnvironmentVariable("CI") is { Length: > 0 }) Assert.Fail("The Markdown renderer tests need node on PATH.");
        Assert.Inconclusive("The Markdown renderer tests need node on PATH.");
        return "";
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Pollux.slnx"))) return dir.FullName;
        throw new InvalidOperationException("Pollux.slnx not found above " + AppContext.BaseDirectory);
    }
}
