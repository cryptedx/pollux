using System.Text;
using System.Text.RegularExpressions;
using Pollux.Core.Navigation;
using Pollux.Core.Settings;

namespace Pollux.Core.Copilot.Guards;

/// <summary>Pollux's own limits on Copilot that hold in every Permission Mode (see GLOSSARY: Guard Rules).</summary>
/// <param name="extensions">The user's extensions from settings; null: defaults only.</param>
/// <param name="folders">
/// The file system: lists the files below a folder a recursive search covers (to find protected files there) and resolves
/// junctions and symbolic links (paths are judged by where they really point); null: the real file system.
/// </param>
/// <param name="clock">Times the look for protected files below a searched folder (<see cref="MaxFolderCheckTime"/>); null: the system clock.</param>
/// <param name="places">Mapped network drives and cloud-sync roots (reading from them taints the Chat); null: the real machine.</param>
public sealed partial class GuardRules(GuardRuleSettings? extensions = null, IFolderFiles? folders = null, TimeProvider? clock = null, IRemotePlaces? places = null)
{
    /// <summary>The reason given when a shell command cannot be judged from its text (fail closed: the user is asked).</summary>
    public const string CannotAnalyse = "Guard Rules cannot analyse this command";

    /// <summary>The reason given when the folder of a recursive search has too many entries to look for protected files in time (fail closed).</summary>
    public const string FolderTooLarge = "Folder too large to check for protected files";

    /// <summary>The reason given when the junctions or symbolic links in a path cannot be resolved (fail closed: the user is asked).</summary>
    public const string CannotResolveLinks = "Guard Rules cannot resolve the links in this path";

    /// <summary>The reason a device namespace path (\\?\GLOBALROOT\..., \\.\PhysicalDrive0, volume GUIDs, pipes) is refused (SECURITY.md S-04).</summary>
    public const string DevicePath = "Blocked: device path";

    /// <summary>The reason opening a network location in Pollux asks: listing it authenticates to the host (SECURITY.md S-06).</summary>
    public const string NetworkLocation = "Opens a network location";

    /// <summary>
    /// The reason opening a relative location in Pollux is refused: Pollux would resolve it against its own current folder,
    /// not the Working Folder the rules judge it against (SECURITY.md S-06).
    /// </summary>
    public const string RelativeLocation = "Blocked: open an absolute path";

    /// <summary>At most this many entries below a searched folder are looked at ...</summary>
    public const int MaxFolderEntries = 20_000;

    /// <summary>... for at most this long; beyond either, the search is asked (<see cref="FolderTooLarge"/>).</summary>
    public static readonly TimeSpan MaxFolderCheckTime = TimeSpan.FromMilliseconds(150);

    /// <summary>The most protected files a reason names (the count covers all).</summary>
    private const int NamedProtectedFiles = 5;

    private readonly GuardRuleSettings _extensions = extensions ?? new();
    private readonly IFolderFiles _folders = folders ?? FileSystemFolderFiles.Instance;
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly IRemotePlaces _places = places ?? SystemRemotePlaces.Instance;
    private readonly ProtectedPaths _protected = new(extensions?.ProtectedPaths);
    private readonly UntrustedSources _untrusted = new(extensions?.UntrustedPaths);

    private static readonly HashSet<string> Partitioning = new(StringComparer.Ordinal)
    {
        "format", "format-volume", "diskpart", "clear-disk", "initialize-disk", "new-partition", "remove-partition",
        "resize-partition", "set-partition", "fdisk", "sfdisk", "parted", "wipefs",
    };

    private static readonly HashSet<string> Elevators = new(StringComparer.Ordinal) { "runas", "sudo", "gsudo", "doas", "su" };

    private static readonly HashSet<string> Downloaders = new(StringComparer.Ordinal)
    {
        "curl", "wget", "invoke-webrequest", "invoke-restmethod", "start-bitstransfer", "bitsadmin",
    };

    private static readonly string[] WebClientMarkers = ["Net.WebClient", "DownloadString", "DownloadFile", "DownloadData", "Net.Http.HttpClient"];

    private static readonly HashSet<string> Interpreters = new(StringComparer.Ordinal)
    {
        "invoke-expression", "sh", "bash", "zsh", "dash", "ksh", "fish", "powershell", "pwsh", "cmd",
        "python", "python3", "py", "node", "perl", "ruby", "php", "msiexec", "source", ".",
    };

    private static readonly HashSet<string> DirectoryChangers = new(StringComparer.Ordinal)
    {
        "cd", "chdir", "set-location", "sl", "pushd", "push-location",
    };

    /// <summary>cmd's own commands, the only ones whose /x arguments are switches (for anything else /x is a path).</summary>
    private static readonly HashSet<string> CmdStyleCommands = new(StringComparer.Ordinal)
    {
        "del", "rd", "copy", "move", "xcopy", "robocopy", "attrib", "icacls", "mklink", "dir", "findstr", "type",
    };

