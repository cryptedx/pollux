using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Pollux.Core.Files;

public enum RenameCase { Keep, Lower, Upper, Title }

/// <summary>
/// What the Batch Rename dialog collects. <see cref="Template"/> such as "{name}_{n:000}{ext}" replaces the whole name
/// after find/replace and case change; empty means no template. Find/replace works on the full name, case change on the stem only.
/// </summary>
public sealed record BatchRenameOptions(
    string Find = "",
    string Replace = "",
    bool UseRegex = false,
    bool MatchCase = false,
    RenameCase Case = RenameCase.Keep,
    string Template = "",
    int Start = 1,
    int Step = 1);

public sealed record RenameSource(string Name, bool IsDirectory);

/// <summary>One row of the preview. <see cref="Error"/> blocks applying the plan.</summary>
public sealed record RenamePreviewItem(string OldName, string NewName, string? Error)
{
    public bool IsChanged => !string.Equals(OldName, NewName, StringComparison.Ordinal);
}

/// <summary>One rename to run, in order. Names are relative to the folder, so a caller only has to join them.</summary>
public sealed record RenameStep(string From, string To);

public sealed class RenamePlan
{
    internal RenamePlan(IReadOnlyList<RenamePreviewItem> items, string? optionsError)
    {
        Items = items;
        OptionsError = optionsError;
    }

    public IReadOnlyList<RenamePreviewItem> Items { get; }

    /// <summary>Set when the find pattern or template itself is unusable; every item is then unchanged.</summary>
    public string? OptionsError { get; }

    public bool HasConflicts => OptionsError is not null || Items.Any(i => i.Error is not null);

    public bool HasChanges => Items.Any(i => i.IsChanged);

    public bool CanApply => !HasConflicts && HasChanges;

    /// <summary>
    /// The renames in a safe order. A name that another item is vacating waits for it; swaps, cycles and case-only
    /// changes go through a temporary name. <paramref name="exists"/> is only used to pick a temporary name that is free.
    /// </summary>
    public IReadOnlyList<RenameStep> Steps(Func<string, bool> exists)
    {
        ArgumentNullException.ThrowIfNull(exists);
        if (!CanApply)
        {
            return [];
        }

        var taken = new HashSet<string>(Items.SelectMany(i => new[] { i.OldName, i.NewName }), StringComparer.OrdinalIgnoreCase);
        var pending = Items.Where(i => i.IsChanged).Select(i => (From: i.OldName, To: i.NewName)).ToList();
        var steps = new List<RenameStep>();
        var tempCounter = 0;

        while (pending.Count > 0)
        {
            var free = -1;
            for (var i = 0; i < pending.Count && free < 0; i++)
            {
                var p = pending[i];
                var blocked = string.Equals(p.From, p.To, StringComparison.OrdinalIgnoreCase);
                for (var j = 0; j < pending.Count && !blocked; j++)
                {
                    blocked = j != i && string.Equals(pending[j].From, p.To, StringComparison.OrdinalIgnoreCase);
                }

                if (!blocked)
                {
                    free = i;
                }
            }

            if (free >= 0)
            {
                steps.Add(new(pending[free].From, pending[free].To));
                pending.RemoveAt(free);
                continue;
            }

            // Only cycles (and case-only changes) are left: park one item under a temporary name.
            string temp;
            do
            {
                temp = $".pollux-rename-{++tempCounter}.tmp";
            }
            while (taken.Contains(temp) || exists(temp));
            taken.Add(temp);
            steps.Add(new(pending[0].From, temp));
            pending[0] = (temp, pending[0].To);
        }

        return steps;
    }
}

public static class BatchRename
{
    private static readonly System.Buffers.SearchValues<char> InvalidChars = System.Buffers.SearchValues.Create("<>:\"/\\|?*");

    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// Builds the preview for <paramref name="sources"/> (in the order that drives {n}). <paramref name="exists"/> says whether a
    /// name is already taken in the folder; names that the batch itself vacates do not count as taken.
    /// </summary>
    public static RenamePlan Plan(IReadOnlyList<RenameSource> sources, BatchRenameOptions options, Func<string, bool> exists)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(exists);

        Regex? regex = null;
        if (options.Find.Length > 0)
        {
            try
            {
                var pattern = options.UseRegex ? options.Find : Regex.Escape(options.Find);
                var flags = RegexOptions.CultureInvariant | (options.MatchCase ? RegexOptions.None : RegexOptions.IgnoreCase);
                regex = new Regex(pattern, flags, TimeSpan.FromSeconds(1));
            }
            catch (ArgumentException ex)
            {
                return Fail(sources, "Invalid pattern: " + ex.Message);
            }
        }

        var (template, templateError) = ParseTemplate(options.Template);
        if (templateError is not null)
        {
            return Fail(sources, templateError);
        }

        var names = new List<string>(sources.Count);
        try
        {
            for (var i = 0; i < sources.Count; i++)
            {
                names.Add(Transform(sources[i], i, options, regex, template));
            }
        }
        catch (RegexMatchTimeoutException)
        {
            return Fail(sources, "The pattern takes too long to match.");
        }
        catch (FormatException)
        {
            return Fail(sources, "The number format in the template is invalid.");
        }

