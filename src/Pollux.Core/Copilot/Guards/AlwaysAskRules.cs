using System.Text.RegularExpressions;

namespace Pollux.Core.Copilot.Guards;

/// <summary>The built-in "always ask" commands of the Guard Rules (spec list 2).</summary>
internal static partial class AlwaysAskRules
{
    private static readonly HashSet<string> RemoveCommands = new(StringComparer.Ordinal) { "rm", "del", "rd", "remove-item" };

    private static readonly HashSet<string> ItemCommands = new(StringComparer.Ordinal)
    {
        "new-item", "set-item", "remove-item", "rm", "del", "rd", "copy-item", "move-item", "rename-item", "clear-item",
    };

    private static readonly HashSet<string> ServiceCommands = new(StringComparer.Ordinal)
    {
        "new-service", "set-service", "remove-service", "start-service", "stop-service", "restart-service",
        "suspend-service", "resume-service",
        "register-scheduledtask", "unregister-scheduledtask", "set-scheduledtask", "start-scheduledtask",
        "stop-scheduledtask", "enable-scheduledtask", "disable-scheduledtask", "launchctl", "at",
    };

    private static readonly HashSet<string> NetworkCommands = new(StringComparer.Ordinal)
    {
        "curl", "wget", "invoke-webrequest", "invoke-restmethod", "scp", "sftp", "ssh", "ftp", "tftp", "rsync", "telnet",
        "nc", "ncat", "netcat", "test-netconnection", "send-mailmessage", "start-bitstransfer", "bitsadmin",
        "enter-pssession", "new-pssession", "gh", "az", "aws", "gcloud", "npx",
    };

    private static readonly Dictionary<string, string[]> PackageInstalls = new(StringComparer.Ordinal)
    {
        ["npm"] = ["install", "i", "ci", "add", "update", "publish"],
        ["pnpm"] = ["install", "i", "add", "update", "publish", "dlx"],
        ["yarn"] = ["install", "add", "upgrade", "publish", "dlx"],
        ["bun"] = ["install", "add", "update", "publish"],
        ["pip"] = ["install", "download"],
        ["pip3"] = ["install", "download"],
        ["winget"] = ["install", "upgrade", "update", "download"],
        ["choco"] = ["install", "upgrade"],
        ["scoop"] = ["install", "update"],
        ["install-module"] = [],
        ["install-package"] = [],
    };

    private static readonly HashSet<string> GitNetwork = new(StringComparer.Ordinal) { "push", "pull", "fetch", "clone", "ls-remote" };

    public static IEnumerable<string> Reasons(SimpleCommand c)
    {
        var args = c.Arguments;
        if (RemoveCommands.Contains(c.Name) && args.Any(IsRecursiveFlag))
        {
            yield return $"Recursive delete ({c.RawName})";
        }

        if (IsRegistryWrite(c))
        {
            yield return $"Registry write ({c.RawName})";
        }

        if (c.Name == "set-executionpolicy")
        {
            yield return "Changes the PowerShell execution policy";
        }

        if (IsServiceOrTaskChange(c))
        {
            yield return $"Changes services or scheduled tasks ({c.RawName})";
        }

        if (c.Name == "git")
        {
            foreach (var reason in GitReasons(args))
            {
                yield return reason;
            }
        }

        if (IsNetworkCommand(c))
        {
            yield return $"Reaches the network ({c.RawName})";
        }
    }

    /// <summary>The command reaches the network (downloads, remote shells, package installs, git fetch/push, ...).</summary>
    public static bool ReachesNetwork(SimpleCommand c) =>
        IsNetworkCommand(c) || (c.Name == "git" && GitSubcommand(c.Arguments) is { } git && IsGitNetwork(git.Sub, git.Args));

    private static bool IsNetworkCommand(SimpleCommand c)
    {
        var args = c.Arguments;
        return NetworkCommands.Contains(c.Name)
            || (c.Name == "certutil" && args.Any(a => a.Contains("urlcache", StringComparison.OrdinalIgnoreCase)))
            || (PackageInstalls.TryGetValue(c.Name, out var verbs) && (verbs.Length == 0 || (args.Count > 0 && verbs.Contains(args[0], StringComparer.OrdinalIgnoreCase))))
            || (c.Name == "dotnet" && IsDotnetNetwork(args));
    }

    /// <summary>Any URL in the command text (http, https, ftp, …).</summary>
    public static bool ContainsUrl(string text) => UrlPattern().IsMatch(text);

    [GeneratedRegex(@"\b[a-z][a-z0-9+.\-]*://", RegexOptions.IgnoreCase)]
    private static partial Regex UrlPattern();

