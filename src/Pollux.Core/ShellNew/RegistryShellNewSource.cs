using System.Runtime.Versioning;
using System.Security;
using System.Text;
using Microsoft.Win32;

namespace Pollux.Core.ShellNew;

/// <summary>
/// Reads <c>HKCR\.ext\ShellNew</c> and <c>HKCR\.ext\&lt;ProgID&gt;\ShellNew</c> (Office registers there). Nothing on other systems.
/// </summary>
public sealed class RegistryShellNewSource : IShellNewSource
{
    public IReadOnlyList<ShellNewRaw> Read() => OperatingSystem.IsWindows() ? ReadAll() : [];

    [SupportedOSPlatform("windows")]
    private static List<ShellNewRaw> ReadAll()
    {
        var result = new List<ShellNewRaw>();
        try
        {
            foreach (var name in Registry.ClassesRoot.GetSubKeyNames())
            {
                if (name.Length < 2 || name[0] != '.') continue;
                try
                {
                    ReadExtension(name, result);
                }
                catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
                {
                }
            }
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
        }

        return result;
    }

    [SupportedOSPlatform("windows")]
    private static void ReadExtension(string extension, List<ShellNewRaw> result)
    {
        using var key = Registry.ClassesRoot.OpenSubKey(extension);
        if (key is null) return;
        var defaultProgId = key.GetValue(null) as string;
        using (var shellNew = key.OpenSubKey("ShellNew"))
        {
            if (shellNew is not null) result.Add(ReadKey(extension, defaultProgId, shellNew));
        }

        foreach (var sub in key.GetSubKeyNames())
        {
            if (sub.Equals("ShellNew", StringComparison.OrdinalIgnoreCase)) continue;
            using var progKey = key.OpenSubKey(sub + @"\ShellNew");
            if (progKey is not null) result.Add(ReadKey(extension, sub, progKey));
        }
    }

    [SupportedOSPlatform("windows")]
    private static ShellNewRaw ReadKey(string extension, string? progId, RegistryKey shellNew)
    {
        string? friendly = null, description = null;
        if (!string.IsNullOrEmpty(progId))
        {
            using var prog = Registry.ClassesRoot.OpenSubKey(progId);
            friendly = prog?.GetValue("FriendlyTypeName", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
            description = prog?.GetValue(null) as string;
        }

        var names = shellNew.GetValueNames();
        bool Has(string n) => names.Contains(n, StringComparer.OrdinalIgnoreCase);
        byte[]? data = shellNew.GetValue("Data") switch
        {
            byte[] bytes => bytes,
            string text => Encoding.UTF8.GetBytes(text),
            _ => null,
        };
        return new ShellNewRaw(
            extension,
            progId,
            shellNew.GetValue("ItemName", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string,
            Has("NullFile"),
            shellNew.GetValue("FileName") as string,
            data,
            Has("Command") || Has("Handler") || Has("Directory"),
            friendly,
            description);
    }
}