    /// <summary>Interpreters and their switches that run code given inline (python -c, node -e, ...).</summary>
    private static readonly Dictionary<string, string[]> InlineCode = new(StringComparer.Ordinal)
    {
        ["python"] = ["-c"], ["python3"] = ["-c"], ["py"] = ["-c"], ["pythonw"] = ["-c"],
        ["node"] = ["-e", "-p", "--eval", "--print"], ["deno"] = ["eval"], ["bun"] = ["-e", "--eval"],
        ["ruby"] = ["-e"], ["php"] = ["-r"], ["lua"] = ["-e"], ["osascript"] = ["-e"],
    };

    private static readonly HashSet<string> SafeVariables = new(StringComparer.OrdinalIgnoreCase) { "$true", "$false", "$null" };

    private static readonly HashSet<string> AssignmentOperators = new(StringComparer.Ordinal) { "=", "+=", "-=", "*=", "/=", "%=", "++", "--", "??=" };

    private static readonly HashSet<string> WriteCommands = new(StringComparer.Ordinal)
    {
        "rm", "del", "rd", "remove-item", "new-item", "set-item", "copy-item", "move-item", "rename-item", "clear-item",
        "set-content", "add-content", "clear-content", "out-file", "tee", "tee-object", "export-csv", "export-clixml",
        "cp", "copy", "mv", "move", "mkdir", "touch", "xcopy", "robocopy", "expand-archive", "compress-archive",
        "tar", "unzip", "ln", "mklink", "new-symlink", "attrib", "icacls", "chmod", "chown", "truncate", "dd",
    };

    /// <summary>
    /// The command names the built-in Blocked rules forbid outright (formatting, partitioning, elevation), lower case;
    /// used as the Copilot CLI's own deny rules (the first enforcement layer). Download-and-execute needs the whole
    /// statement and is enforced by <see cref="Evaluate"/> only.
    /// </summary>
    public static IReadOnlyList<string> BlockedCommandNames { get; } = [.. Partitioning.Order(StringComparer.Ordinal), "mkfs", .. Elevators.Order(StringComparer.Ordinal)];

    public GuardVerdict Evaluate(ToolRequest request, GuardContext context)
    {
        var findings = new Findings();
        if (request.Kind == ToolKind.Shell && request.Command is { } command)
        {
            var statements = ShellParser.Parse(command, out var incomplete);
            if (incomplete || IsUnanalysable(command, statements, context))
            {
                findings.Ask(CannotAnalyse);
            }

            foreach (var statement in statements)
            {
                if (IsDownloadAndExecute(statement))
                {
                    findings.Deny("Blocked: download and execute");
                }

                foreach (var c in statement.Commands)
                {
                    if (IsElevation(c))
                    {
                        findings.Deny($"Blocked: elevation ({c.RawName})");
                    }

                    if (Partitioning.Contains(c.Name) || c.Name.StartsWith("mkfs", StringComparison.Ordinal))
                    {
                        findings.Deny($"Blocked: formatting or partitioning ({c.RawName})");
                    }

                    if (_extensions.BlockedCommands.FirstOrDefault(e => Matches(c, e)) is { } blocked)
                    {
                        findings.Deny($"Blocked by settings: {blocked}");
                    }

                    if (_extensions.AskCommands.FirstOrDefault(e => Matches(c, e)) is { } ask)
                    {
                        findings.Ask($"Always ask (settings): {ask}");
                    }

                    foreach (var reason in AlwaysAskRules.Reasons(c))
                    {
                        findings.Ask(reason);
                    }

                    foreach (var token in SecretCandidates(c))
                    {
                        CheckSecret(token, context, findings);
                    }

                    foreach (var target in WriteTargets(c))
                    {
                        CheckWorkingFolder(target, context, findings);
                    }

                    foreach (var folder in RecursiveReadFolders(c))
                    {
                        CheckRecursiveRead(folder, context, findings);
                    }
                }

                foreach (var value in AssignedValues(statement))
                {
                    CheckSecret(value, context, findings);
                }

                foreach (var target in PipedWriteTargets(statement))
                {
                    CheckWorkingFolder(target, context, findings);
                }
            }

            if (AlwaysAskRules.ContainsUrl(command) || WebClientMarkers.Any(m => command.Contains(m, StringComparison.OrdinalIgnoreCase)))
            {
                findings.Ask("Accesses a URL");
            }
        }

        foreach (var path in request.Paths)
        {
            if (request.Navigates && !WinPath.IsAbsoluteLocation(path))
            {
                findings.Deny($"{RelativeLocation} ({path})");
                continue;
            }

            if (request.Navigates && RemoteShare(path, context) is { } share)
            {
                CheckNetworkLocation(share, context, findings);
                continue;
            }

            CheckSecret(path, context, findings);
            if (request.Kind == ToolKind.Write)
            {
                CheckWorkingFolder(path, context, findings);
            }

            if (request.Recursive)
            {
                CheckRecursiveRead(path, context, findings);
            }
        }

        foreach (var url in request.Urls)
        {
            CheckSecret(url, context, findings);
        }

        if (request.Kind == ToolKind.Url || request.Urls.Count > 0)
        {
            findings.Ask("Accesses a URL");
        }

        // A Tainted Chat asks before everything but a plain read: shell, writes, URLs, steering Pollux (navigating) and
        // tools of any other kind, with or without path arguments (SECURITY.md S-12).
        if (context.Tainted && (request.Kind != ToolKind.Read || request.Navigates))
        {
            findings.Ask("Tainted Chat: it has read untrusted content");
        }

        return findings.Verdict();
    }

