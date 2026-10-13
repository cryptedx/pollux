using System.Text.Json;

namespace Pollux.Core.Files;

/// <summary>
/// A web message from one of Pollux's own pages (S-16). <see cref="Read"/> is the only way to get one: it drops
/// messages whose source is not the view's own page (preview policy) and anything that is not a JSON object with a
/// string <c>type</c>, so App handlers never throw on hostile or malformed input. Field accessors return null instead
/// of throwing when a field is missing or has the wrong JSON type.
/// </summary>
public sealed class WebMessage
{
    /// <summary>Upper bound for terminal columns and rows; far above any real window, far below <see cref="short.MaxValue"/>.</summary>
    public const short MaxTerminalSize = 1000;

    private readonly JsonElement _root;

    private WebMessage(JsonElement root, string type)
    {
        _root = root;
        Type = type;
    }

    /// <summary>The message's <c>type</c> field.</summary>
    public string Type { get; }

    /// <summary>
    /// Reads <paramref name="json"/> posted by the document at <paramref name="source"/> into <paramref name="view"/>;
    /// null when the source is foreign (<see cref="WebViewPolicy.IsOwnSource"/>) or the content malformed.
    /// </summary>
    public static WebMessage? Read(string? source, string? json, WebViewKind view)
    {
        if (!WebViewPolicy.IsOwnSource(source, view) || string.IsNullOrEmpty(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("type", out var type)
                || type.ValueKind != JsonValueKind.String)
                return null;
            return new WebMessage(root.Clone(), type.GetString()!);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The string field <paramref name="name"/>, or null when missing or not a string.</summary>
    public string? Text(string name) =>
        _root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>
    /// The base64 string field <paramref name="name"/> decoded; null when missing, not a string, not valid base64, empty,
    /// or larger than <paramref name="maxBytes"/> once decoded (the size is checked before decoding).
    /// </summary>
    public byte[]? Bytes(string name, int maxBytes)
    {
        var text = Text(name);
        if (string.IsNullOrEmpty(text) || (long)text.Length / 4 * 3 > (long)maxBytes + 2) return null;
        var buffer = new byte[(text.Length / 4 * 3) + 3];
        if (!Convert.TryFromBase64String(text, buffer, out var written) || written == 0 || written > maxBytes) return null;
        return buffer[..written];
    }

    /// <summary>
    /// The number field <paramref name="name"/> as a terminal column or row count, clamped to
    /// 1..<see cref="MaxTerminalSize"/>; null when missing or not a number. Never wraps like a cast to short.
    /// </summary>
    public short? TerminalSize(string name)
    {
        if (!_root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number) return null;
        var number = value.GetDouble(); // never throws for a JSON number; huge values become large doubles or infinity
        return (short)Math.Clamp(Math.Floor(number), 1, MaxTerminalSize);
    }
}
