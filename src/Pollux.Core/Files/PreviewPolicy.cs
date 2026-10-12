namespace Pollux.Core.Files;

/// <summary>How Quick Look and the Preview Pane show a file.</summary>
public enum PreviewMode
{
    /// <summary>A native image control; no web content involved.</summary>
    NativeImage,

    /// <summary>Monaco for text, hex for binaries (the App sniffs the content).</summary>
    Text,

    /// <summary>The file preview WebView2: scripts off, exactly this one file served (S-07).</summary>
    SandboxedWeb,
}

/// <summary>
/// The preview policy (S-07, S-16): decides how a file is shown, its Content-Security-Policy and what the sandboxed
/// file preview serves. Pure decisions; the App's WebView2 wiring only executes them. What every web view may load
/// and accept is <see cref="WebViewPolicy"/>.
/// </summary>
public static class PreviewPolicy
{
    private static readonly HashSet<string> Images = new(StringComparer.OrdinalIgnoreCase) { "png", "jpg", "jpeg", "gif", "bmp", "webp", "ico", "tif", "tiff" };

    private static readonly Dictionary<string, string> WebTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["html"] = "text/html",
        ["htm"] = "text/html",
        ["svg"] = "image/svg+xml",
        ["pdf"] = "application/pdf",
        ["mp4"] = "video/mp4",
        ["webm"] = "video/webm",
        ["mp3"] = "audio/mpeg",
        ["wav"] = "audio/wav",
    };

    /// <summary>Text and source files Monaco shows better than a shell preview handler (PowerToys registers some of them).</summary>
    private static readonly HashSet<string> TextTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "txt", "text", "md", "markdown", "log", "csv", "tsv", "json", "jsonc", "json5", "xml", "xaml", "xsd", "xsl", "xslt", "yaml", "yml", "toml",
        "ini", "cfg", "conf", "config", "env", "properties", "reg", "inf", "nfo", "rst", "tex", "diff", "patch",
        "cs", "csx", "vb", "fs", "fsx", "fsi", "c", "h", "cpp", "hpp", "cc", "cxx", "hh", "hxx", "m", "mm", "java", "kt", "kts", "scala", "groovy", "gradle",
        "js", "mjs", "cjs", "ts", "mts", "cts", "jsx", "tsx", "css", "scss", "sass", "less", "vue", "svelte", "go", "rs", "py", "pyw", "rb", "php", "pl", "pm",
        "swift", "dart", "lua", "r", "sql", "sh", "bash", "zsh", "fish", "ps1", "psm1", "psd1", "bat", "cmd", "asm", "hs", "ex", "exs", "erl", "clj", "el", "lisp",
        "sln", "slnx", "csproj", "vbproj", "fsproj", "vcxproj", "props", "targets", "resx", "manifest", "editorconfig", "gitignore", "gitattributes",
        "dockerfile", "makefile", "cmake", "tf", "hcl", "proto", "graphql", "gql", "lock",
    };

    /// <summary>
    /// Whether a registered shell preview handler (ADR 0009) should show files with <paramref name="extension"/> (without
    /// dot). Pollux keeps images, its web previews (HTML, SVG, PDF, media) and text/code; everything else (Office documents,
    /// .msg, .eml, Visio, fonts, unknown types) goes to the handler when one is registered.
    /// </summary>
    public static bool PrefersShellHandler(string extension) =>
        extension.Length > 0 && !Images.Contains(extension) && !WebTypes.ContainsKey(extension) && !TextTypes.Contains(extension);

    /// <summary>
    /// Whether a file whose <c>Zone.Identifier</c> stream holds <paramref name="zoneIdentifier"/> (null: none) may go to a
    /// shell preview handler. Files from the Internet or Restricted zone (ZoneId 3 or 4) never do, like Explorer since
    /// its 2025 preview hardening: a handler could follow links in them and leak NTLM credentials. Unreadable zone
    /// data counts as Internet.
    /// </summary>
    public static bool AllowsShellHandler(string? zoneIdentifier)
    {
        if (zoneIdentifier is null) return true;
        foreach (var raw in zoneIdentifier.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith("ZoneId", StringComparison.OrdinalIgnoreCase)) continue;
            var eq = line.IndexOf('=', StringComparison.Ordinal);
            if (eq < 0) return false;
            return int.TryParse(line[(eq + 1)..].Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var zone) && zone < 3;
        }

        return false;
    }

    /// <summary>The preview mode for a file extension (without dot).</summary>
    public static PreviewMode ModeFor(string extension) =>
        Images.Contains(extension) ? PreviewMode.NativeImage
        : WebTypes.ContainsKey(extension) ? PreviewMode.SandboxedWeb
        : PreviewMode.Text;

    /// <summary>
    /// The Content-Security-Policy header for a previewed HTML or SVG file at <paramref name="fileUrl"/>: no script,
    /// connect, frames or objects; images, media and styles only from the file itself (plus inline styles and
    /// <c>data:</c> images, which are part of the file). Null for PDF and media, which are no scripted documents
    /// and whose built-in viewers a document CSP would break.
    /// </summary>
    public static string? ContentSecurityPolicy(string extension, string fileUrl) =>
        extension.ToLowerInvariant() is "html" or "htm" or "svg"
            ? string.Join("; ",
                "default-src 'none'",
                "script-src 'none'",
                "connect-src 'none'",
                "frame-src 'none'",
                "object-src 'none'",
                "form-action 'none'",
                "base-uri 'none'",
                $"img-src {fileUrl} data:",
                $"media-src {fileUrl}",
                $"style-src {fileUrl} 'unsafe-inline'",
                "sandbox")
            : null;

    /// <summary>The virtual host the file preview serves its one file from. Nothing is mapped to it.</summary>
    public const string PreviewHost = "pollux.preview";

    /// <summary>The URL the file preview navigates to for <paramref name="path"/>: only the file name, on the preview host.</summary>
    public static string FileUrl(string path) => $"https://{PreviewHost}/{Uri.EscapeDataString(Path.GetFileName(path))}";

    /// <summary>
    /// How the file preview's request handler answers <paramref name="requestUri"/> while <paramref name="currentFile"/>
    /// is shown: the file itself with its type and CSP, 404 for anything else on the preview host (siblings,
    /// subfolders, stale files: S-07), null for requests to other hosts.
    /// </summary>
    public static PreviewResponse? Respond(string requestUri, string? currentFile)
    {
        if (!Uri.TryCreate(requestUri, UriKind.Absolute, out var uri) || !string.Equals(uri.Host, PreviewHost, StringComparison.OrdinalIgnoreCase))
            return null;
        if (currentFile is null || !IsExactly(uri, FileUrl(currentFile)))
            return PreviewResponse.NotFound;

        var extension = Path.GetExtension(currentFile).TrimStart('.');
        return new PreviewResponse(200, currentFile, WebTypes.GetValueOrDefault(extension, "application/octet-stream"), ContentSecurityPolicy(extension, FileUrl(currentFile)));
    }

    /// <summary>
    /// Opens the answer to <paramref name="requestUri"/> for the file preview's request handler: status, headers in
    /// WebView2's "Name: value\r\n" form and the body stream (the caller hands it to WebView2, which disposes it).
    /// A single <c>bytes=</c> range is answered with 206 so media can seek; null for requests to other hosts.
    /// </summary>
    public static PreviewContent? Open(string requestUri, string? currentFile, string? rangeHeader)
    {
        var response = Respond(requestUri, currentFile);
        if (response is null) return null;
        if (response.FilePath is null) return PreviewContent.NotFound;

        FileStream file;
        try { file = new FileStream(response.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return PreviewContent.NotFound; }

        var length = file.Length;
        var headers = new System.Text.StringBuilder()
            .Append("Content-Type: ").Append(response.ContentType).Append("\r\n")
            .Append("X-Content-Type-Options: nosniff\r\n")
            .Append("Cache-Control: no-store\r\n")
            .Append("Accept-Ranges: bytes\r\n");
        if (response.ContentSecurityPolicy is not null)
            headers.Append("Content-Security-Policy: ").Append(response.ContentSecurityPolicy).Append("\r\n");

        var range = ParseRange(rangeHeader, length);
        if (range is { } r && r.Start < 0)
        {
            file.Dispose();
            headers.Append("Content-Range: bytes */").Append(length).Append("\r\n");
            return new PreviewContent(416, "Range Not Satisfiable", headers.ToString(), null);
        }

        if (range is { } part)
        {
            var count = part.End - part.Start + 1;
            headers.Append("Content-Range: bytes ").Append(part.Start).Append('-').Append(part.End).Append('/').Append(length).Append("\r\n")
                .Append("Content-Length: ").Append(count).Append("\r\n");
            return new PreviewContent(206, "Partial Content", headers.ToString(), new SliceStream(file, part.Start, count));
        }

        headers.Append("Content-Length: ").Append(length).Append("\r\n");
        return new PreviewContent(200, "OK", headers.ToString(), file);
    }

    /// <summary>
    /// A single <c>bytes=a-b</c>, <c>bytes=a-</c> or <c>bytes=-n</c> range clamped to the file; null for no or an
    /// unsupported header (whole file), Start = -1 for an unsatisfiable range.
    /// </summary>
    private static (long Start, long End)? ParseRange(string? header, long length)
    {
        if (header is null || !header.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase)) return null;
        var spec = header[6..].Trim();
        if (spec.Contains(',', StringComparison.Ordinal)) return null;
        var dash = spec.IndexOf('-', StringComparison.Ordinal);
        if (dash < 0) return null;
        var first = spec[..dash].Trim();
        var last = spec[(dash + 1)..].Trim();
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        const System.Globalization.NumberStyles digits = System.Globalization.NumberStyles.None;

        if (first.Length == 0)
        {
            if (!long.TryParse(last, digits, inv, out var n)) return null;
            if (n == 0 || length == 0) return (-1, -1);
            return (Math.Max(0, length - n), length - 1);
        }

        if (!long.TryParse(first, digits, inv, out var start)) return null;
        long end = length - 1;
        if (last.Length > 0)
        {
            if (!long.TryParse(last, digits, inv, out end) || end < start) return null;
            end = Math.Min(end, length - 1);
        }

        return start >= length ? (-1, -1) : (start, end);
    }

    /// <summary>Exact match of scheme, host, port, path and (empty) query; no user info.</summary>
    private static bool IsExactly(Uri uri, string expected)
    {
        var e = new Uri(expected);
        return uri.Scheme == Uri.UriSchemeHttps
            && uri.IsDefaultPort
            && uri.UserInfo.Length == 0
            && uri.Query.Length == 0
            && string.Equals(uri.Host, e.Host, StringComparison.OrdinalIgnoreCase)
            && string.Equals(Uri.UnescapeDataString(uri.AbsolutePath), Uri.UnescapeDataString(e.AbsolutePath), StringComparison.Ordinal);
    }
}

