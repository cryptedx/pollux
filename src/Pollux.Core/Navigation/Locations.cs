using Pollux.Core.Archives;

namespace Pollux.Core.Navigation;

/// <summary>What a Tab's location is, which decides how it is listed and what may be done there.</summary>
public enum LocationKind
{
    /// <summary><see cref="WinPath.Home"/>: no listing.</summary>
    Home,

    /// <summary><see cref="WinPath.RecycleBin"/>: listed by the bin's own interop.</summary>
    RecycleBin,

    /// <summary>A filesystem folder: a drive path or a UNC share path ("\\server\share\dir").</summary>
    Folder,

    /// <summary>A .zip file or a folder inside one ("C:\a.zip\docs"), browsed read-only.</summary>
    Zip,

    /// <summary>A server alone ("\\server"): its disk shares.</summary>
    Server,

    /// <summary>A shell namespace folder without a filesystem path (This PC, Network, Control Panel, Libraries, phones).</summary>
    Shell,
}

/// <summary>What a location allows.</summary>
/// <param name="CanModify">Paste, new, rename, delete, move and drop into it.</param>
/// <param name="CanOpenItems">Open or enter its items.</param>
/// <param name="CanCopyOut">Copy its items to a folder (extracting from a zip, the shell's copy for a phone).</param>
/// <param name="IsFileSystem">A real folder: watching, git, folder sizes, search, a terminal there.</param>
public sealed record LocationAccess(bool CanModify, bool CanOpenItems, bool CanCopyOut, bool IsFileSystem);

/// <summary>One breadcrumb: its label and the location it navigates to.</summary>
public sealed record LocationSegment(string Label, string Location);

/// <summary>
/// Locations beyond filesystem folders, as written in a Tab: the pseudo locations (<c>pollux:…</c>), shell namespace
/// parsing names (<c>::{CLSID}…</c>, <c>shell:Libraries</c>, or <c>pollux:shell:</c> + any other parsing name), a
/// server alone (<c>\\server</c>) and paths through a .zip (<c>C:\a.zip\docs</c>). Pure string logic except
/// <see cref="Classify"/>, which asks whether a path's ".zip" segment is a file.
/// </summary>
public static class Locations
{
    /// <summary>Prefix for a shell parsing name that is neither a CLSID path nor a <c>shell:</c> moniker.</summary>
    public const string ShellPrefix = "pollux:shell:";

    /// <summary>The parsing names behind <see cref="WinPath.ThisPc"/> and <see cref="WinPath.Network"/>.</summary>
    public const string ThisPcParsingName = "::{20D04FE0-3AEA-1069-A2D8-08002B30309D}";
    public const string NetworkParsingName = "::{F02C1A0D-BE21-4350-88B0-7367FC96EF3C}";

    /// <summary>What the shell reports as the parsing name of <see cref="WinPath.Libraries"/>.</summary>
    public const string LibrariesParsingName = "::{031E4825-7B94-4DC3-B131-E946B44C8DD5}";

    private static readonly LocationAccess Nothing = new(false, false, false, false);
    private static readonly LocationAccess Everything = new(true, true, true, true);
    private static readonly LocationAccess ReadOnlyCopyable = new(false, true, true, false);
    private static readonly LocationAccess ReadOnlyBrowse = new(false, true, false, false);

    /// <summary>
    /// The kind of <paramref name="location"/>. A path with a ".zip" segment is a <see cref="LocationKind.Zip"/> only when that
    /// segment is a file (a folder may be called "x.zip"); <paramref name="isFile"/> defaults to <see cref="File.Exists"/>.
    /// </summary>
    public static LocationKind Classify(string location, Func<string, bool>? isFile = null)
    {
        if (location == WinPath.Home) return LocationKind.Home;
        if (location == WinPath.RecycleBin) return LocationKind.RecycleBin;
        if (IsShell(location)) return LocationKind.Shell;
        if (ServerOf(location) is not null) return LocationKind.Server;
        if (ZipFolder.TryParse(location, out var archive, out _) && (isFile ?? File.Exists)(archive)) return LocationKind.Zip;
        return LocationKind.Folder;
    }

