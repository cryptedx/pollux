using Pollux.Core.Files;

namespace Pollux.Core.Navigation;

/// <summary>One share of a server as NetShareEnum reports it (SHARE_INFO_1).</summary>
public sealed record NetShare(string Name, uint Type, string? Remark);

/// <summary>Lists the shares of a server (netapi32 on Windows). Blocking; throws <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/>.</summary>
public interface IServerShares
{
    IReadOnlyList<NetShare> List(string server);
}

/// <summary>The folders a bare "\\server" shows: its disk shares. Admin shares (C$, ADMIN$) are hidden entries, so Show Hidden reveals them.</summary>
public static class ServerShares
{
    private const uint TypeMask = 0xFF, DiskTree = 0, Special = 0x80000000;

    public static IReadOnlyList<FileEntry> Entries(string server, IEnumerable<NetShare> shares)
    {
        var result = new List<FileEntry>();
        foreach (var s in shares)
        {
            if ((s.Type & TypeMask) != DiskTree || s.Name.Length == 0) continue;
            var hidden = (s.Type & Special) != 0 || s.Name.EndsWith('$');
            var attributes = FileAttributes.Directory | (hidden ? FileAttributes.Hidden : 0);
            result.Add(new FileEntry(s.Name, $@"\\{server}\{s.Name}", true, 0, default, attributes));
        }

        return result;
    }
}
