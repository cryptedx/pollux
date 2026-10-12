using System.ComponentModel;
using System.Runtime.InteropServices;
using Pollux.Core.Navigation;

namespace Pollux.App.Interop;

/// <summary>
/// The shares of a server (NetShareEnum, level 1), for a bare "\\server" location. Only runs when the user navigates
/// there (ADR 0010); Windows' SMB client makes the connection with the user's own credentials. Blocking.
/// </summary>
internal sealed unsafe partial class NetShares : IServerShares
{
    public static NetShares Instance { get; } = new();

    private const uint MaxPreferredLength = 0xFFFFFFFF;
    private const uint ErrorAccessDenied = 5, ErrorBadNetPath = 53, ErrorLogonFailure = 1326, ErrorMoreData = 234;
    private const uint ResourceTypeAny = 0, ConnectPrompt = 0x10;

    private NetShares()
    {
    }

    public IReadOnlyList<NetShare> List(string server)
    {
        var shares = new List<NetShare>();
        uint resume = 0;
        uint rc;
        do
        {
            rc = NetShareEnum(@"\\" + server, 1, out var buffer, MaxPreferredLength, out var read, out _, ref resume);
            try
            {
                if (rc is not 0 and not ErrorMoreData) throw Failure(server, rc);
                var info = (SHARE_INFO_1*)buffer;
                for (var i = 0; i < read; i++)
                {
                    shares.Add(new NetShare(new string(info[i].shi1_netname), info[i].shi1_type, info[i].shi1_remark is null ? null : new string(info[i].shi1_remark)));
                }
            }
            finally
            {
                if (buffer != 0) _ = NetApiBufferFree(buffer);
            }
        }
        while (rc == ErrorMoreData);

        return shares;
    }

    /// <summary>
    /// Connects to the server's IPC$ with Windows' own credential prompt, for when listing it was denied; Pollux never
    /// sees the password. Null on success, "" when the user cancelled, a message otherwise. Blocking, so off the UI thread.
    /// </summary>
    public static string? ConnectInteractive(nint owner, string server)
    {
        fixed (char* remote = @"\\" + server + @"\IPC$")
        {
            var resource = new Native.NETRESOURCEW { dwType = ResourceTypeAny, lpRemoteName = remote };
            var rc = Native.WNetAddConnection3W(owner, &resource, null, null, Native.CONNECT_INTERACTIVE | ConnectPrompt);
            return rc switch
            {
                Native.NO_ERROR => null,
                Native.ERROR_CANCELLED => "",
                _ => new Win32Exception((int)rc).Message,
            };
        }
    }

    /// <summary>True for a listing error that a sign-in (<see cref="ConnectInteractive"/>) can fix.</summary>
    public static bool NeedsSignIn(Exception error) => error is UnauthorizedAccessException;

    private static Exception Failure(string server, uint rc) => rc switch
    {
        ErrorAccessDenied or ErrorLogonFailure => new UnauthorizedAccessException($@"Access to \\{server} was denied: {new Win32Exception((int)rc).Message}"),
        ErrorBadNetPath => new IOException($@"\\{server} was not found on the network."),
        _ => new IOException($@"\\{server}: {new Win32Exception((int)rc).Message}"),
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct SHARE_INFO_1
    {
        public char* shi1_netname;
        public uint shi1_type;
        public char* shi1_remark;
    }

    [LibraryImport("netapi32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint NetShareEnum(string serverName, uint level, out nint buffer, uint preferredMaximumLength, out uint entriesRead, out uint totalEntries, ref uint resumeHandle);

    [LibraryImport("netapi32.dll")]
    private static partial uint NetApiBufferFree(nint buffer);
}
