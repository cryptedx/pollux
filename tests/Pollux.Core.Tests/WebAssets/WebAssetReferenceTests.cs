using System.Text.RegularExpressions;
using Pollux.Core.Tests.SupplyChain;

namespace Pollux.Core.Tests.WebAssets;

/// <summary>
/// The pages Pollux serves from src/Pollux.App/Assets/web (https://pollux.assets/) and everything they load. A file the
/// page asks for but the app does not ship fails silently in the WebView (a 404, a blocked script, an editor that never
/// appears), so these checks resolve the references statically: script and stylesheet tags, the Monaco AMD module graph
/// (define dependency lists, lazy language loaders, require.toUrl targets such as the editor and language workers) and
/// the page names the host code opens. Run them after updating or pruning the vendored Monaco or xterm.
/// </summary>
[TestClass]
public sealed partial class WebAssetReferenceTests
{
    private static string WebRoot => Path.Combine(RepoFiles.Root, "src", "Pollux.App", "Assets", "web");

    private static IEnumerable<string> Pages() => Directory.EnumerateFiles(WebRoot, "*.html").Order(StringComparer.Ordinal);

    [TestMethod]
    public void EveryTagReferenceOfAPageExists()
    {
        var missing = new List<string>();
        var checkedCount = 0;
        foreach (var page in Pages())
        {
            var html = File.ReadAllText(page);
            foreach (Match tag in TagReference().Matches(html))
            {
                var reference = tag.Groups["url"].Value;
                if (IsExternal(reference))
                    continue;
                checkedCount++;
                var file = CheckFile(Path.GetDirectoryName(page)!, reference, Relative(page), missing);
                if (file != null && file.EndsWith(".css", StringComparison.OrdinalIgnoreCase))
                    CheckStylesheet(file, missing);
                if (file != null && file.EndsWith(".js", StringComparison.OrdinalIgnoreCase))
                    CheckPlainScript(page, file, missing);
            }
            foreach (var script in InlineScripts(html))
                CheckWorkers(Path.GetDirectoryName(page)!, script, Relative(page), missing);
        }

        Assert.IsGreaterThan(0, checkedCount, "No <script src> or <link href> found in Assets/web/*.html; has the tag pattern stopped matching?");
        Assert.IsEmpty(missing, "Assets/web references files that do not exist:\n" + string.Join("\n", missing));
    }

    [TestMethod]
    public void EveryModuleAndWorkerOfMonacoExists()
    {
        var missing = new List<string>();
        var amdPages = 0;
        var workers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var page in Pages())
        {
            var html = File.ReadAllText(page);
            var inline = string.Join("\n", InlineScripts(html));
            var config = RequireConfig().Match(inline);
            if (!config.Success)
                continue;
            amdPages++;
            var paths = PathEntry().Matches(config.Groups["paths"].Value)
                .ToDictionary(m => m.Groups["id"].Value, m => m.Groups["path"].Value.TrimEnd('/'), StringComparer.Ordinal);
            var graph = new ModuleGraph(Path.GetDirectoryName(page)!, paths, missing, workers);
            var entries = 0;
            foreach (Match call in RequireCall().Matches(inline))
                foreach (var id in Strings(call.Groups["deps"].Value))
                {
                    graph.Load(id, Relative(page));
                    entries++;
                }
            // Counts the ids the page requires, not the files found, so a missing entry module is reported by name below.
            Assert.IsGreaterThan(0, entries, $"{Relative(page)}: no require([...]) call found; has the require() pattern stopped matching?");
            graph.Run();
        }