    private static bool IsRecursiveFlag(string a)
    {
        if (a.Equals("/s", StringComparison.OrdinalIgnoreCase) || a.Equals("--recursive", StringComparison.Ordinal))
        {
            return true;
        }

        if (ShellParser.IsParameter(a, "-recurse", 2))
        {
            return !a.EndsWith(":$false", StringComparison.OrdinalIgnoreCase);
        }

        // POSIX flag clusters like -rf, -fr, -Rfv.
        return a.Length >= 3 && a[0] == '-' && a[1] != '-' && a.Skip(1).All(ch => "rRfivdI".Contains(ch, StringComparison.Ordinal))
            && (a.Contains('r', StringComparison.Ordinal) || a.Contains('R', StringComparison.Ordinal));
    }

    private static bool IsRegistryPath(string a)
    {
        // -Path:HKCU:\x carries the value after the first colon.
        var value = a.StartsWith('-') && a.Contains(':', StringComparison.Ordinal) ? a.Split(':', 2)[1] : a;
        var drive = value.Split(':', 2)[0];
        return (value.Contains(':', StringComparison.Ordinal) && drive.ToUpperInvariant() is "HKCU" or "HKLM" or "HKCR" or "HKU" or "HKCC")
            || value.StartsWith("HKEY_", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("Registry::", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRegistryWrite(SimpleCommand c)
    {
        var args = c.Arguments;
        switch (c.Name)
        {
            case "reg":
                return args.Count > 0 && new[] { "add", "delete", "import", "copy", "restore", "load", "unload" }.Contains(args[0], StringComparer.OrdinalIgnoreCase);
            case "regedit":
            case "regini":
                return true;
        }

        if (c.Name.EndsWith("-itemproperty", StringComparison.Ordinal) && c.Name != "get-itemproperty")
        {
            return true;
        }

        return ItemCommands.Contains(c.Name) && args.Any(IsRegistryPath);
    }

    private static bool IsServiceOrTaskChange(SimpleCommand c)
    {
        var args = c.Arguments;
        var first = args.Count > 0 ? args[0].ToLowerInvariant() : "";
        return c.Name switch
        {
            "sc" => first.Length > 0 && !first.StartsWith('q') && first is not ("enumdepend" or "getdisplayname" or "getkeyname" or "sdshow" or "showsid"),
            "net" => first is "start" or "stop" or "pause" or "continue",
            "schtasks" => !args.Any(a => a.Equals("/query", StringComparison.OrdinalIgnoreCase)),
            "systemctl" => first is not ("" or "status" or "show" or "cat" or "is-active" or "is-enabled" or "is-failed") && !first.StartsWith("list", StringComparison.Ordinal),
            "crontab" => !args.Any(a => a == "-l"),
            _ => ServiceCommands.Contains(c.Name),
        };
    }

    private static IEnumerable<string> GitReasons(IReadOnlyList<string> args)
    {
        if (GitSubcommand(args) is not { } git)
        {
            yield break;
        }

        var (sub, rest) = git;
        if (sub == "push" && rest.Any(a => a is "-f" or "--force" || a.StartsWith("--force-", StringComparison.Ordinal) || (a.StartsWith('+') && a.Length > 1)))
        {
            yield return "Force push (git push --force)";
        }

        if (sub == "reset" && rest.Contains("--hard"))
        {
            yield return "Discards changes (git reset --hard)";
        }

        if (IsGitNetwork(sub, rest))
        {
            yield return $"Reaches the network (git {sub})";
        }
    }

    /// <summary>git's subcommand (lower case) and its arguments, after git's own options; null when there is none.</summary>
    private static (string Sub, List<string> Args)? GitSubcommand(IReadOnlyList<string> args)
    {
        var i = 0;
        while (i < args.Count && args[i].StartsWith('-'))
        {
            i += args[i] is "-C" or "-c" or "--git-dir" or "--work-tree" or "--namespace" ? 2 : 1;
        }

        return i >= args.Count ? null : (args[i].ToLowerInvariant(), args.Skip(i + 1).ToList());
    }

    private static bool IsGitNetwork(string sub, List<string> rest) =>
        GitNetwork.Contains(sub) || (sub is "remote" or "submodule" && rest.Count > 0 && rest[0] is "update" or "add");

    private static bool IsDotnetNetwork(IReadOnlyList<string> args) =>
        args.Count >= 2 && ((args[0] == "nuget" && args[1] == "push") || (args[0] == "add" && args.Contains("package")) || (args[0] == "tool" && args[1] is "install" or "update"));
}
