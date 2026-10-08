using System.Diagnostics;
using System.Globalization;

namespace Pollux.App.Services;

/// <summary>
/// Measures startup milestones against the spec budgets and appends them to %LocalAppData%\Pollux\logs\startup.log.
/// Local file only (ADR 0001). Times are milliseconds since process start.
/// </summary>
internal static class StartupLog
{
    private static readonly long ProcessStart = Stopwatch.GetTimestamp() - (long)((DateTime.Now - Process.GetCurrentProcess().StartTime).TotalSeconds * Stopwatch.Frequency);
    private static readonly List<string> Marks = [];
    private static string? _file;
    private static bool _enabled = true;

    public static void Configure(string logsDirectory, bool enabled)
    {
        _file = Path.Combine(logsDirectory, "startup.log");
        _enabled = enabled;
    }

    public static double SinceProcessStart => Stopwatch.GetElapsedTime(ProcessStart).TotalMilliseconds;

    public static void Mark(string name) => Marks.Add($"{name}={SinceProcessStart.ToString("0", CultureInfo.InvariantCulture)}ms");

    /// <summary>Warm opens (resident process) are measured from the request, not from process start.</summary>
    public static void Warm(string name, long requestTimestamp) =>
        Write($"{DateTime.Now:s} warm {name}={Stopwatch.GetElapsedTime(requestTimestamp).TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture)}ms");

    public static void Flush(string kind)
    {
        if (Marks.Count == 0) return;
        Write($"{DateTime.Now:s} {kind} " + string.Join(' ', Marks));
        Marks.Clear();
    }

    private static void Write(string line)
    {
        if (!_enabled || _file is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            File.AppendAllText(_file, line + Environment.NewLine);
        }
        catch (IOException)
        {
            // Logging must never break startup.
        }
    }
}
