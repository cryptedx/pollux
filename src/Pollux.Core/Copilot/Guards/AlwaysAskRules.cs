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
        // DNS lookups and pings carry data out in host names (SECURITY.md S-11).
        "nslookup", "resolve-dnsname", "dig", "ping", "test-connection", "tracert", "pathping", "nltest",
    };

    /// <summary>
    /// Environment variables commands use as path prefixes (<c>$env:TEMP\log.txt</c>, <c>%LOCALAPPDATA%\Pollux</c>); reading
    /// any other variable may reveal a token (<c>GH_TOKEN</c>) and asks (SECURITY.md S-17).
    /// </summary>
    private static readonly HashSet<string> PathVariables = new(StringComparer.OrdinalIgnoreCase)
    {
        "USERPROFILE", "HOME", "HOMEDRIVE", "HOMEPATH", "APPDATA", "LOCALAPPDATA", "TEMP", "TMP", "SystemRoot", "windir",
        "SystemDrive", "ProgramData", "ProgramFiles", "ProgramW6432", "CommonProgramFiles", "PUBLIC", "ALLUSERSPROFILE",
        "OneDrive", "OneDriveCommercial", "OneDriveConsumer",
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

        if (c.Name == "setx")
        {
            yield return "Persistent environment change (setx)";
        }

        if (ReadsEnvironment(c))
        {
            yield return ReadsEnvironmentReason;
        }
    }

    /// <summary>The reason given when a command reads environment variables (they may hold tokens, SECURITY.md S-17).</summary>
    public const string ReadsEnvironmentReason = "Reads environment variables";

    /// <summary>
    /// The command lists or reads environment variables: <c>printenv</c>, bare <c>env</c>, <c>set</c> / <c>export</c> without
    /// an assignment, the <c>env:</c> drive, or <c>$env:NAME</c> / <c>%NAME%</c> of a variable that is not a path prefix.
    /// </summary>
    private static bool ReadsEnvironment(SimpleCommand c)
    {
        var args = c.Arguments;
        var lists = c.Name switch
        {
            "printenv" => true,
            "env" => args.Count == 0,
            "set" => args.Count == 0 || (!args[0].Contains('=', StringComparison.Ordinal) && !args[0].StartsWith('/')),
            "export" => args.Count == 0 || args[0] == "-p",
            _ => false,
        };

        return lists || args.Prepend(c.RawName).Any(a => IsEnvDrive(a) || NamesEnvironmentVariable(a));
    }

    /// <summary>A path on PowerShell's env: drive (<c>env:</c>, <c>Env:\GH_TOKEN</c>, <c>-Path:env:X</c>).</summary>
    private static bool IsEnvDrive(string a)
    {
        var value = a.StartsWith('-') && a.Contains(':', StringComparison.Ordinal) ? a.Split(':', 2)[1] : a;
        return value.StartsWith("env:", StringComparison.OrdinalIgnoreCase);
    }

    private static bool NamesEnvironmentVariable(string a) =>
        EnvironmentVariable().Matches(a).Any(m => !PathVariables.Contains(m.Groups["ps"].Success ? m.Groups["ps"].Value : m.Groups["cmd"].Value));

    [GeneratedRegex(@"\$\{?env:(?<ps>\w+)|%(?<cmd>[A-Za-z_]\w*)%", RegexOptions.IgnoreCase)]
    private static partial Regex EnvironmentVariable();

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

        if (sub == "config" && rest.Any(a => a is "--global" or "--system"))
        {
            yield return "Changes global git configuration (git config --global/--system)";
        }
    }

    /// <summary>
    /// The file <c>git config --file &lt;f&gt;</c> / <c>-f &lt;f&gt;</c> / <c>--file=&lt;f&gt;</c> works on; null for any other command.
    /// The Guard Rules treat it as a write target (SECURITY.md S-11): the global config under another name is persistence.
    /// </summary>
    public static string? GitConfigFile(SimpleCommand c)
    {
        if (c.Name != "git" || GitSubcommand(c.Arguments) is not ("config", var rest))
        {
            return null;
        }

        for (var i = 0; i < rest.Count; i++)
        {
            if (rest[i] is "--file" or "-f" && i + 1 < rest.Count)
            {
                return rest[i + 1];
            }

            if (rest[i].StartsWith("--file=", StringComparison.Ordinal))
            {
                return rest[i]["--file=".Length..];
            }
        }

        return null;
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
