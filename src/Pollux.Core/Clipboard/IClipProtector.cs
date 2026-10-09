namespace Pollux.Core.Clipboard;

/// <summary>Encrypts Clips at rest. The app uses DPAPI for the current user; tests use a reversible fake.</summary>
public interface IClipProtector
{
    byte[] Protect(byte[] data);

    /// <summary>Throws <see cref="System.Security.Cryptography.CryptographicException"/> when the data cannot be decrypted.</summary>
    byte[] Unprotect(byte[] data);
}
