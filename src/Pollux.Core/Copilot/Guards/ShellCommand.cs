using System.Text;

namespace Pollux.Core.Copilot.Guards;

/// <summary>One simple command inside a command line: the command word (alias-resolved, lower case) and its arguments.</summary>
public sealed record SimpleCommand(string Name, IReadOnlyList<string> Arguments, string RawName)
{
    public bool HasArgument(string argument) =>
        Arguments.Any(a => a.Equals(argument, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// A statement: what runs between sequential separators (<c>;</c>, newline, <c>&amp;&amp;</c>, <c>||</c>, <c>&amp;</c>).
/// <see cref="Connected"/> is true when its commands are joined by pipes or sub-expressions (<c>|</c>, <c>$( )</c>, <c>( )</c>, <c>&lt;( )</c>),
/// i.e. one command's output can feed another.
/// </summary>
public sealed record ShellStatement(string Text, IReadOnlyList<SimpleCommand> Commands, bool Connected);

/// <summary>
/// Shell-agnostic, conservative parsing of PowerShell, cmd and POSIX command lines. It does not execute or fully
/// understand any shell; it splits chained commands, resolves common aliases and unwraps nested shells
/// (<c>cmd /c</c>, <c>powershell -Command</c>, <c>-EncodedCommand</c>, <c>bash -c</c>, <c>Invoke-Expression "…"</c>).
/// </summary>
public static class ShellParser
{
    private const int MaxDepth = 4;

    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        // Remove-Item
        ["rm"] = "rm", ["del"] = "del", ["erase"] = "del", ["rd"] = "rd", ["rmdir"] = "rd", ["ri"] = "remove-item",
        // Web
        ["iwr"] = "invoke-webrequest", ["irm"] = "invoke-restmethod", ["wget"] = "wget", ["curl"] = "curl",
        // Expression / process
        ["iex"] = "invoke-expression", ["saps"] = "start-process", ["start"] = "start-process",
        // Items
        ["ni"] = "new-item", ["si"] = "set-item", ["sp"] = "set-itemproperty", ["rp"] = "remove-itemproperty",
        ["cpi"] = "copy-item", ["copy"] = "copy", ["cp"] = "cp", ["mi"] = "move-item", ["move"] = "move", ["mv"] = "mv",
        ["ren"] = "rename-item", ["rni"] = "rename-item", ["md"] = "mkdir",
    };

    private static readonly HashSet<string> Prefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "env", "nohup", "time", "command", "exec", "call", "builtin", "nice",
    };

    private static readonly HashSet<string> NestedShells = new(StringComparer.OrdinalIgnoreCase)
    {
        "cmd", "powershell", "pwsh", "bash", "sh", "zsh", "dash", "ksh", "fish", "wsl",
    };

    /// <summary>Splits a command line into statements, unwrapping nested shells into further statements.</summary>
    public static IReadOnlyList<ShellStatement> Parse(string commandLine) => Parse(commandLine, out _);

    /// <summary>
    /// Splits a command line into statements, unwrapping nested shells into further statements.
    /// <paramref name="incomplete"/> is true when something could not be unwrapped: nesting deeper than the parser
    /// follows, or an encoded command that does not decode. The statements then do not show everything that would run.
    /// </summary>
    public static IReadOnlyList<ShellStatement> Parse(string commandLine, out bool incomplete)
    {
        var result = new List<ShellStatement>();
        incomplete = false;
        Parse(commandLine, result, 0, ref incomplete);
        return result;
    }

    private static void Parse(string commandLine, List<ShellStatement> result, int depth, ref bool incomplete)
    {
        foreach (var statementText in SplitStatements(commandLine))
        {
            var connected = false;
            var commands = new List<SimpleCommand>();
            foreach (var part in SplitCommands(statementText, ref connected))
            {
                var tokens = Tokenize(part);
                var command = ToCommand(tokens);
                if (command is null)
                {
                    continue;
                }

                commands.Add(command);
                if (Nested(command, ref incomplete) is { Length: > 0 } nested)
                {
                    if (depth < MaxDepth)
                    {
                        Parse(nested, result, depth + 1, ref incomplete);
                    }
                    else
                    {
                        incomplete = true;
                    }
                }
            }

            if (commands.Count > 0)
            {
                result.Add(new ShellStatement(statementText.Trim(), commands, connected));
            }
        }
    }

    /// <summary>The command line run by a nested shell or expression evaluator, if any.</summary>
    private static string? Nested(SimpleCommand command, ref bool incomplete)
    {
        var args = command.Arguments;
        if (command.Name == "invoke-expression")
        {
            return string.Join(' ', args.Where(a => !a.StartsWith('-')));
        }

        if (command.Name == "start-process")
        {
            return StartedCommand(args);
        }

        if (!NestedShells.Contains(command.Name))
        {
            return null;
        }

        if (command.Name == "wsl")
        {
            var start = args.ToList().FindIndex(a => a is "-e" or "--exec" or "--");
            return string.Join(' ', start >= 0 ? args.Skip(start + 1) : args.SkipWhile(a => a.StartsWith('-')));
        }

        for (var i = 0; i < args.Count; i++)
        {
            var a = args[i];
            if (command.Name == "cmd" && (a.Equals("/c", StringComparison.OrdinalIgnoreCase) || a.Equals("/k", StringComparison.OrdinalIgnoreCase)))
            {
                return string.Join(' ', args.Skip(i + 1));
            }

            if (command.Name is "powershell" or "pwsh")
            {
                if (IsParameter(a, "-encodedcommand", 2) || a.Equals("-ec", StringComparison.OrdinalIgnoreCase))
                {
                    var decoded = i + 1 < args.Count ? DecodePowerShell(args[i + 1]) : null;
                    incomplete |= decoded is null;
                    return decoded;
                }

                if (IsParameter(a, "-command", 2) || a == "-c")
                {
                    return string.Join(' ', args.Skip(i + 1));
                }

                continue;
            }

            if (a == "-c" || (a.StartsWith('-') && !a.StartsWith("--", StringComparison.Ordinal) && a.Contains('c', StringComparison.Ordinal) && a.Length <= 4))
            {
                return string.Join(' ', args.Skip(i + 1));
            }
        }

        return null;
    }

    /// <summary>The program and arguments of <c>Start-Process</c> / cmd's <c>start</c>.</summary>
    private static string StartedCommand(IReadOnlyList<string> args)
    {
        var parts = new List<string>();
        for (var i = 0; i < args.Count; i++)
        {
            var a = args[i];
            if (a.Length == 0 || IsCmdSwitch(a))
            {
                continue;
            }

            if (!a.StartsWith('-'))
            {
                parts.Add(a);
                continue;
            }

            if (a.Contains(':', StringComparison.Ordinal) || i + 1 >= args.Count)
            {
                continue;
            }

            if (IsParameter(a, "-filepath", 2) || IsParameter(a, "-argumentlist", 2))
            {
                parts.Add(args[++i]);
            }
            else if (IsParameter(a, "-verb", 2) || IsParameter(a, "-windowstyle", 3) || IsParameter(a, "-workingdirectory", 3)
                || IsParameter(a, "-credential", 2) || a.StartsWith("-RedirectStandard", StringComparison.OrdinalIgnoreCase))
            {
                i++;
            }
        }

        return string.Join(' ', parts);
    }

    private static string? DecodePowerShell(string base64)
    {
        try
        {
            return Encoding.Unicode.GetString(Convert.FromBase64String(base64));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static bool IsCmdSwitch(string a) =>
        a.Length <= 9 && a.StartsWith('/') && a.Skip(1).All(char.IsLetter);

    /// <summary>True when <paramref name="arg"/> is a (possibly abbreviated) PowerShell parameter name, e.g. -Rec for -Recurse.</summary>
    public static bool IsParameter(string arg, string fullName, int minLength)
    {
        var name = arg.Split(':', 2)[0];
        return name.Length >= minLength && name.StartsWith('-') && !name.StartsWith("--", StringComparison.Ordinal)
            && fullName.StartsWith(name, StringComparison.OrdinalIgnoreCase);
    }

    private static SimpleCommand? ToCommand(List<string> tokens)
    {
        var i = 0;
        while (i < tokens.Count && (tokens[i].Length == 0 || IsAssignment(tokens[i]) || Prefixes.Contains(tokens[i])))
        {
            i++;
        }

        if (i >= tokens.Count)
        {
            // A bare "env" (with only assignments) lists the environment: it is a command of its own.
            return tokens.Any(t => t.Equals("env", StringComparison.OrdinalIgnoreCase)) ? new SimpleCommand("env", [], "env") : null;
        }

        var raw = tokens[i];
        return new SimpleCommand(Canonical(raw), tokens.Skip(i + 1).ToList(), raw);
    }

    private static bool IsAssignment(string token)
    {
        var eq = token.IndexOf('=', StringComparison.Ordinal);
        return eq > 0 && !token.StartsWith('-') && !token.StartsWith('$') && token[..eq].All(c => char.IsLetterOrDigit(c) || c == '_');
    }

    /// <summary>Lower-case command name without directory and executable extension, aliases resolved.</summary>
    public static string Canonical(string word)
    {
        var w = word;
        var slash = w.LastIndexOfAny(['\\', '/']);
        if (slash >= 0)
        {
            w = w[(slash + 1)..];
        }

        foreach (var ext in new[] { ".exe", ".com", ".cmd", ".bat" })
        {
            if (w.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
            {
                w = w[..^ext.Length];
                break;
            }
        }

        w = w.ToLowerInvariant();
        return Aliases.TryGetValue(w, out var canonical) ? canonical : w;
    }

    private static IEnumerable<string> SplitStatements(string text)
    {
        var sb = new StringBuilder();
        char quote = '\0';
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quote != '\0')
            {
                sb.Append(c);
                if (c == quote)
                {
                    quote = '\0';
                }

                continue;
            }

            if (c is '\'' or '"')
            {
                quote = c;
                sb.Append(c);
                continue;
            }

            var isSeparator = c is ';' or '\n' or '\r'
                || (c == '&' && !(i > 0 && text[i - 1] is '>' or '<') && !(i + 1 < text.Length && text[i + 1] == '>'))
                || (c == '|' && i + 1 < text.Length && text[i + 1] == '|');
            if (isSeparator)
            {
                if (c is '&' or '|' && i + 1 < text.Length && text[i + 1] == c)
                {
                    i++;
                }

                yield return sb.ToString();
                sb.Clear();
                continue;
            }

            sb.Append(c);
        }

        yield return sb.ToString();
    }

    private static List<string> SplitCommands(string statement, ref bool connected)
    {
        var parts = new List<string>();
        var sb = new StringBuilder();
        char quote = '\0';
        for (var i = 0; i < statement.Length; i++)
        {
            var c = statement[i];
            if (quote != '\0')
            {
                sb.Append(c);
                if (c == quote)
                {
                    quote = '\0';
                }

                continue;
            }

            if (c is '\'' or '"')
            {
                quote = c;
                sb.Append(c);
                continue;
            }

            // ${env:NAME} / ${HOME} is a variable, not a script block.
            if (c == '$' && i + 1 < statement.Length && statement[i + 1] == '{' && statement.IndexOf('}', i) is var close and > 0)
            {
                sb.Append(statement, i, close - i + 1);
                i = close;
                continue;
            }

            if (c is '|' or '(' or ')' or '{' or '}' or '`')
            {
                connected |= c is '|' or '(' or '`';
                if (c == '(' && sb.Length > 0 && sb[^1] is '$' or '<' or '@')
                {
                    sb.Length--;
                }

                parts.Add(sb.ToString());
                sb.Clear();
                continue;
            }

            sb.Append(c);
        }

        parts.Add(sb.ToString());
        return parts;
    }

    /// <summary>Splits on whitespace outside quotes, removes the quotes and cmd's ^ escapes.</summary>
    public static List<string> Tokenize(string text)
    {
        var tokens = new List<string>();
        var sb = new StringBuilder();
        var inToken = false;
        char quote = '\0';
        foreach (var c in text)
        {
            if (quote != '\0')
            {
                if (c == quote)
                {
                    quote = '\0';
                }
                else
                {
                    sb.Append(c);
                }

                continue;
            }

            if (c is '\'' or '"')
            {
                quote = c;
                inToken = true;
                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                if (inToken)
                {
                    tokens.Add(sb.ToString());
                    sb.Clear();
                    inToken = false;
                }

                continue;
            }

            if (c == '^')
            {
                continue;
            }

            sb.Append(c);
            inToken = true;
        }

        if (inToken)
        {
            tokens.Add(sb.ToString());
        }

        return tokens;
    }
}
