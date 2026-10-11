using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Win32;
using Pollux.Core.Navigation;

namespace Pollux.Core.Copilot.Guards;

/// <summary>
/// Places on this machine whose content comes from elsewhere (tests pass a fake): drives mapped to a network share, and
/// folders a cloud-sync client keeps in sync with a service others can write to (OneDrive, SharePoint libraries). Reading
/// from them makes the Chat a Tainted Chat (SECURITY.md S-12). UNC paths and the OneDrive folders named by the
/// <c>OneDrive*</c> environment variables are recognised without it.
/// </summary>
public interface IRemotePlaces
{
    /// <summary>Whether <paramref name="path"/> (absolute) lies on a drive letter mapped to a network share.</summary>
    bool IsNetworkDrive(string path);

    /// <summary>The roots (absolute folders) of every cloud-synced folder of the current user.</summary>
    IReadOnlyList<string> SyncRoots();
}

/// <summary>
/// The real machine: network drives by their drive type (unknown counts as network), sync roots from the OneDrive client's mount points
/// (<c>HKCU\Software\SyncEngines\Providers\OneDrive</c>) and the Windows sync root registrations of the current user
/// (<c>SyncRootManager</c>, any cloud-files provider). Nothing on other systems.
/// </summary>
public sealed class SystemRemotePlaces : IRemotePlaces
{
    /// <summary>Sync roots are read again after this long (a newly synced library counts without restarting Pollux).</summary>
    private static readonly TimeSpan SyncRootsLifetime = TimeSpan.FromSeconds(30);

    public static SystemRemotePlaces Instance { get; } = new();

    private readonly Lock _gate = new();
    private IReadOnlyList<string> _syncRoots = [];
    private long _syncRootsRead = long.MinValue;

    /// <summary>The same check as git trust (<see cref="NetworkDrive.IsNetwork(string)"/>): an unknown or unreadable drive taints (fail closed).</summary>
    public bool IsNetworkDrive(string path) => NetworkDrive.IsNetwork(path);

    public IReadOnlyList<string> SyncRoots()
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        lock (_gate)
        {
            var now = Environment.TickCount64;
            if (_syncRootsRead == long.MinValue || now - _syncRootsRead > SyncRootsLifetime.TotalMilliseconds)
            {
                _syncRoots = ReadSyncRoots();
                _syncRootsRead = now;
            }

            return _syncRoots;
        }
    }

    [SupportedOSPlatform("windows")]
    private static List<string> ReadSyncRoots()
    {
        var roots = new List<string>();
        try
        {
            using (var providers = Registry.CurrentUser.OpenSubKey(@"Software\SyncEngines\Providers\OneDrive"))
            {
                foreach (var name in providers?.GetSubKeyNames() ?? [])
                {
                    using var provider = providers!.OpenSubKey(name);
                    if (provider?.GetValue("MountPoint") is string mount) roots.Add(mount);
                }
            }

            var sid = WindowsIdentity.GetCurrent().User?.Value;
            using var manager = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\SyncRootManager");
            foreach (var name in manager?.GetSubKeyNames() ?? [])
            {
                using var user = manager!.OpenSubKey(name + @"\UserSyncRoots");
                if (user is null) continue;
                foreach (var value in user.GetValueNames())
                {
                    if ((sid is null || value.Equals(sid, StringComparison.OrdinalIgnoreCase)) && user.GetValue(value) is string root) roots.Add(root);
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // What could be read counts; the OneDrive* environment variables still cover the OneDrive folders.
        }

        return [.. roots.Where(r => !string.IsNullOrWhiteSpace(r)).Select(WinPath.Normalize).Distinct(StringComparer.OrdinalIgnoreCase)];
    }
}
