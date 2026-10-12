namespace Pollux.Core.ShellNew;

/// <summary>How a ShellNew entry fills the new file.</summary>
public enum ShellNewKind
{
    /// <summary>Zero-byte file (<c>NullFile</c>).</summary>
    Empty,

    /// <summary>Copy of a template file (<c>FileName</c>).</summary>
    Template,

    /// <summary>The bytes of the <c>Data</c> value.</summary>
    Data,
}

/// <summary>One item of the "New" menu: a file type that can be created from the registry's ShellNew registration.</summary>
public sealed record ShellNewEntry(string Extension, string DisplayName, ShellNewKind Kind, string? TemplatePath = null, byte[]? Data = null);

/// <summary>A ShellNew key as found in the registry, before selection (tests pass fakes).</summary>
/// <param name="Extension">With the leading dot, e.g. ".docx".</param>
/// <param name="ProgId">The ProgID the key belongs to, if known.</param>
/// <param name="ItemName">The key's <c>ItemName</c> value (may be an indirect "@dll,-id" string).</param>
/// <param name="NullFile">Whether <c>NullFile</c> is present.</param>
/// <param name="FileName">The <c>FileName</c> template (relative to the template folders, or absolute).</param>
/// <param name="Data">The <c>Data</c> value (REG_BINARY bytes, or the string as UTF-8).</param>
/// <param name="HasCommand">Whether <c>Command</c>, <c>Handler</c> or <c>Directory</c> is present: those create the item through code, not data.</param>
/// <param name="FriendlyTypeName">The ProgID's <c>FriendlyTypeName</c> (may be indirect).</param>
/// <param name="ProgIdDescription">The ProgID's default value.</param>
public sealed record ShellNewRaw(
    string Extension,
    string? ProgId,
    string? ItemName,
    bool NullFile,
    string? FileName,
    byte[]? Data,
    bool HasCommand,
    string? FriendlyTypeName,
    string? ProgIdDescription);

/// <summary>Where ShellNew registrations come from.</summary>
public interface IShellNewSource
{
    /// <summary>Every ShellNew key, extension level first, then the ProgID subkeys of each extension.</summary>
    IReadOnlyList<ShellNewRaw> Read();
}
