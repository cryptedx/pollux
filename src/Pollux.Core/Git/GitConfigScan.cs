using System.Text;

namespace Pollux.Core.Git;

/// <summary>
/// Scans a repository's own git config for keys that make git run commands or read further files (SECURITY.md S-01,
/// ADR 0006): any <c>filter.*</c>, <c>diff.*.command</c>/<c>textconv</c>, <c>merge.*.driver</c>, <c>include.*</c>,
/// <c>includeIf.*</c> and the command settings <c>core.fsmonitor</c>, <c>core.sshCommand</c>, <c>core.pager</c>,
/// <c>core.editor</c>, <c>core.hooksPath</c>, <c>core.attributesFile</c>. The tokenizer follows git's own config
/// parser (sections, subsections, legacy <c>[a.b]</c> headers, quotes, escapes, comments, line continuations), so a
/// key git reads cannot hide from the scan. Text git would reject counts as refused (fail closed). This is the second
/// line of defence; trust is the first.
/// </summary>
public static class GitConfigScan
{
    /// <summary>Reported instead of a key when the config cannot be parsed or read.</summary>
    public const string Unreadable = "(unreadable config)";

    private static readonly HashSet<string> CoreCommandKeys = new(StringComparer.Ordinal)
    {
        "fsmonitor", "sshcommand", "pager", "editor", "hookspath", "attributesfile",
    };

    /// <summary>The first refused key in <paramref name="config"/> (as "section.subsection.key", section and key lowercase), or null when it is clean.</summary>
    public static string? FindRefusedKey(string config)
    {
        string? refused = null;
        var ok = Parse(config, (key, _) =>
        {
            if (refused is null && IsRefused(key)) refused = key;
        });
        return ok ? refused : refused ?? Unreadable;
    }

    /// <summary>The last <c>extensions.objectFormat</c> value in <paramref name="config"/>, lower case; null when unset or unreadable.</summary>
    public static string? ObjectFormat(string config)
    {
        string? format = null;
        var ok = Parse(config, (key, value) =>
        {
            if (key == "extensions.objectformat") format = value.ToLowerInvariant();
        });
        return ok ? format : null;
    }

    /// <summary>The short path-bar hint shown instead of status letters when the scan refused a repository.</summary>
    public const string ShortHint = "no git status: repo config";

    /// <summary>The longer explanation (tooltip) for a refused <paramref name="key"/>.</summary>
    public static string Hint(string key) => key == Unreadable
        ? "git status is skipped: this repository's own config could not be read completely, so Pollux cannot rule out that it makes git run commands."
        : $"git status is skipped: this repository's own config sets {key}, which can make git run commands or read other files. Remove the setting to get status letters back.";

    /// <summary>Whether a full key name ("section.subsection.key" or "section.key") is one the scan refuses.</summary>
    public static bool IsRefused(string fullKey)
    {
        var firstDot = fullKey.IndexOf('.');
        var lastDot = fullKey.LastIndexOf('.');
        if (firstDot < 0) return true;
        var section = fullKey[..firstDot].ToLowerInvariant();
        var key = fullKey[(lastDot + 1)..].ToLowerInvariant();
        var hasSubsection = lastDot > firstDot;
        return section switch
        {
            "filter" or "include" or "includeif" => true,
            "diff" => hasSubsection && key is "command" or "textconv",
            "merge" => hasSubsection && key == "driver",
            "core" => !hasSubsection && CoreCommandKeys.Contains(key),
            _ => false,
        };
    }