    public static LocationAccess Access(LocationKind kind) => kind switch
    {
        LocationKind.Folder => Everything,
        LocationKind.Zip or LocationKind.Shell => ReadOnlyCopyable,
        LocationKind.Server => ReadOnlyBrowse,
        _ => Nothing,
    };

    /// <summary>A shell namespace location: <see cref="WinPath.ThisPc"/>, <see cref="WinPath.Network"/>, <c>::{…}</c>, <c>shell:…</c> or <see cref="ShellPrefix"/>.</summary>
    public static bool IsShell(string location) =>
        location is WinPath.ThisPc or WinPath.Network
        || location.StartsWith("::", StringComparison.Ordinal)
        || location.StartsWith("shell:", StringComparison.OrdinalIgnoreCase)
        || location.StartsWith(ShellPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>The name the shell parses for a shell location (SHCreateItemFromParsingName); the location itself for anything else.</summary>
    public static string ParsingName(string location) => location switch
    {
        WinPath.ThisPc => ThisPcParsingName,
        WinPath.Network => NetworkParsingName,
        _ when location.StartsWith(ShellPrefix, StringComparison.OrdinalIgnoreCase) => location[ShellPrefix.Length..],
        _ => location,
    };

    /// <summary>
    /// How a shell item is written as a location: its filesystem path if it has one, a server or CLSID path as is,
    /// This PC and Network by their short names, anything else behind <see cref="ShellPrefix"/>.
    /// </summary>
    public static string FromShell(string parsingName, string? fileSystemPath = null)
    {
        if (!string.IsNullOrEmpty(fileSystemPath)) return WinPath.Normalize(fileSystemPath);
        if (parsingName.Equals(ThisPcParsingName, StringComparison.OrdinalIgnoreCase)) return WinPath.ThisPc;
        if (parsingName.Equals(NetworkParsingName, StringComparison.OrdinalIgnoreCase)) return WinPath.Network;
        if (parsingName.Equals(LibrariesParsingName, StringComparison.OrdinalIgnoreCase)) return WinPath.Libraries;
        if (parsingName.Equals(WinPath.ControlPanel, StringComparison.OrdinalIgnoreCase)) return WinPath.ControlPanel;
        if (parsingName.StartsWith("::", StringComparison.Ordinal) || parsingName.StartsWith("shell:", StringComparison.OrdinalIgnoreCase)) return parsingName;
        if (ServerOf(parsingName) is not null) return parsingName;
        return ShellPrefix + parsingName;
    }

    /// <summary>The server name of a bare "\\server" (also "\\server\"); null for shares, device paths and everything else.</summary>
    public static string? ServerOf(string location)
    {
        var p = location.Replace('/', '\\');
        if (!p.StartsWith(@"\\", StringComparison.Ordinal)) return null;
        var name = p[2..].TrimEnd('\\');
        return name.Length == 0 || name.Contains('\\', StringComparison.Ordinal) || name is "?" or "." ? null : name;
    }

    /// <summary>The server of a bare server or of any UNC path under it ("\\server\share\dir" gives "server"); null otherwise.</summary>
    public static string? ServerOfPath(string location)
    {
        var p = location.Replace('/', '\\');
        if (!p.StartsWith(@"\\", StringComparison.Ordinal)) return null;
        var end = p.IndexOf('\\', 2);
        return ServerOf(end < 0 ? p : p[..end]);
    }

    /// <summary>The Tab title; null for a shell location whose name only the shell knows.</summary>
    public static string? Title(string location) => location switch
    {
        WinPath.Home => "Home",
        WinPath.RecycleBin => "Recycle Bin",
        WinPath.ThisPc => "This PC",
        WinPath.Network => "Network",
        WinPath.ControlPanel => "Control Panel",
        WinPath.Libraries => "Libraries",
        _ when ServerOf(location) is { } server => server,
        _ when IsShell(location) => null,
        _ => WinPath.Name(location),
    };

    /// <summary>
    /// Where Up goes: a drive root to This PC, a share to its server, a server to Network, This PC and Network to Home.
    /// Null for Home and for shell locations the shell has to resolve (<c>IShellNamespace.Segments</c>).
    /// </summary>
    public static string? Parent(string location, Func<string, bool>? isFile = null)
    {
        switch (Classify(location, isFile))
        {
            case LocationKind.Home:
                return null;
            case LocationKind.RecycleBin:
                return WinPath.Home;
            case LocationKind.Server:
                return WinPath.Network;
            case LocationKind.Shell:
                return location is WinPath.ThisPc or WinPath.Network or WinPath.ControlPanel or WinPath.Libraries ? WinPath.Home : null;
        }

        var p = WinPath.Normalize(location);
        if (WinPath.Parent(p) is { } parent) return parent;
        if (WinPath.IsUnc(p)) return p.IndexOf('\\', 2) is var i and > 2 && ServerOf(p[..i]) is not null ? p[..i] : WinPath.Home;
        return WinPath.IsDriveRooted(p) ? WinPath.ThisPc : WinPath.Home;
    }

    /// <summary>
    /// The breadcrumbs, root first. A share gets its server before it; a zip path is a path. A shell location the
    /// shell has to name returns a single crumb labelled <paramref name="shellTitle"/> (or its parsing name).
    /// </summary>
    public static IReadOnlyList<LocationSegment> Segments(string location, string? shellTitle = null)
    {
        if (Title(location) is { } title && (Classify(location, static _ => false) is not LocationKind.Folder))
        {
            return [new LocationSegment(location == WinPath.Home ? "home" : title, location)];
        }

        if (IsShell(location)) return [new LocationSegment(shellTitle ?? ParsingName(location), location)];

        var segments = WinPath.Segments(location);
        var result = new List<LocationSegment>(segments.Count + 1);
        if (WinPath.IsUnc(segments[0]) && ServerOf(segments[0]) is null)
        {
            var server = segments[0][..segments[0].IndexOf('\\', 2)];
            result.Add(new LocationSegment(server[2..], server));
            result.Add(new LocationSegment(WinPath.Name(segments[0]), segments[0]));
        }
        else
        {
            result.Add(new LocationSegment(segments[0].TrimEnd('\\'), segments[0]));
        }

        for (var i = 1; i < segments.Count; i++) result.Add(new LocationSegment(WinPath.Name(segments[i]), segments[i]));
        return result;
    }

    /// <summary>
    /// <see cref="WinPath.Normalize"/> for paths (folders, zip paths, servers); pseudo and shell locations stay exactly as
    /// written, since a phone's parsing name holds "\\?\" and must not lose a backslash.
    /// </summary>
    public static string Normalize(string location)
    {
        var t = location.Trim();
        return t.StartsWith("pollux:", StringComparison.OrdinalIgnoreCase) || IsShell(t) ? t : WinPath.Normalize(t);
    }

    /// <summary>
    /// Turns what the user typed into a location: a well-known name, a shell location or bare server as written, anything
    /// else through <see cref="WinPath.Resolve"/>. Whether it exists is the caller's question.
    /// </summary>
    public static string ResolveTyped(string input, string currentDirectory, Func<string, string?> getEnvironmentVariable)
    {
        var t = input.Trim().Trim('"').Trim();
        if (WinPath.WellKnown(t) is { } known) return known;
        if (IsShell(t)) return FromShell(ParsingName(t));
        if (ServerOf(t) is { } server) return @"\\" + server;
        return WinPath.Resolve(t, currentDirectory, getEnvironmentVariable);
    }
}