    /// <summary>
    /// Whether the tool call brings untrusted content into the Chat (spec "Tainted Chat"): reading from an untrusted place
    /// (Downloads, Inbox, Temp, archives, <c>copilot.guardRules.untrustedPaths</c>), fetching a URL, or a shell command that
    /// names such a place or reaches the network. Returns the reason, or null. Writes alone do not taint.
    /// </summary>
    public string? Taint(ToolRequest request, GuardContext context)
    {
        if (request.Kind == ToolKind.Url || request.Urls.Count > 0)
        {
            return "Read web content";
        }

        if (request.Kind == ToolKind.Read && request.Paths.Select(p => Untrusted(p, context)).FirstOrDefault(r => r is not null) is { } read)
        {
            return read;
        }

        if (request.Kind == ToolKind.Shell && request.Command is { } command)
        {
            foreach (var c in ShellParser.Parse(command).SelectMany(s => s.Commands))
            {
                if (PathArguments(c).Select(p => Untrusted(p, context)).FirstOrDefault(r => r is not null) is { } named)
                {
                    return named;
                }
            }

            foreach (var c in ShellParser.Parse(command).SelectMany(s => s.Commands))
            {
                if (IsDownloader(c) || AlwaysAskRules.ReachesNetwork(c))
                {
                    return $"Reads from the network ({c.RawName})";
                }
            }

            if (AlwaysAskRules.ContainsUrl(command) || WebClientMarkers.Any(m => command.Contains(m, StringComparison.OrdinalIgnoreCase)))
            {
                return "Reads from the network (URL in the command)";
            }
        }

        return null;
    }

    /// <summary>
    /// Why reading <paramref name="path"/> brings untrusted content into the Chat, or null: judged as written and, for a
    /// local path, again where its junctions and symbolic links really point (SECURITY.md S-12), so a junction into
    /// Downloads or onto a share does not hide where the content comes from.
    /// </summary>
    private string? Untrusted(string path, GuardContext context)
    {
        if (LocalPath(path) is not { } local || GuardPaths.Resolve(local, context) is var resolved && GuardPaths.IsDevicePath(resolved))
        {
            return null;
        }

        // A share is judged as written: resolving its links would already contact the host.
        var written = UntrustedPlace(resolved, context);
        if (written is not null || WinPath.IsUnc(resolved))
        {
            return written;
        }

        return _folders.Canonical(resolved) is not { } real ? $"Read from a place whose links cannot be resolved ({resolved})"
            : string.Equals(real, resolved, StringComparison.OrdinalIgnoreCase) ? null
            : UntrustedPlace(GuardPaths.Resolve(real, context), context);
    }

    /// <summary>The untrusted place <paramref name="path"/> (resolved, absolute) lies in, as a reason; null when none.</summary>
    private string? UntrustedPlace(string path, GuardContext context)
    {
        if (WinPath.IsUnc(path))
        {
            return $"Read from an untrusted place (network location {path})";
        }

        if (_places.IsNetworkDrive(path))
        {
            return $"Read from an untrusted place (network drive {path[..2]})";
        }

        if (_places.SyncRoots().FirstOrDefault(root => WinPath.IsSameOrUnder(path, root)) is { } synced)
        {
            return $"Read from an untrusted place (cloud-synced folder {synced})";
        }

        return _untrusted.Match(path, context) is { } pattern ? $"Read from an untrusted place ({pattern})" : null;
    }

    /// <summary>
    /// The canonical UNC path when <paramref name="path"/> names a share on another host (the own host's admin shares
    /// count as local drives); null otherwise.
    /// </summary>
    private static string? RemoteShare(string path, GuardContext context) =>
        LocalPath(path) is { } local && GuardPaths.Resolve(local, context) is var resolved && WinPath.IsUnc(resolved) && !GuardPaths.IsDevicePath(resolved) ? resolved : null;

