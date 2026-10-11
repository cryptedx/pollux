using System.Globalization;

namespace Pollux.Core.Archives;

/// <summary>
/// Sanity limits checked before an extraction writes anything (zip bomb guard): the total uncompressed size against
/// the free space (minus a margin) and a ceiling, and each entry's compression ratio. Exceeding one asks first.
/// </summary>
/// <param name="MaxTotalBytes">Ceiling for the total uncompressed size of the extracted files.</param>
/// <param name="FreeSpaceMargin">Space that must stay free on the target drive after extracting.</param>
/// <param name="MaxRatio">Highest uncompressed/compressed ratio an entry may have without asking.</param>
/// <param name="RatioMinBytes">Entries smaller than this never trip the ratio check (small text files compress well).</param>
public sealed record ExtractLimits(long MaxTotalBytes, long FreeSpaceMargin, double MaxRatio, long RatioMinBytes)
{
    /// <summary>10 GiB ceiling, 1 GiB kept free, 100:1 ratio for entries of 1 MiB and more.</summary>
    public static ExtractLimits Default { get; } = new(10L << 30, 1L << 30, 100, 1L << 20);

    /// <summary>No checks at all (the user already confirmed).</summary>
    public static ExtractLimits None { get; } = new(long.MaxValue, 0, double.PositiveInfinity, long.MaxValue);
}

/// <summary>What an archive would expand to and which limit it breaks; handed to the confirmation callback.</summary>
/// <param name="TotalBytes">Declared uncompressed size of all extracted files.</param>
/// <param name="FreeBytes">Free space on the target drive, or null when it could not be determined.</param>
/// <param name="WorstEntry">Name of the entry with the highest ratio over the limit, or null when no entry is over it.</param>
public sealed record ExtractLimitBreach(
    string ZipPath,
    long TotalBytes,
    long? FreeBytes,
    bool OverCeiling,
    bool OverFreeSpace,
    string? WorstEntry,
    double WorstRatio)
{
    /// <summary>One line per broken limit, for a dialog or a toast.</summary>
    public string Describe()
    {
        var lines = new List<string>();
        var total = FormatBytes(TotalBytes);
        if (OverFreeSpace) lines.Add($"It expands to {total}, but only {FormatBytes(FreeBytes ?? 0)} is free.");
        else if (OverCeiling) lines.Add($"It expands to {total}.");
        if (WorstEntry is not null)
        {
            lines.Add(string.Create(CultureInfo.InvariantCulture, $"\"{WorstEntry}\" is compressed {WorstRatio:0}:1, typical of a zip bomb."));
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["bytes", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? string.Create(CultureInfo.InvariantCulture, $"{bytes} bytes")
            : string.Create(CultureInfo.InvariantCulture, $"{value:0.#} {units[unit]}");
    }
}

/// <summary>Thrown when an archive breaks an <see cref="ExtractLimits"/> limit and extraction was not confirmed. Nothing was written.</summary>
public sealed class ExtractLimitExceededException : Exception
{
    public ExtractLimitExceededException(ExtractLimitBreach breach)
        : base("Extraction stopped: " + (breach ?? throw new ArgumentNullException(nameof(breach))).Describe())
    {
        Breach = breach;
    }

    public ExtractLimitBreach Breach { get; }
}