        var vacated = new HashSet<string>(
            sources.Where((s, i) => !string.Equals(s.Name, names[i], StringComparison.Ordinal)).Select(s => s.Name),
            StringComparer.OrdinalIgnoreCase);
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in names)
        {
            counts[n] = counts.GetValueOrDefault(n) + 1;
        }

        var items = new List<RenamePreviewItem>(sources.Count);
        for (var i = 0; i < sources.Count; i++)
        {
            var old = sources[i].Name;
            var name = names[i];
            string? error = null;
            if (!string.Equals(old, name, StringComparison.Ordinal))
            {
                error = Validate(name)
                    ?? (counts[name] > 1 ? "Duplicate target name" : null)
                    ?? (!string.Equals(old, name, StringComparison.OrdinalIgnoreCase) && !vacated.Contains(name) && exists(name)
                        ? "A file or folder with this name already exists"
                        : null);
            }
            else if (counts[name] > 1)
            {
                error = "Duplicate target name";
            }

            items.Add(new(old, name, error));
        }

        return new(items, null);
    }

    private static RenamePlan Fail(IReadOnlyList<RenameSource> sources, string error) =>
        new([.. sources.Select(s => new RenamePreviewItem(s.Name, s.Name, null))], error);

    /// <summary>Returns a reason when <paramref name="name"/> cannot be a Windows file name, else null.</summary>
    public static string? Validate(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "Name is empty";
        }

        if (name.AsSpan().ContainsAny(InvalidChars) || name.Any(char.IsControl))
        {
            return "Name contains invalid characters";
        }

        if (name.EndsWith('.') || name.EndsWith(' '))
        {
            return "Name ends with a dot or space";
        }

        if (name.Length > 255)
        {
            return "Name is too long";
        }

        var dot = name.IndexOf('.');
        if (Reserved.Contains(dot < 0 ? name : name[..dot]))
        {
            return "Name is reserved by Windows";
        }

        return null;
    }

    private static string Transform(RenameSource source, int index, BatchRenameOptions options, Regex? regex, IReadOnlyList<TemplatePart> template)
    {
        var name = source.Name;
        if (regex is not null)
        {
            name = options.UseRegex
                ? regex.Replace(name, options.Replace)
                : regex.Replace(name, _ => options.Replace);
        }

        var (stem, ext) = Split(name, source.IsDirectory);
        stem = options.Case switch
        {
            RenameCase.Lower => stem.ToLowerInvariant(),
            RenameCase.Upper => stem.ToUpperInvariant(),
            RenameCase.Title => TitleCase(stem),
            _ => stem,
        };

        if (template.Count == 0)
        {
            return stem + ext;
        }

        var number = options.Start + (long)index * options.Step;
        var sb = new StringBuilder();
        foreach (var part in template)
        {
            switch (part.Kind)
            {
                case PartKind.Literal: sb.Append(part.Text); break;
                case PartKind.Name: sb.Append(stem); break;
                case PartKind.Ext: sb.Append(ext); break;
                case PartKind.Number: sb.Append(number.ToString(part.Text, CultureInfo.InvariantCulture)); break;
            }
        }

        return sb.ToString();
    }

    private static (string Stem, string Ext) Split(string name, bool isDirectory)
    {
        var dot = name.LastIndexOf('.');
        return isDirectory || dot <= 0 ? (name, "") : (name[..dot], name[dot..]);
    }

    private static string TitleCase(string text)
    {
        var sb = new StringBuilder(text.Length);
        var wordStart = true;
        foreach (var c in text)
        {
            sb.Append(wordStart ? char.ToUpperInvariant(c) : char.ToLowerInvariant(c));
            wordStart = !char.IsLetterOrDigit(c);
        }

        return sb.ToString();
    }

    private enum PartKind { Literal, Name, Ext, Number }

    private readonly record struct TemplatePart(PartKind Kind, string Text);

    private static (IReadOnlyList<TemplatePart> Parts, string? Error) ParseTemplate(string template)
    {
        var parts = new List<TemplatePart>();
        if (string.IsNullOrEmpty(template))
        {
            return (parts, null);
        }

        var literal = new StringBuilder();
        void Flush()
        {
            if (literal.Length > 0)
            {
                parts.Add(new(PartKind.Literal, literal.ToString()));
                literal.Clear();
            }
        }

        for (var i = 0; i < template.Length; i++)
        {
            var c = template[i];
            if (c == '{' && i + 1 < template.Length && template[i + 1] == '{')
            {
                literal.Append('{');
                i++;
            }
            else if (c == '}' && i + 1 < template.Length && template[i + 1] == '}')
            {
                literal.Append('}');
                i++;
            }
            else if (c == '{')
            {
                var close = template.IndexOf('}', i);
                if (close < 0)
                {
                    return ([], "Template has an unclosed '{'");
                }

                var token = template[(i + 1)..close];
                Flush();
                if (token == "name")
                {
                    parts.Add(new(PartKind.Name, ""));
                }
                else if (token == "ext")
                {
                    parts.Add(new(PartKind.Ext, ""));
                }
                else if (token == "n")
                {
                    parts.Add(new(PartKind.Number, "0"));
                }
                else if (token.StartsWith("n:", StringComparison.Ordinal) && token.Length > 2)
                {
                    parts.Add(new(PartKind.Number, token[2..]));
                }
                else
                {
                    return ([], $"Unknown template token '{{{token}}}'");
                }

                i = close;
            }
            else if (c == '}')
            {
                return ([], "Template has an unmatched '}'");
            }
            else
            {
                literal.Append(c);
            }
        }

        Flush();
        return (parts, null);
    }
}