    /// <summary>
    /// A network location opened in Pollux: refused when protected, asked otherwise. Judged as written: resolving its
    /// links would already contact the host.
    /// </summary>
    private void CheckNetworkLocation(string share, GuardContext context, Findings findings)
    {
        if (_protected.Match(share, context) is { } pattern)
        {
            findings.Deny($"Protected secret ({pattern})");
        }

        findings.Ask($"{NetworkLocation} ({share})");
    }

    private void CheckSecret(string path, GuardContext context, Findings findings)
    {
        if (LocalPath(path) is not { } local)
        {
            return;
        }

        var resolved = GuardPaths.Resolve(local, context);
        if (GuardPaths.IsDevicePath(resolved))
        {
            findings.Deny($"{DevicePath} ({resolved})");
        }
        else if (_protected.Match(resolved, context) is { } pattern)
        {
            findings.Deny($"Protected secret ({pattern})");
        }
        else if (_folders.Canonical(resolved) is not { } real)
        {
            findings.Ask(CannotResolveLinks);
        }
        else if (_protected.Match(real, context) is { } linked)
        {
            findings.Deny($"Protected secret ({linked})");
        }
    }

    /// <summary>The Working Folder with its links resolved (itself when that fails: paths are then compared as written).</summary>
    private string RealWorkingFolder(GuardContext context) => _folders.Canonical(context.WorkingFolder) ?? context.WorkingFolder;

    /// <summary>
    /// A recursive read of <paramref name="folder"/> (a search or listing below it): blocked when a protected location lies
    /// below it, asked when it is outside the Working Folder.
    /// </summary>
    private void CheckRecursiveRead(string folder, GuardContext context, Findings findings)
    {
        if (LocalPath(folder) is not { } local)
        {
            return;
        }

        var resolved = GuardPaths.Resolve(local, context);
        if (Glob.HasWildcard(WinPath.Name(resolved)))
        {
            resolved = WinPath.Parent(resolved) ?? resolved;
        }

        var real = _folders.Canonical(resolved);
        if ((_protected.Below(resolved, context) ?? (real is null ? null : _protected.Below(real, context))) is { } pattern)
        {
            findings.Deny($"Recursive search over a protected secret ({pattern})");
        }
        else if (real is null)
        {
            findings.Ask(CannotResolveLinks);
        }
        else if (string.IsNullOrWhiteSpace(context.WorkingFolder) || !WinPath.IsSameOrUnder(resolved, context.WorkingFolder)
            || !WinPath.IsSameOrUnder(real, RealWorkingFolder(context)))
        {
            findings.Ask("Recursive search outside the Working Folder");
        }
        else
        {
            CheckFilesBelow(resolved, context, findings);
        }
    }

    /// <summary>
    /// A recursive search of <paramref name="folder"/> (inside the Working Folder) is asked when protected files lie below
    /// it (up to five named, relative to it, plus the count), when there are too many entries to tell, or when a link below
    /// it leads out of the Working Folder; it is blocked when a link below it leads to a protected location.
    /// </summary>
    private void CheckFilesBelow(string folder, GuardContext context, Findings findings)
    {
        var found = new List<string>();
        var entries = 0;
        var started = _clock.GetTimestamp();
        foreach (var entry in _folders.Below(folder))
        {
            if (++entries > MaxFolderEntries || _clock.GetElapsedTime(started) > MaxFolderCheckTime)
            {
                findings.Ask(FolderTooLarge);
                return;
            }

            if (!entry.IsFolder && _protected.MatchName(entry.Path) is not null)
            {
                found.Add(entry.Path);
            }

            if (entry.IsLink)
            {
                CheckLinkBelow(WinPath.Combine(folder, entry.Path), entry.Path, context, findings);
            }
        }

        if (found.Count > 0)
        {
            findings.Ask($"Recursive search over protected files ({found.Count}): {string.Join(", ", found.Take(NamedProtectedFiles))}");
        }
    }

    /// <summary>A link below a searched folder: a search tool that follows links reads its target too.</summary>
    private void CheckLinkBelow(string link, string relative, GuardContext context, Findings findings)
    {
        if (_folders.Canonical(link) is not { } target)
        {
            findings.Ask(CannotResolveLinks);
        }
        else if ((_protected.Match(target, context) ?? _protected.Below(target, context)) is { } pattern)
        {
            findings.Deny($"Recursive search reaches a protected secret through a link ({relative} -> {pattern})");
        }
        else if (!WinPath.IsSameOrUnder(target, RealWorkingFolder(context)))
        {
            findings.Ask($"Recursive search follows a link outside the Working Folder ({relative})");
        }
    }

    /// <summary>The path an argument names: itself, or the local path of a file: URI; null for other URLs and blanks.</summary>
    private static string? LocalPath(string path) =>
        string.IsNullOrWhiteSpace(path) ? null
        : GuardPaths.FromFileUri(path) is { } file ? file
        : path.Contains("://", StringComparison.Ordinal) ? null
        : path;

