using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace Pollux.App.Interop;

/// <summary>Reads and writes .lnk shortcut files through IShellLinkW + IPersistFile. Source-generated COM interop, so it works under Native AOT.</summary>
internal static unsafe partial class ShellLink
{
    private const uint CLSCTX_INPROC_SERVER = 1;
    private static readonly StrategyBasedComWrappers Wrappers = new();
    private static readonly Guid ClsidShellLink = new("00021401-0000-0000-C000-000000000046");

    /// <summary>Creates a shortcut file at <paramref name="linkPath"/> pointing to <paramref name="target"/>. Returns an error text, or null on success.</summary>
    public static string? Create(string linkPath, string target, string workingDirectory)
    {
        var link = Open(out var ptr, out var error);
        try
        {
            if (link is null) return error;
            var hr = link.SetPath(target);
            if (hr < 0) return Failed(hr);
            if (workingDirectory.Length > 0) link.SetWorkingDirectory(workingDirectory);
            if (link is not IPersistFile file) return "Shortcut files are not supported here";
            hr = file.Save(linkPath, true);
            return hr < 0 ? Failed(hr) : null;
        }
        finally
        {
            Close(link, ptr);
        }
    }

    /// <summary>The path a shortcut points to, or null if it cannot be read (or points to a non-file item).</summary>
    public static string? ReadTarget(string linkPath)
    {
        var link = Open(out var ptr, out _);
        try
        {
            if (link is null || link is not IPersistFile file) return null;
            if (file.Load(linkPath, 0) < 0) return null;
            var buffer = stackalloc char[1024];
            buffer[0] = '\0';
            if (link.GetPath(buffer, 1024, 0, 0) < 0) return null;
            var text = new string(buffer);
            return text.Length == 0 ? null : text;
        }
        finally
        {
            Close(link, ptr);
        }
    }

    // A unique instance so FinalRelease frees the wrapper's reference right away, on this thread, instead of in a finalizer;
    // the reference CoCreateInstance returned is ours and is released separately.
    private static IShellLinkW? Open(out nint ptr, out string? error)
    {
        error = null;
        var clsid = ClsidShellLink;
        var iid = typeof(IShellLinkW).GUID;
        var hr = CoCreateInstance(&clsid, 0, CLSCTX_INPROC_SERVER, &iid, out ptr);
        if (hr < 0 || ptr == 0)
        {
            error = Failed(hr);
            ptr = 0;
            return null;
        }

        return (IShellLinkW)Wrappers.GetOrCreateObjectForComInstance(ptr, CreateObjectFlags.UniqueInstance);
    }

    private static void Close(IShellLinkW? link, nint ptr)
    {
        if ((object?)link is ComObject com) com.FinalRelease();
        if (ptr != 0) Marshal.Release(ptr);
    }

    private static string Failed(int hr) => $"Shortcut failed (0x{hr:X8})";

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(Guid* clsid, nint outer, uint context, Guid* iid, out nint ppv);

    [GeneratedComInterface]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    internal partial interface IShellLinkW
    {
        [PreserveSig] int GetPath(char* file, int cch, nint findData, uint flags);
        [PreserveSig] int GetIDList(nint* pidl);
        [PreserveSig] int SetIDList(nint pidl);
        [PreserveSig] int GetDescription(char* name, int cch);
        [PreserveSig] int SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        [PreserveSig] int GetWorkingDirectory(char* dir, int cch);
        [PreserveSig] int SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
        [PreserveSig] int GetArguments(char* args, int cch);
        [PreserveSig] int SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
        [PreserveSig] int GetHotkey(ushort* hotkey);
        [PreserveSig] int SetHotkey(ushort hotkey);
        [PreserveSig] int GetShowCmd(int* showCmd);
        [PreserveSig] int SetShowCmd(int showCmd);
        [PreserveSig] int GetIconLocation(char* path, int cch, int* icon);
        [PreserveSig] int SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int icon);
        [PreserveSig] int SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        [PreserveSig] int Resolve(nint hwnd, uint flags);
        [PreserveSig] int SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }

    [GeneratedComInterface]
    [Guid("0000010C-0000-0000-C000-000000000046")]
    internal partial interface IPersist
    {
        [PreserveSig] int GetClassID(Guid* classId);
    }

    [GeneratedComInterface]
    [Guid("0000010B-0000-0000-C000-000000000046")]
    internal partial interface IPersistFile : IPersist
    {
        [PreserveSig] int IsDirty();
        [PreserveSig] int Load([MarshalAs(UnmanagedType.LPWStr)] string fileName, uint mode);
        [PreserveSig] int Save([MarshalAs(UnmanagedType.LPWStr)] string? fileName, [MarshalAs(UnmanagedType.Bool)] bool remember);
        [PreserveSig] int SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string fileName);
        [PreserveSig] int GetCurFile(nint* fileName);
    }
}
