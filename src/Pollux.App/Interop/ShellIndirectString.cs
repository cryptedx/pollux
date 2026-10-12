using System.Runtime.InteropServices;

namespace Pollux.App.Interop;

/// <summary>Resolves "@dll,-id" resource strings (as the registry stores type names) via SHLoadIndirectString.</summary>
internal static unsafe partial class ShellIndirectString
{
    [LibraryImport("shlwapi.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SHLoadIndirectString(string source, char* buffer, uint bufferChars, nint reserved);

    /// <summary>The loaded string, or null if the resource cannot be resolved.</summary>
    public static string? Load(string source)
    {
        const int bufferChars = 260;
        var buffer = stackalloc char[bufferChars];
        return SHLoadIndirectString(source, buffer, bufferChars, 0) >= 0 ? new string(buffer) : null;
    }
}
