using Pollux.Core;

namespace Pollux.App.Services;

/// <summary>Writes unhandled exceptions to a local file only (ADR 0001: nothing is ever uploaded).</summary>
internal static class CrashLog
{
    /// <summary>Diagnostic trace, only when %LocalAppData%\Pollux\trace.on exists.</summary>
    public static readonly bool TraceEnabled = File.Exists(Path.Combine(PolluxPaths.Current().DataDirectory, "trace.on"));

    public static void Trace(string message)
    {
        if (!TraceEnabled) return;
        try
        {
            var dir = PolluxPaths.Current().LogsDirectory;
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "trace.log"), $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch (IOException)
        {
        }
    }

    public static void Write(Exception ex)
    {
        try
        {
            var dir = PolluxPaths.Current().LogsDirectory;
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "errors.log"), $"{DateTime.Now:s} {ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (IOException)
        {
        }
    }
}
