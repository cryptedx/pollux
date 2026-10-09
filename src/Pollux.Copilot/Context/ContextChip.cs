using System.Security.Cryptography;
using System.Text;

namespace Pollux.Copilot.Context;

/// <summary>What a context chip stands for.</summary>
public enum ContextChipKind
{
    /// <summary>The folder the active Pane shows.</summary>
    Location,

    /// <summary>The entries selected in the active Pane.</summary>
    Selection,

    /// <summary>The folder of the other Pane.</summary>
    Pane,

    /// <summary>The folder of a Tab.</summary>
    Tab,

    /// <summary>A file or folder added with @ or by drag and drop.</summary>
    File,
}

/// <summary>One visible, removable piece of context a message carries to Copilot.</summary>
public sealed record ContextChip(ContextChipKind Kind, string Label, IReadOnlyList<string> Paths)
{
    /// <summary>Stable for the same kind and paths, so the page can tell a changed selection from an unchanged one.</summary>
    public string Id { get; } = MakeId(Kind, Paths);

    public static string KindName(ContextChipKind kind) => kind switch
    {
        ContextChipKind.Location => "location",
        ContextChipKind.Selection => "selection",
        ContextChipKind.Pane => "pane",
        ContextChipKind.Tab => "tab",
        _ => "file",
    };

    private static string MakeId(ContextChipKind kind, IReadOnlyList<string> paths)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', paths)));
        return KindName(kind) + ":" + Convert.ToHexStringLower(hash.AsSpan(0, 6));
    }
}