    /// <summary>Calls <paramref name="onKey"/> with every key git would read and its value; false when git would reject the text.</summary>
    private static bool Parse(string text, Action<string, string> onKey)
    {
        var reader = new Reader(text);
        var section = new StringBuilder();
        var hasSection = false;
        var comment = false;
        for (;;)
        {
            var c = reader.Next();
            if (c == '\n')
            {
                if (reader.Eof) return true;
                comment = false;
                continue;
            }

            if (comment || IsSpace(c)) continue;
            if (c is '#' or ';')
            {
                comment = true;
                continue;
            }

            if (c == '[')
            {
                section.Clear();
                if (!ReadSectionHeader(reader, section)) return false;
                hasSection = true;
                continue;
            }

            if (!char.IsAsciiLetter(c) || !hasSection) return false;
            var name = new StringBuilder().Append(char.ToLowerInvariant(c));
            var value = new StringBuilder();
            if (!ReadKeyAndValue(reader, name, value)) return false;
            onKey(section + "." + name, value.ToString());
        }
    }

    /// <summary>git's get_base_var/get_extended_base_var: "[name]", "[name.sub]" or "[name "sub"]".</summary>
    private static bool ReadSectionHeader(Reader reader, StringBuilder section)
    {
        for (;;)
        {
            var c = reader.Next();
            if (reader.Eof) return false;
            if (c == ']') return section.Length > 0;
            if (IsSpace(c)) return ReadQuotedSubsection(reader, section);
            if (!IsKeyChar(c) && c != '.') return false;
            section.Append(char.ToLowerInvariant(c));
        }
    }

    private static bool ReadQuotedSubsection(Reader reader, StringBuilder section)
    {
        char c;
        do
        {
            c = reader.Next();
            if (c == '\n') return false;
        }
        while (IsSpace(c));

        if (c != '"') return false;
        section.Append('.');
        for (;;)
        {
            c = reader.Next();
            if (c == '\n') return false;
            if (c == '"') break;
            if (c == '\\')
            {
                c = reader.Next();
                if (c == '\n') return false;
            }

            section.Append(c);
        }

        return reader.Next() == ']';
    }

    /// <summary>git's get_value/parse_value: the rest of the key name, then an optional "= value" (unquoted blanks dropped).</summary>
    private static bool ReadKeyAndValue(Reader reader, StringBuilder name, StringBuilder value)
    {
        char c;
        for (;;)
        {
            c = reader.Next();
            if (reader.Eof || !IsKeyChar(c)) break;
            name.Append(char.ToLowerInvariant(c));
        }

        while (c is ' ' or '\t') c = reader.Next();
        if (c == '\n') return true;
        if (c != '=') return false;

        var quote = false;
        var comment = false;
        for (;;)
        {
            c = reader.Next();
            if (c == '\n') return !quote;
            if (comment) continue;
            if (IsSpace(c) && !quote) continue;
            if (!quote && c is ';' or '#')
            {
                comment = true;
                continue;
            }

            if (c == '\\')
            {
                c = reader.Next();
                if (c == '\n') continue; // line continuation
                if (c is not ('t' or 'b' or 'n' or '\\' or '"')) return false;
                value.Append(c switch { 't' => '\t', 'b' => '\b', 'n' => '\n', _ => c });
                continue;
            }

            if (c == '"') quote = !quote;
            else value.Append(c);
        }
    }

    private static bool IsKeyChar(char c) => char.IsAsciiLetterOrDigit(c) || c == '-';

    // git's isspace: space, \t, \n, \v, \f, \r.
    private static bool IsSpace(char c) => c is ' ' or '\t' or '\n' or '\v' or '\f' or '\r';

    /// <summary>git's get_next_char: "\r\n" reads as "\n", the end of the text reads as "\n" with <see cref="Eof"/> set.</summary>
    private sealed class Reader(string text)
    {
        private int _pos = text.Length > 0 && text[0] == '﻿' ? 1 : 0;

        public bool Eof { get; private set; }

        public char Next()
        {
            if (_pos >= text.Length)
            {
                Eof = true;
                return '\n';
            }

            var c = text[_pos++];
            if (c == '\r' && _pos < text.Length && text[_pos] == '\n') c = text[_pos++];
            return c;
        }
    }
}
