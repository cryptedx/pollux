using Pollux.Core.Navigation;

namespace Pollux.Core.Operations;

/// <summary>What a drop does. Values match WinRT's DataPackageOperation.</summary>
[Flags]
public enum DropOperation
{
    None = 0,
    Copy = 1,
    Move = 2,
    Link = 4,
}

/// <summary>Keys and buttons held while dragging. Values match WinRT's DragDropModifiers.</summary>
[Flags]
public enum DropKeys
{
    None = 0,
    Shift = 1,
    Control = 2,
    Alt = 4,
    RightButton = 32,
}

/// <summary>The operation a drop performs, and whether a right-button drag asks for it at drop time.</summary>
public readonly record struct DropPlan(DropOperation Operation, bool Ask)
{
    public static readonly DropPlan Refused = new(DropOperation.None, false);

    /// <summary>The drag caption, e.g. "Move to Docs"; empty when refused.</summary>
    public string Caption(string targetName) => Operation switch
    {
        DropOperation.Copy => "Copy to " + targetName,
        DropOperation.Move => "Move to " + targetName,
        DropOperation.Link => "Create shortcut in " + targetName,
        _ => "",
    };
}

/// <summary>Explorer's drop rules: modifiers, same-volume default, and folders that cannot take the dragged items.</summary>
public static class DropDecision
{
    /// <summary>
    /// Alt or Ctrl+Shift links, Ctrl copies, Shift moves; otherwise items on the target's volume move and others copy.
    /// Refused when the target is one of the sources or inside one, or (for copy and move) the sources' own folder, or when
    /// the source does not allow the operation. A default move the source does not allow becomes a copy; a link is made by
    /// Pollux itself, so it only needs Copy. Items of a read-only location (<paramref name="copyOnly"/>: a zip, a phone) are
    /// only ever copied out, whatever the keys say. A drive or share root (<see cref="IsVolumeRoot"/>) only ever gets a shortcut,
    /// like in Explorer: a stray drag of a drive must not copy or move all of it.
    /// </summary>
    public static DropPlan Decide(DropKeys keys, IReadOnlyList<string> sources, string target, DropOperation allowed, bool copyOnly = false)
    {
        if (sources.Count == 0 || string.IsNullOrWhiteSpace(target)) return DropPlan.Refused;
        if (!copyOnly && sources.Any(IsVolumeRoot))
        {
            return Allowed(DropOperation.Link, allowed) == DropOperation.Link ? new DropPlan(DropOperation.Link, keys.HasFlag(DropKeys.RightButton)) : DropPlan.Refused;
        }

        if (!IsValidTarget(sources, target)) return DropPlan.Refused;
        if (copyOnly)
        {
            return allowed.HasFlag(DropOperation.Copy) && !sources.All(s => IsDirectChild(s, target))
                ? new DropPlan(DropOperation.Copy, keys.HasFlag(DropKeys.RightButton))
                : DropPlan.Refused;
        }

        var ctrl = keys.HasFlag(DropKeys.Control);
        var shift = keys.HasFlag(DropKeys.Shift);
        DropOperation op;
        if (keys.HasFlag(DropKeys.Alt) || (ctrl && shift)) op = Allowed(DropOperation.Link, allowed);
        else if (ctrl) op = Allowed(DropOperation.Copy, allowed);
        else if (shift) op = Allowed(DropOperation.Move, allowed);
        else
        {
            op = sources.All(s => SameVolume(s, target)) ? DropOperation.Move : DropOperation.Copy;
            if (Allowed(op, allowed) == DropOperation.None) op = Allowed(DropOperation.Copy, allowed);
        }

        // Copy or move into the folder the items already live in does nothing; a shortcut there is fine.
        if (op is DropOperation.Copy or DropOperation.Move && sources.All(s => IsDirectChild(s, target))) return DropPlan.Refused;
        return op == DropOperation.None ? DropPlan.Refused : new DropPlan(op, keys.HasFlag(DropKeys.RightButton));
    }

    /// <summary>What to tell the drag source: a link Pollux makes itself is reported as Copy when the source does not offer Link.</summary>
    public static DropOperation Reported(DropOperation op, DropOperation allowed) =>
        op == DropOperation.Link && !allowed.HasFlag(DropOperation.Link) ? DropOperation.Copy : op;

    /// <summary>
    /// The operations a right-button drop menu offers, in menu order; only Copy for items of a read-only location, only
    /// Link when a drive or share root is among the items (<paramref name="linkOnly"/>).
    /// </summary>
    public static IReadOnlyList<DropOperation> Choices(DropOperation allowed, bool copyOnly = false, bool linkOnly = false) =>
        [.. new[] { DropOperation.Copy, DropOperation.Move, DropOperation.Link }.Where(o => Allowed(o, allowed) == o
            && (!copyOnly || o == DropOperation.Copy) && (!linkOnly || copyOnly || o == DropOperation.Link))];

    /// <summary>A drive root ("C:\") or a share root ("\\server\share"), as This PC and a server list them.</summary>
    public static bool IsVolumeRoot(string path) =>
        Volume(path) is { } volume && WinPath.Normalize(path).TrimEnd('\\').Equals(volume, StringComparison.OrdinalIgnoreCase);

    /// <summary>False when the target folder is one of the sources or lies inside one of them.</summary>
    public static bool IsValidTarget(IReadOnlyList<string> sources, string target) => !sources.Any(s => WinPath.IsSameOrUnder(target, s));

    /// <summary>"C:" for drive paths, "\\server\share" for UNC paths, null for anything else.</summary>
    public static string? Volume(string path)
    {
        var p = WinPath.Normalize(path);
        if (WinPath.IsUnc(p))
        {
            var parts = p[2..].Split('\\');
            return parts.Length >= 2 && parts[1].Length > 0 ? $@"\\{parts[0]}\{parts[1]}".ToUpperInvariant() : null;
        }

        return p.Length >= 2 && p[1] == ':' && char.IsLetter(p[0]) ? char.ToUpperInvariant(p[0]) + ":" : null;
    }

    public static bool SameVolume(string a, string b) => Volume(a) is { } va && va == Volume(b);

    private static bool IsDirectChild(string path, string folder) =>
        WinPath.Parent(path) is { } parent && string.Equals(parent, WinPath.Normalize(folder), StringComparison.OrdinalIgnoreCase);

    private static DropOperation Allowed(DropOperation op, DropOperation allowed)
    {
        if (op == DropOperation.Link) return allowed.HasFlag(DropOperation.Link) || allowed.HasFlag(DropOperation.Copy) ? op : DropOperation.None;
        return allowed.HasFlag(op) ? op : DropOperation.None;
    }
}
