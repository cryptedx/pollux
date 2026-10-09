using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Pollux.App.Interop;
using Pollux.Core.Clipboard;

namespace Pollux.App.Services;

/// <summary>Encrypts the Clipboard History with DPAPI for the current Windows user (crypt32 directly, no extra package).</summary>
internal sealed unsafe class DpapiClipProtector : IClipProtector
{
    public static DpapiClipProtector Instance { get; } = new();

    private DpapiClipProtector()
    {
    }

    public byte[] Protect(byte[] data) => Transform(data, protect: true);

    public byte[] Unprotect(byte[] data) => Transform(data, protect: false);

    private static byte[] Transform(byte[] data, bool protect)
    {
        fixed (byte* bytes = data)
        {
            var input = new Native.DATA_BLOB { cbData = (uint)data.Length, pbData = (nint)bytes };
            var output = default(Native.DATA_BLOB);
            var ok = protect
                ? Native.CryptProtectData(&input, 0, 0, 0, 0, Native.CRYPTPROTECT_UI_FORBIDDEN, &output)
                : Native.CryptUnprotectData(&input, 0, 0, 0, 0, Native.CRYPTPROTECT_UI_FORBIDDEN, &output);
            if (!ok)
            {
                throw new CryptographicException(Marshal.GetLastPInvokeError());
            }

            try
            {
                return new ReadOnlySpan<byte>((void*)output.pbData, (int)output.cbData).ToArray();
            }
            finally
            {
                Native.LocalFree(output.pbData);
            }
        }
    }
}