/// <summary>The file preview's answer to one request: status, the file to stream (200 only), its type and CSP header.</summary>
public sealed record PreviewResponse(int Status, string? FilePath, string? ContentType, string? ContentSecurityPolicy)
{
    public static readonly PreviewResponse NotFound = new(404, null, null, null);
}

/// <summary>An opened answer of the file preview's request handler. <see cref="Body"/> is null unless there is content.</summary>
public sealed class PreviewContent(int status, string reason, string headers, Stream? body) : IDisposable
{
    public static PreviewContent NotFound => new(404, "Not Found", "", null);

    public int Status { get; } = status;
    public string Reason { get; } = reason;
    public string Headers { get; } = headers;
    public Stream? Body { get; } = body;

    public void Dispose() => Body?.Dispose();
}

/// <summary>A read-only, seekable window of <paramref name="count"/> bytes from <paramref name="offset"/> into a seekable stream.</summary>
internal sealed class SliceStream(Stream inner, long offset, long count) : Stream
{
    private long _position;

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => count;

    public override long Position
    {
        get => _position;
        set => _position = Math.Clamp(value, 0, count);
    }

    public override int Read(byte[] buffer, int offset1, int count1) => Read(buffer.AsSpan(offset1, count1));

    public override int Read(Span<byte> buffer)
    {
        var left = count - _position;
        if (left <= 0) return 0;
        if (buffer.Length > left) buffer = buffer[..(int)left];
        inner.Position = offset + _position;
        var n = inner.Read(buffer);
        _position += n;
        return n;
    }

    public override long Seek(long offset1, SeekOrigin origin) => Position = origin switch
    {
        SeekOrigin.Begin => offset1,
        SeekOrigin.Current => _position + offset1,
        _ => count + offset1,
    };

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset1, int count1) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) inner.Dispose();
        base.Dispose(disposing);
    }
}
