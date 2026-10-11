using System.Text.RegularExpressions;

namespace Pollux.Copilot.Tests.Panel;

/// <summary>
/// The Copilot Panel page (src/Pollux.App/Assets/web/copilot.html). Its CSP allows scripts only from files
/// (no 'unsafe-inline'); an inline script is silently blocked and leaves the Panel without theme, Send and Enter.
/// </summary>
[TestClass]
public sealed partial class PanelPageTests
{
    [TestMethod]
    public void PageHasNoInlineScript()
    {
        var html = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Pollux.App", "Assets", "web", "copilot.html"));

        StringAssert.Contains(html, "script-src 'self' https://pollux.assets;");
        foreach (Match script in ScriptTag().Matches(html))
            StringAssert.Contains(script.Value, " src=", "Inline <script> is blocked by the page's CSP: " + script.Value);
    }

    [GeneratedRegex(@"<script\b[^>]*>")]
    private static partial Regex ScriptTag();

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Pollux.slnx"))) return dir.FullName;
        throw new InvalidOperationException("Pollux.slnx not found above " + AppContext.BaseDirectory);
    }
}
