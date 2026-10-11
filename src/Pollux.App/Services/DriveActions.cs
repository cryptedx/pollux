using System.ComponentModel;
using Pollux.App.Interop;

namespace Pollux.App.Services;

/// <summary>
/// Map, disconnect and eject drives. All three are plain OS functionality triggered by the user (ADR 0001):
/// Windows asks for credentials itself, Pollux never handles a password.
/// Every method blocks, so call them off the UI thread. They return null on success, a message otherwise.
/// </summary>
internal static unsafe class DriveActions
{
    /// <summary>Maps <paramref name="share"/> (\\server\share) to <paramref name="letter"/> ("Z:"). Returns "" when the user cancelled the Windows credential prompt.</summary>
    public static string? Map(nint owner, string letter, string share)
    {
        fixed (char* local = letter)
        fixed (char* remote = share)
        {
            var resource = new Native.NETRESOURCEW { dwType = Native.RESOURCETYPE_DISK, lpLocalName = local, lpRemoteName = remote };
            var rc = Native.WNetAddConnection3W(owner, &resource, null, null, Native.CONNECT_INTERACTIVE | Native.CONNECT_UPDATE_PROFILE);
            return rc switch
            {
                Native.NO_ERROR => null,
                Native.ERROR_CANCELLED => "",
                _ => new Win32Exception((int)rc).Message,
            };
        }
    }

    /// <summary>Disconnects the network drive <paramref name="letter"/> ("Z:") and forgets it for the next sign-in.</summary>
    public static string? Disconnect(string letter)
    {
        fixed (char* local = letter)
        {
            var rc = Native.WNetCancelConnection2W(local, Native.CONNECT_UPDATE_PROFILE, false);
            return rc == Native.NO_ERROR ? null : new Win32Exception((int)rc).Message;
        }
    }

    /// <summary>Ejects the removable or optical drive <paramref name="letter"/> ("E:"): lock, dismount, unlock, eject.</summary>
    public static string? Eject(string letter)
    {
        var handle = Native.CreateFileW(@"\\.\" + letter, Native.GENERIC_READ | Native.GENERIC_WRITE, Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE, 0, Native.OPEN_EXISTING, 0, 0);
        if (handle == -1) return new Win32Exception(System.Runtime.InteropServices.Marshal.GetLastPInvokeError()).Message;
        try
        {
            // A failed lock means files are still open on the drive; ejecting anyway could lose data.
            if (!Native.DeviceIoControl(handle, Native.FSCTL_LOCK_VOLUME, null, 0, null, 0, out _, 0))
            {
                return "The drive is in use. Close files and folders on it and try again.";
            }

            Native.DeviceIoControl(handle, Native.FSCTL_DISMOUNT_VOLUME, null, 0, null, 0, out _, 0);
            byte allow = 0; // PREVENT_MEDIA_REMOVAL.PreventMediaRemoval = FALSE
            Native.DeviceIoControl(handle, Native.IOCTL_STORAGE_MEDIA_REMOVAL, &allow, 1, null, 0, out _, 0);
            return Native.DeviceIoControl(handle, Native.IOCTL_STORAGE_EJECT_MEDIA, null, 0, null, 0, out _, 0)
                ? null
                : new Win32Exception(System.Runtime.InteropServices.Marshal.GetLastPInvokeError()).Message;
        }
        finally
        {
            Native.CloseHandle(handle);
        }
    }
}