    /// <summary>
    /// Fail closed: the command's effect cannot be read from its text. A directory change followed by other commands
    /// (relative paths then mean something else), variables, sub-expressions or unknown %VARS% as arguments, backtick
    /// escapes, a variable run as a command, inline interpreter code, in-place editors and git -C.
    /// </summary>
    private static bool IsUnanalysable(string command, IReadOnlyList<ShellStatement> statements, GuardContext context)
    {
        if (command.Contains("$(", StringComparison.Ordinal) || command.Contains('`', StringComparison.Ordinal) || command.Contains("<(", StringComparison.Ordinal))
        {
            return true;
        }

        if (statements.Any(s => HasExpression(s.Text)))
        {
            return true;
        }

        var commands = statements.SelectMany(s => s.Commands).ToList();
        if (commands.Any(c => c.Name == "new-object"))
        {
            return true;
        }

        if (commands.Count > 1 && commands.Any(c => DirectoryChangers.Contains(c.Name)))
        {
            return true;
        }

        foreach (var c in commands)
        {
            if (IsVariable(c.RawName) && !(c.Arguments.Count > 0 && AssignmentOperators.Contains(c.Arguments[0])))
            {
                return true;
            }

            if (!c.RawName.StartsWith('$') && c.Arguments.Any(a => HasUnknownVariable(a, context)))
            {
                return true;
            }

            if (RunsInlineCode(c))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// A PowerShell expression outside quotes builds values the parser cannot see (SECURITY.md S-03, ADR 0007): grouping or
    /// sub-expression parentheses, string concatenation with <c>+</c>, and static .NET calls like <c>[IO.File]::ReadAllText</c>.
    /// Text inside quotes (<c>git commit -m "fix (x)"</c>) is a literal and does not count.
    /// </summary>
    private static bool HasExpression(string statement)
    {
        var text = Unquoted(statement);
        return text.Contains('(', StringComparison.Ordinal) || text.Contains(')', StringComparison.Ordinal)
            || StaticCall().IsMatch(text) || Concatenation().IsMatch(text) || Splat().IsMatch(text);
    }

    /// <summary>The statement with every quoted string replaced by a single <c>'</c> (so a quote's neighbours stay visible).</summary>
    private static string Unquoted(string statement)
    {
        var sb = new StringBuilder(statement.Length);
        var quote = '\0';
        foreach (var ch in statement)
        {
            if (quote != '\0')
            {
                if (ch == quote)
                {
                    quote = '\0';
                }
            }
            else if (ch is '\'' or '"')
            {
                quote = ch;
                sb.Append('\'');
            }
            else
            {
                sb.Append(ch);
            }
        }

        return sb.ToString();
    }

    /// <summary>A static member access on a type literal: <c>[IO.File]::</c>, <c>[System.Environment]::</c>.</summary>
    [GeneratedRegex(@"\[[\w.`]+(\[[\w.,\s]*\])?\]\s*::")]
    private static partial Regex StaticCall();

    /// <summary>
    /// A PowerShell splat (<c>Get-Content @a</c>) or hashtable literal (<c>@{Path='...'}</c>): the arguments come from a
    /// value the parser cannot see. A cmd <c>@echo</c> at the start of a statement and npm scopes (<c>@types/node</c>) do not count.
    /// </summary>
    [GeneratedRegex(@"(?<=[\s=,;|])@(\{|\w+(?=\s|$|[;|]))")]
    private static partial Regex Splat();

    /// <summary>
    /// <c>+</c> joining values: a lone <c>+</c> token, or one next to a string, variable or parenthesis. Flags such as
    /// <c>chmod +x</c> or names like <c>g++</c> do not count.
    /// </summary>
    [GeneratedRegex(@"(^|\s)\+(\s|$)|['$)]\s*\+|\+\s*['$(]")]
    private static partial Regex Concatenation();

    /// <summary>A plain variable such as $cmd (not $_.Name, not $env:X).</summary>
    private static bool IsVariable(string word) =>
        word.Length > 1 && word[0] == '$' && word.Skip(1).All(ch => char.IsLetterOrDigit(ch) || ch == '_') && !SafeVariables.Contains(word);

    /// <summary>A $variable or %VAR% in the argument whose value the Guard Rules do not know.</summary>
    private static bool HasUnknownVariable(string argument, GuardContext context)
    {
        if (SafeVariables.Contains(argument))
        {
            return false;
        }

        var expanded = GuardPaths.ExpandShellVariables(argument, context);
        for (var i = 0; i + 1 < expanded.Length; i++)
        {
            if (expanded[i] == '$' && (char.IsLetter(expanded[i + 1]) || expanded[i + 1] is '_' or '{' or '('))
            {
                return true;
            }
        }

        var start = expanded.IndexOf('%', StringComparison.Ordinal);
        while (start >= 0)
        {
            var end = expanded.IndexOf('%', start + 1);
            if (end < 0)
            {
                break;
            }

            var name = expanded[(start + 1)..end];
            if (name.Length > 0 && (char.IsLetter(name[0]) || name[0] == '_') && name.All(ch => char.IsLetterOrDigit(ch) || ch is '_' or '(' or ')') && context.Environment(name) is null)
            {
                return true;
            }

            start = expanded.IndexOf('%', end + 1);
        }

        return false;
    }

    private static bool RunsInlineCode(SimpleCommand c)
    {
        var args = c.Arguments;
        if (InlineCode.TryGetValue(c.Name, out var switches) && args.Any(a => switches.Contains(a, StringComparer.Ordinal)))
        {
            return true;
        }

        return c.Name switch
        {
            // perl -e / -E code, -i in place (also in clusters like -pi -ne)
            "perl" => args.Any(a => a.Length > 1 && a[0] == '-' && a[1] != '-' && a.Skip(1).TakeWhile(char.IsLetter).Any(ch => ch is 'e' or 'E' or 'i')),
            "sed" or "gsed" => args.Any(a => a.StartsWith("--in-place", StringComparison.Ordinal) || (a.Length > 1 && a[0] == '-' && a[1] != '-' && a.Skip(1).TakeWhile(char.IsLetter).Contains('i'))),
            "awk" or "gawk" => args.Any(a => a == "-i" || a.StartsWith("--include", StringComparison.Ordinal)),
            "git" => args.TakeWhile(a => a.StartsWith('-')).Any(a => a == "-C" || a.StartsWith("--git-dir", StringComparison.Ordinal) || a.StartsWith("--work-tree", StringComparison.Ordinal)),
            _ => false,
        };
    }

    /// <summary>
    /// The folders a command reads recursively (rg, grep -r, findstr /s, Get-ChildItem -Recurse, dir /s, ls -R, find);
    /// the Working Folder (".") when none is given.
    /// </summary>
    private static IEnumerable<string> RecursiveReadFolders(SimpleCommand c)
    {
        var args = c.Arguments;
        var dashed = args.Where(a => a.StartsWith('-')).ToList();
        var plain = args.Where(a => !a.StartsWith('-')).ToList();
        IEnumerable<string>? folders = c.Name switch
        {
            "rg" or "ag" => SkipPattern(plain, dashed.Any(a => a is "-e" or "-f" || a.StartsWith("--regexp", StringComparison.Ordinal) || a.StartsWith("--file", StringComparison.Ordinal))),
            "grep" or "egrep" or "fgrep" when dashed.Any(a => a.StartsWith("--recursive", StringComparison.Ordinal) || a.StartsWith("--dereference-recursive", StringComparison.Ordinal) || (!a.StartsWith("--", StringComparison.Ordinal) && (a.Contains('r', StringComparison.Ordinal) || a.Contains('R', StringComparison.Ordinal))))
                => SkipPattern(plain, dashed.Any(a => a is "-e" or "-f")),
            "findstr" when args.Any(a => a.Equals("/s", StringComparison.OrdinalIgnoreCase))
                => SkipPattern([.. args.Where(a => !a.StartsWith('/'))], args.Any(a => a.StartsWith("/c:", StringComparison.OrdinalIgnoreCase) || a.StartsWith("/g:", StringComparison.OrdinalIgnoreCase))),
            "dir" when args.Any(a => a.Equals("/s", StringComparison.OrdinalIgnoreCase)) => args.Where(a => !a.StartsWith('/')),
            "get-childitem" or "gci" or "dir" or "ls" when args.Any(a => ShellParser.IsParameter(a, "-recurse", 2) && !a.EndsWith(":$false", StringComparison.OrdinalIgnoreCase)) => ChildItemPaths(args),
            "ls" when dashed.Any(a => !a.StartsWith("--", StringComparison.Ordinal) && a.Contains('R', StringComparison.Ordinal)) || dashed.Contains("--recursive") => plain,
            "find" => args.TakeWhile(a => !a.StartsWith('-') && a is not ("(" or "!")),
            "tree" => args.Where(a => !a.StartsWith('/')),
            _ => null,
        };

        if (folders is null)
        {
            yield break;
        }

        var any = false;
        foreach (var folder in folders)
        {
            any = true;
            yield return folder;
        }

        if (!any)
        {
            yield return ".";
        }
    }

    private static IEnumerable<string> SkipPattern(List<string> plain, bool patternGivenAsOption) => patternGivenAsOption ? plain : plain.Skip(1);

    /// <summary>Get-ChildItem's folders: -Path / -LiteralPath values and positional arguments (values of other parameters are included too: over-inclusive).</summary>
    private static IEnumerable<string> ChildItemPaths(IReadOnlyList<string> args)
    {
        for (var i = 0; i < args.Count; i++)
        {
            var a = args[i];
            if (!a.StartsWith('-'))
            {
                yield return a;
            }
            else if (a.Contains(':', StringComparison.Ordinal) && (ShellParser.IsParameter(a, "-path", 2) || ShellParser.IsParameter(a, "-literalpath", 2)))
            {
                yield return a.Split(':', 2)[1];
            }
        }
    }

    /// <summary>A write is asked outside the Working Folder, judged both as written and by where its links really point.</summary>
    private void CheckWorkingFolder(string path, GuardContext context, Findings findings)
    {
        if (string.IsNullOrWhiteSpace(context.WorkingFolder))
        {
            findings.Ask("Write without a Working Folder");
            return;
        }

        var resolved = GuardPaths.Resolve(path, context);
        if (!WinPath.IsSameOrUnder(resolved, context.WorkingFolder))
        {
            findings.Ask("Writes outside the Working Folder");
        }
        else if (_folders.Canonical(resolved) is not { } real)
        {
            findings.Ask(CannotResolveLinks);
        }
        else if (!WinPath.IsSameOrUnder(real, RealWorkingFolder(context)))
        {
            findings.Ask("Writes outside the Working Folder");
        }
    }

    /// <summary>Paths a shell command may change: arguments of file-changing commands and redirection targets.</summary>
    private static IEnumerable<string> WriteTargets(SimpleCommand c)
    {
        var args = c.Arguments;
        for (var i = 0; i < args.Count; i++)
        {
            var a = args[i];
            var redirect = a.TrimStart('0', '1', '2', '*');
            if (redirect.StartsWith('>'))
            {
                var target = redirect.TrimStart('>');
                if (target.Length == 0 && i + 1 < args.Count)
                {
                    target = args[++i];
                }

                if (!target.StartsWith('&') && !IsNullDevice(target))
                {
                    yield return target;
                }
            }
        }

        if (AlwaysAskRules.GitConfigFile(c) is { } configFile)
        {
            yield return configFile;
        }

        if (!WriteCommands.Contains(c.Name))
        {
            yield break;
        }

        foreach (var a in PathArguments(c))
        {
            if (!IsSwitch(c, a) && !a.StartsWith('>') && !IsNullDevice(a) && !AlwaysAskRules.ContainsUrl(a))
            {
                yield return a;
            }
        }
    }

    /// <summary>
    /// A pipeline into a writing or deleting command (<c>Get-ChildItem D:\x | Remove-Item</c>) changes the items the commands
    /// before it name: their path arguments count as write targets (SECURITY.md S-11, ADR 0007).
    /// </summary>
    private static IEnumerable<string> PipedWriteTargets(ShellStatement statement)
    {
        if (!statement.Connected)
        {
            yield break;
        }

        var commands = statement.Commands;
        var lastWriter = -1;
        for (var i = 0; i < commands.Count; i++)
        {
            if (WriteCommands.Contains(commands[i].Name))
            {
                lastWriter = i;
            }
        }

        for (var i = 0; i < lastWriter; i++)
        {
            foreach (var a in PathArguments(commands[i]))
            {
                if (!IsSwitch(commands[i], a) && !IsNullDevice(a) && !AlwaysAskRules.ContainsUrl(a))
                {
                    yield return a;
                }
            }
        }
    }

    /// <summary>
    /// Every token a secret could hide in (SECURITY.md S-03): the command word, each path argument, and the value glued to a
    /// short option (<c>-iC:\Users\me\.ssh\id_rsa</c>).
    /// </summary>
    private static IEnumerable<string> SecretCandidates(SimpleCommand c)
    {
        yield return c.RawName;
        foreach (var path in PathArguments(c))
        {
            yield return path;
        }

        foreach (var a in c.Arguments)
        {
            if (a.Length > 2 && a[0] == '-' && a[1] != '-' && char.IsLetter(a[1]))
            {
                yield return a[2..];
            }
        }
    }

    /// <summary>
    /// The values inside a statement's assignments and hashtable literals (<c>$a=@{Path='~\.ssh\id_rsa'}</c>): they name
    /// paths a later splat (<c>Get-Content @a</c>) reads, so secrets are checked on them too (SECURITY.md S-03).
    /// </summary>
    private static IEnumerable<string> AssignedValues(ShellStatement statement)
    {
        foreach (var word in ShellParser.Tokenize(statement.Text))
        {
            if (word.AsSpan().IndexOfAny(AssignmentSeparatorValues) < 0)
            {
                continue;
            }

            foreach (var part in word.Split(AssignmentSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                yield return part;
            }
        }
    }

    private static readonly char[] AssignmentSeparators = ['=', '{', '}', ';', '\'', '"', '@'];

    private static readonly System.Buffers.SearchValues<char> AssignmentSeparatorValues = System.Buffers.SearchValues.Create(AssignmentSeparators);

    private static bool IsNullDevice(string target) =>
        target.Equals("$null", StringComparison.OrdinalIgnoreCase) || target.Equals("nul", StringComparison.OrdinalIgnoreCase)
        || target.Equals("/dev/null", StringComparison.Ordinal);

    /// <summary>
    /// cmd-style switches like /s, /Y, /MIR of cmd's own commands: a slash and one or two letters, or upper-case letters.
    /// Anything else (/tmp, /Users, any /x of a POSIX command) stays a path: when in doubt, a path.
    /// </summary>
    private static bool IsSwitch(SimpleCommand c, string a) =>
        CmdStyleCommands.Contains(c.Name) && a.Length is > 1 and <= 9 && a[0] == '/' && a.Skip(1).All(char.IsLetter)
        && (a.Length <= 3 || a.Skip(1).All(char.IsUpper));

    /// <summary>
    /// Every argument that could name a file: plain arguments, values of -Name:value / --name=value, redirection targets.
    /// Deliberately over-inclusive; a false hit only means the user is asked or a secret stays protected.
    /// </summary>
    private static IEnumerable<string> PathArguments(SimpleCommand c)
    {
        foreach (var arg in c.Arguments)
        {
            var a = arg.TrimStart('0', '1', '2', '*', '<', '>', '&');
            if (a.StartsWith('-'))
            {
                var sep = a.IndexOfAny([':', '=']);
                if (sep < 0)
                {
                    continue;
                }

                a = a[(sep + 1)..];
            }

            if (a.Length > 0 && !a.StartsWith('$') || a.StartsWith("$env:", StringComparison.OrdinalIgnoreCase) || a.StartsWith("${", StringComparison.Ordinal) || a.StartsWith("$HOME", StringComparison.OrdinalIgnoreCase))
            {
                yield return a;
            }
        }
    }

    private static bool IsElevation(SimpleCommand c)
    {
        if (Elevators.Contains(c.Name))
        {
            return true;
        }

        var args = c.Arguments;
        for (var i = 0; i < args.Count; i++)
        {
            if (!ShellParser.IsParameter(args[i], "-verb", 2))
            {
                continue;
            }

            var inline = args[i].Split(':', 2);
            var verb = inline.Length == 2 ? inline[1] : i + 1 < args.Count ? args[i + 1] : "";
            if (verb.Equals("runas", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Something downloaded is handed to an interpreter in the same statement: piped into it (<c>curl … | sh</c>,
    /// <c>iwr … | iex</c>), passed as a sub-expression (<c>iex (iwr …)</c>, <c>bash &lt;(curl …)</c>) or run as the command
    /// itself (<c>$(curl …)</c>).
    /// </summary>
    private static bool IsDownloadAndExecute(ShellStatement statement)
    {
        var downloads = statement.Commands.Any(IsDownloader) || WebClientMarkers.Any(m => statement.Text.Contains(m, StringComparison.OrdinalIgnoreCase));
        if (!downloads)
        {
            return false;
        }

        var substitutedCommand = statement.Text.StartsWith("$(", StringComparison.Ordinal) || statement.Text.StartsWith('`');
        return substitutedCommand || (statement.Connected && statement.Commands.Any(c => Interpreters.Contains(c.Name)));
    }

    private static bool IsDownloader(SimpleCommand c) =>
        Downloaders.Contains(c.Name) || (c.Name == "certutil" && c.Arguments.Any(a => a.Contains("urlcache", StringComparison.OrdinalIgnoreCase)));

    /// <summary>A settings entry "name arg…" matches when the command name (alias-resolved) is equal and every arg is present.</summary>
    private static bool Matches(SimpleCommand command, string entry)
    {
        var tokens = ShellParser.Tokenize(entry);
        return tokens.Count > 0
            && ShellParser.Canonical(tokens[0]) == command.Name
            && tokens.Skip(1).All(command.HasArgument);
    }

    private sealed class Findings
    {
        private readonly List<string> _deny = [];
        private readonly List<string> _ask = [];

        public void Deny(string reason) => Add(_deny, reason);

        public void Ask(string reason) => Add(_ask, reason);

        private static void Add(List<string> list, string reason)
        {
            if (!list.Contains(reason))
            {
                list.Add(reason);
            }
        }

        public GuardVerdict Verdict() =>
            _deny.Count > 0 ? GuardVerdict.Of(GuardDecision.Deny, _deny)
            : _ask.Count > 0 ? GuardVerdict.Of(GuardDecision.Ask, _ask)
            : GuardVerdict.Allowed;
    }
}
