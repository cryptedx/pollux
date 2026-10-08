using Microsoft.Win32;

namespace Pollux.App.Services;

/// <summary>Start with Windows via the per-user Run key (no admin).</summary>
internal static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Name = "Pollux";

    public static void Apply(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
        {
            key.SetValue(Name, $"\"{Environment.ProcessPath}\" --background");
        }
        else if (key.GetValue(Name) is not null)
        {
            key.DeleteValue(Name);
        }
    }
}