        Assert.IsGreaterThan(0, amdPages, "No page configures require() paths; has the require.config pattern stopped matching?");
        // Missing files first: a missing module also hides the workers it would have led to.
        Assert.IsEmpty(missing, "The Monaco pages load files that do not exist:\n" + string.Join("\n", missing.Distinct()));
        Assert.IsNotEmpty(workers, "No require.toUrl worker URL found in Monaco; has the bundle changed shape?");
    }

    [TestMethod]
    public void PagesWhoseCspBlocksInlineScriptsHaveNone()
    {
        // An inline script under a CSP without 'unsafe-inline' is silently blocked; the Copilot Panel shipped dead that way.
        foreach (var page in Pages())
        {
            var html = File.ReadAllText(page);
            var csp = Csp().Match(html);
            var scriptSrc = ScriptSrc().Match(csp.Success ? csp.Groups["policy"].Value : "");
            if (!scriptSrc.Success || scriptSrc.Value.Contains("'unsafe-inline'", StringComparison.Ordinal))
                continue;
            foreach (Match script in ScriptTag().Matches(html))
                StringAssert.Contains(script.Value, " src=", $"{Relative(page)}: inline <script> is blocked by the page's CSP: {script.Value}");
        }
    }

    [TestMethod]
    public void EveryPageTheHostOpensExists()
    {
        var sources = new[] { "Pollux.App", "Pollux.Core" }
            .SelectMany(p => Directory.EnumerateFiles(Path.Combine(RepoFiles.Root, "src", p), "*.cs", SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
        var pages = sources
            .SelectMany(f => PageLiteral().Matches(File.ReadAllText(f)).Select(m => (File: f, Page: m.Groups["page"].Value)))
            .ToList();

        Assert.IsNotEmpty(pages, "No \"*.html\" page names found in the host code; has the pattern stopped matching?");
        var missing = pages.Where(p => !File.Exists(Path.Combine(WebRoot, p.Page)))
            .Select(p => $"{Path.GetRelativePath(RepoFiles.Root, p.File)}: \"{p.Page}\" -> missing Assets/web/{p.Page}");
        Assert.IsEmpty(missing, string.Join("\n", missing));
    }

    /// <summary>
    /// Follows the AMD loader (monaco/vs/loader.js) from the page's require() calls: module ids map to files through
    /// require.config paths (relative to the page), relative ids resolve against the requiring module's id.
    /// </summary>
    private sealed class ModuleGraph(string pageDir, Dictionary<string, string> paths, List<string> missing, HashSet<string> workers)
    {
        private static readonly string[] Special = ["require", "exports", "module"];

        private readonly Queue<(string Id, string From)> _queue = new();
        private readonly HashSet<string> _seen = new(StringComparer.Ordinal);

        public void Load(string id, string from)
        {
            // "vs/nls.messages-loader!" is a loader plugin: the module before '!' is loaded, the rest is its argument.
            var bang = id.IndexOf('!', StringComparison.Ordinal);
            if (bang >= 0)
                id = id[..bang];
            if (Special.Contains(id) || !_seen.Add(id))
                return;
            _queue.Enqueue((id, from));
        }

        public void Run()
        {
            while (_queue.TryDequeue(out var next))
            {
                var file = CheckFile(pageDir, ToPath(next.Id) + ".js", $"{next.From} -> {next.Id}", missing);
                if (file == null)
                    continue;
                var js = File.ReadAllText(file);
                var origin = Relative(file);
                foreach (Match define in Define().Matches(js))
                {
                    if (define.Groups["name"].Success && define.Groups["name"].Value != next.Id)
                        missing.Add($"{origin}: defines \"{define.Groups["name"].Value}\" but is loaded as \"{next.Id}\"");
                    foreach (var dep in Strings(define.Groups["deps"].Value))
                        Load(Resolve(next.Id, dep), origin);
                }
                foreach (Match call in LocalRequire().Matches(js))
                    foreach (var dep in Strings(call.Groups["deps"].Value))
                        Load(Resolve(next.Id, dep), origin);
                foreach (Match url in ToUrl().Matches(js))
                {
                    var target = CheckFile(pageDir, ToPath(Resolve(next.Id, url.Groups["url"].Value)), $"{origin}: toUrl(\"{url.Groups["url"].Value}\")", missing);
                    if (target != null && target.EndsWith(".js", StringComparison.OrdinalIgnoreCase))
                        workers.Add(Relative(target));
                    if (target != null && target.EndsWith(".css", StringComparison.OrdinalIgnoreCase))
                        CheckStylesheet(target, missing);
                }
            }
        }

        /// <summary>Resolves "./x" and "../x" against the directory of <paramref name="from"/>, both module ids.</summary>
        private static string Resolve(string from, string id)
        {
            if (!id.StartsWith("./", StringComparison.Ordinal) && !id.StartsWith("../", StringComparison.Ordinal))
                return id;
            var parts = from.Split('/').SkipLast(1).ToList();
            foreach (var part in id.Split('/'))
            {
                if (part == "..")
                {
                    if (parts.Count > 0)
                        parts.RemoveAt(parts.Count - 1);
                }
                else if (part != ".")
                    parts.Add(part);
            }
            return string.Join('/', parts);
        }

        /// <summary>The page-relative path of a module id: the longest matching paths entry, else the id itself.</summary>
        private string ToPath(string id)
        {
            foreach (var prefix in paths.Keys.OrderByDescending(k => k.Length))
                if (id == prefix || id.StartsWith(prefix + "/", StringComparison.Ordinal))
                    return paths[prefix] + id[prefix.Length..];
            return id;
        }
    }

    /// <summary>
    /// Resolves <paramref name="reference"/> (a URL path relative to <paramref name="baseDir"/>, or rooted at Assets/web)
    /// and returns the file, or records it in <paramref name="missing"/> and returns null.
    /// </summary>
    private static string? CheckFile(string baseDir, string reference, string from, List<string> missing)
    {
        var path = reference.Split('?', '#')[0];
        var full = Path.GetFullPath(path.StartsWith('/') ? Path.Combine(WebRoot, path[1..]) : Path.Combine(baseDir, path));
        if (!full.StartsWith(Path.GetFullPath(WebRoot) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            missing.Add($"{from}: \"{reference}\" -> outside Assets/web");
            return null;
        }
        if (File.Exists(full))
            return full;
        missing.Add($"{from}: \"{reference}\" -> missing {Relative(full)}");
        return null;
    }

    private static void CheckStylesheet(string file, List<string> missing)
    {
        foreach (Match url in CssUrl().Matches(File.ReadAllText(file)))
            if (!IsExternal(url.Groups["url"].Value))
                CheckFile(Path.GetDirectoryName(file)!, url.Groups["url"].Value, Relative(file), missing);
    }

    /// <summary>A classic script's worker URLs resolve against the page, not the script.</summary>
    private static void CheckPlainScript(string page, string file, List<string> missing) =>
        CheckWorkers(Path.GetDirectoryName(page)!, File.ReadAllText(file), Relative(file), missing);

    private static void CheckWorkers(string pageDir, string script, string from, List<string> missing)
    {
        foreach (Match worker in WorkerUrl().Matches(script))
            CheckFile(pageDir, worker.Groups["url"].Value, from, missing);
    }

    private static IEnumerable<string> InlineScripts(string html) =>
        InlineScript().Matches(html).Select(m => m.Groups["body"].Value);

    private static IEnumerable<string> Strings(string list) =>
        StringLiteral().Matches(list).Select(m => m.Groups["s"].Value);

    private static bool IsExternal(string url) =>
        url.Length == 0 || url.StartsWith('#') || url.StartsWith("//", StringComparison.Ordinal) || UrlScheme().IsMatch(url);

    private static string Relative(string file) => "Assets/web/" + Path.GetRelativePath(WebRoot, file).Replace('\\', '/');

    [GeneratedRegex(@"<(?:script|link|img|iframe|source)\b[^>]*?\s(?:src|href)\s*=\s*[""'](?<url>[^""']*)[""']", RegexOptions.IgnoreCase)]
    private static partial Regex TagReference();

    [GeneratedRegex(@"<script\b(?![^>]*\ssrc\s*=)[^>]*>(?<body>.*?)</script>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex InlineScript();

    [GeneratedRegex(@"<script\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex ScriptTag();

    [GeneratedRegex(@"http-equiv=""Content-Security-Policy""\s+content=""(?<policy>[^""]*)""", RegexOptions.IgnoreCase)]
    private static partial Regex Csp();

    [GeneratedRegex(@"script-src[^;]*")]
    private static partial Regex ScriptSrc();

    [GeneratedRegex(@"require\.config\(\s*\{[^}]*?paths\s*:\s*\{(?<paths>[^}]*)\}")]
    private static partial Regex RequireConfig();

    [GeneratedRegex(@"[""']?(?<id>[\w./-]+)[""']?\s*:\s*[""'](?<path>[^""']+)[""']")]
    private static partial Regex PathEntry();

    [GeneratedRegex(@"\brequire\(\s*\[(?<deps>[^\]]*)\]")]
    private static partial Regex RequireCall();

    [GeneratedRegex(@"\bdefine\(\s*(?:""(?<name>[^""]*)""\s*,\s*)?\[(?<deps>[^\]]*)\]")]
    private static partial Regex Define();

    /// <summary>A lazy load through a module's local require, e.g. <c>a(["../abap-D-t0cyap"],e,s)</c>.</summary>
    [GeneratedRegex(@"\(\[(?<deps>\s*""\.{1,2}/[^""]*""(?:\s*,\s*""[^""]*"")*\s*)\]\s*,")]
    private static partial Regex LocalRequire();

    [GeneratedRegex(@"\btoUrl\(\s*""(?<url>[^""]+)""\s*\)")]
    private static partial Regex ToUrl();

    [GeneratedRegex(@"new\s+(?:Shared)?Worker\(\s*[""'](?<url>[^""']+)[""']")]
    private static partial Regex WorkerUrl();

    [GeneratedRegex(@"url\(\s*[""']?(?<url>[^""')]+)[""']?\s*\)")]
    private static partial Regex CssUrl();

    [GeneratedRegex(@"[""'](?<s>[^""']*)[""']")]
    private static partial Regex StringLiteral();

    [GeneratedRegex(@"^[a-zA-Z][a-zA-Z0-9+.-]*:")]
    private static partial Regex UrlScheme();

    [GeneratedRegex(@"""(?:https://pollux\.assets/)?(?<page>[\w.-]+\.html)""")]
    private static partial Regex PageLiteral();
}
