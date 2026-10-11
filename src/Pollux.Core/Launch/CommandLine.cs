using System.Text;

namespace Pollux.Core.Launch;

/// <summary>Quoting for Windows command lines: the argument rules programs use, and cmd.exe's own rules on top.</summary>
public static class CommandLine
{
    /// <summary>
    /// One argument, always in double quotes, as CommandLineToArgvW reads it back: backslashes before a quote (and
    /// before the closing quote) are doubled, quotes are escaped.
    /// </summary>
    public static string Quote(string argument)
    {
        var sb = new StringBuilder(argument.Length + 2).Append('"');
        AppendQuotedBody(sb, argument);
        return sb.Append('"').ToString();
    }

    /// <summary>
    /// An argument on a line cmd.exe parses (<c>cmd /c</c> or its prompt) for a program that reads it with
    /// <see cref="Quote"/>'s rules. cmd expands <c>%NAME%</c> even inside quotes, so every '%' is moved outside the
    /// quotes and escaped as <c>^%</c>: cmd then sees no variable name and drops the caret (S-20). <c>&amp; | &lt; &gt; ^</c>
    /// stay inside quotes, where cmd leaves them alone.
    /// </summary>
    public static string QuoteForCmd(string argument) => QuoteForCmd(argument, programRules: true);

    /// <summary>An argument of a cmd.exe builtin (mklink, cd): like <see cref="QuoteForCmd"/>, but the text inside quotes is taken literally.</summary>
    public static string QuoteForCmdBuiltin(string argument) => QuoteForCmd(argument, programRules: false);

    /// <summary>The embedded cmd terminal's "cd" into <paramref name="folder"/>, with '%' kept literal (S-20).</summary>
    public static string CmdChangeDirectory(string folder) => "cd /d " + QuoteForCmd(folder, programRules: false);

    private static string QuoteForCmd(string argument, bool programRules)
    {
        var sb = new StringBuilder(argument.Length + 8);
        var parts = argument.Split('%');
        for (var i = 0; i < parts.Length; i++)
        {
            if (i > 0) sb.Append("^%");
            sb.Append('"');
            if (programRules) AppendQuotedBody(sb, parts[i]);
            else sb.Append(parts[i]);
            sb.Append('"');
        }

        return sb.ToString();
    }

    private static void AppendQuotedBody(StringBuilder sb, string text)
    {
        var backslashes = 0;
        foreach (var c in text)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }

            if (c == '"')
            {
                sb.Append('\\', backslashes * 2 + 1).Append('"');
            }
            else
            {
                sb.Append('\\', backslashes).Append(c);
            }

            backslashes = 0;
        }

        sb.Append('\\', backslashes * 2);
    }
}
