namespace Pollux.Core.Archives;

/// <summary>
/// Paths that point into a .zip archive as if it were a folder ("C:\data\a.zip\docs\b.txt").
/// Implemented without System.IO.Path so it behaves the same on every OS the tests run on.
/// Zip browsing itself parses locations with <see cref="ZipFolder.TryParse"/>, which keeps the separators as written.
/// </summary>
public static class ZipPath
{
    /// <summary>Splits a path at the first segment ending in ".zip". <c>inner</c> uses forward slashes, no leading or trailing slash ("" = archive root).</summary>
    public static bool TryParse(string path, out string archive, out string inner)
    {
        archive = inner = "";
        var p = path.Replace('/', '\\');
        var start = 0;
        while (start < p.Length)
        {
            var end = p.IndexOf('\\', start);
            if (end < 0) end = p.Length;
            if (end - start > 4 && p.AsSpan(start, end - start).EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                archive = p[..end];
                inner = string.Join('/', p[Math.Min(end + 1, p.Length)..].Split('\\', StringSplitOptions.RemoveEmptyEntries));
                return true;
            }

            start = end + 1;
        }

        return false;
    }

    public static bool IsZipFile(string path) =>
        path.Length > 4 && path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);

    /// <summary>Builds the browsable path of an entry inside an archive.</summary>
    public static string Combine(string archive, string inner) =>
        inner.Length == 0 ? archive : archive + "\\" + inner.Replace('/', '\\');
}
