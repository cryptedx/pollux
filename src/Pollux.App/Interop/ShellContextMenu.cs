using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace Pollux.App.Interop;

/// <summary>
/// Shows the real Windows shell context menu (with 7-Zip, "Open with Code", Git, Send to, …) for files in one folder.
/// Uses source-generated COM interop so it works under Native AOT.
/// </summary>
internal static unsafe partial class ShellContextMenu
{
    private const uint CMF_NORMAL = 0, CMF_EXPLORE = 0x4, CMF_CANRENAME = 0x10, CMF_EXTENDEDVERBS = 0x100;
    private const int FirstCommand = 1, LastCommand = 0x7FFF;
    private const uint WM_INITMENUPOPUP = 0x0117, WM_DRAWITEM = 0x002B, WM_MEASUREITEM = 0x002C, WM_MENUCHAR = 0x0120, WM_MENUSELECT = 0x011F;

    private static readonly StrategyBasedComWrappers Wrappers = new();
    private static IContextMenu2? _menu2;
    private static IContextMenu3? _menu3;
    private static nint _host;

    /// <summary>Shows the menu at screen coordinates. Returns true if a command was invoked.</summary>
    public static bool Show(nint owner, IReadOnlyList<string> paths, int x, int y, bool extended)
    {
        if (paths.Count == 0) return false;
        var pidls = new List<nint>();
        nint parentPtr = 0, menuPtr = 0, hmenu = 0;
        try
        {
            foreach (var p in paths)
            {
                if (SHParseDisplayName(p, 0, out var pidl, 0, out _) != 0 || pidl == 0) return false;
                pidls.Add(pidl);
            }

            var iidFolder = typeof(IShellFolder).GUID;
            if (SHBindToParent(pidls[0], &iidFolder, out parentPtr, out _) != 0) return false;
            var folder = (IShellFolder)Wrappers.GetOrCreateObjectForComInstance(parentPtr, CreateObjectFlags.None);

            var children = stackalloc nint[pidls.Count];
            for (var i = 0; i < pidls.Count; i++) children[i] = ILFindLastID(pidls[i]);
            var iidMenu = typeof(IContextMenu).GUID;
            if (folder.GetUIObjectOf(owner, (uint)pidls.Count, children, &iidMenu, 0, out menuPtr) != 0) return false;
            var menu = (IContextMenu)Wrappers.GetOrCreateObjectForComInstance(menuPtr, CreateObjectFlags.None);
            _menu2 = menu as IContextMenu2;
            _menu3 = menu as IContextMenu3;

            hmenu = Native.CreatePopupMenu();
            var flags = CMF_NORMAL | CMF_EXPLORE | CMF_CANRENAME | (extended ? CMF_EXTENDEDVERBS : 0);
            if (menu.QueryContextMenu(hmenu, 0, FirstCommand, LastCommand, flags) < 0) return false;

            var host = EnsureHost();
            Native.SetForegroundWindow(host);
            var cmd = Native.TrackPopupMenuEx(hmenu, Native.TPM_RETURNCMD | Native.TPM_RIGHTBUTTON, x, y, host, 0);
            if (cmd < FirstCommand) return false;

            var info = new CMINVOKECOMMANDINFO
            {
                cbSize = (uint)sizeof(CMINVOKECOMMANDINFO),
                hwnd = owner,
                lpVerb = cmd - FirstCommand,
                nShow = 1,
            };
            return menu.InvokeCommand(&info) == 0;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            Services.CrashLog.Write(ex);
            return false;
        }
        finally
        {
            _menu2 = null;
            _menu3 = null;
            if (hmenu != 0) Native.DestroyMenu(hmenu);
            if (menuPtr != 0) Marshal.Release(menuPtr);
            if (parentPtr != 0) Marshal.Release(parentPtr);
            foreach (var p in pidls) Marshal.FreeCoTaskMem(p);
        }
    }

    private static nint EnsureHost()
    {
        if (_host != 0) return _host;
        const string className = "PolluxShellMenuHost";
        fixed (char* cls = className)
        {
            var wc = new Native.WNDCLASSEXW
            {
                cbSize = (uint)sizeof(Native.WNDCLASSEXW),
                lpfnWndProc = &HostProc,
                hInstance = Native.GetModuleHandleW(null),
                lpszClassName = cls,
            };
            Native.RegisterClassExW(&wc);
        }

        _host = Native.CreateWindowExW(0, className, "", 0, 0, 0, 0, 0, 0, 0, Native.GetModuleHandleW(null), 0);
        return _host;
    }

    /// <summary>Forwards owner-draw and submenu messages so "Send to", "Open with" and icons work.</summary>
    [UnmanagedCallersOnly]
    private static nint HostProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        if (msg is WM_INITMENUPOPUP or WM_DRAWITEM or WM_MEASUREITEM or WM_MENUCHAR or WM_MENUSELECT)
        {
            try
            {
                if (_menu3 is not null)
                {
                    nint result = 0;
                    if (_menu3.HandleMenuMsg2(msg, wParam, lParam, &result) == 0) return result;
                }
                else if (_menu2 is not null && _menu2.HandleMenuMsg(msg, wParam, lParam) == 0)
                {
                    return 0;
                }
            }
            catch (COMException)
            {
            }
        }

        return Native.DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct CMINVOKECOMMANDINFO
    {
        public uint cbSize;
        public uint fMask;
        public nint hwnd;
        public nint lpVerb;
        public nint lpParameters;
        public nint lpDirectory;
        public int nShow;
        public uint dwHotKey;
        public nint hIcon;
    }

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SHParseDisplayName(string name, nint bindCtx, out nint pidl, uint sfgaoIn, out uint sfgaoOut);

    [LibraryImport("shell32.dll")]
    private static partial int SHBindToParent(nint pidl, Guid* riid, out nint ppv, out nint pidlLast);

    [LibraryImport("shell32.dll")]
    private static partial nint ILFindLastID(nint pidl);

    [GeneratedComInterface]
    [Guid("000214E6-0000-0000-C000-000000000046")]
    internal partial interface IShellFolder
    {
        [PreserveSig] int ParseDisplayName(nint hwnd, nint bc, nint name, nint eaten, nint pidl, nint attributes);
        [PreserveSig] int EnumObjects(nint hwnd, uint flags, nint enumList);
        [PreserveSig] int BindToObject(nint pidl, nint bc, Guid* riid, nint ppv);
        [PreserveSig] int BindToStorage(nint pidl, nint bc, Guid* riid, nint ppv);
        [PreserveSig] int CompareIDs(nint lParam, nint pidl1, nint pidl2);
        [PreserveSig] int CreateViewObject(nint hwnd, Guid* riid, nint ppv);
        [PreserveSig] int GetAttributesOf(uint cidl, nint apidl, nint attributes);
        [PreserveSig] int GetUIObjectOf(nint hwnd, uint cidl, nint* apidl, Guid* riid, nint reserved, out nint ppv);
        [PreserveSig] int GetDisplayNameOf(nint pidl, uint flags, nint name);
        [PreserveSig] int SetNameOf(nint hwnd, nint pidl, nint name, uint flags, nint pidlOut);
    }

    [GeneratedComInterface]
    [Guid("000214E4-0000-0000-C000-000000000046")]
    internal partial interface IContextMenu
    {
        [PreserveSig] int QueryContextMenu(nint hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint flags);
        [PreserveSig] int InvokeCommand(CMINVOKECOMMANDINFO* info);
        [PreserveSig] int GetCommandString(nuint idCmd, uint type, nint reserved, nint name, uint cchMax);
    }

    [GeneratedComInterface]
    [Guid("000214F4-0000-0000-C000-000000000046")]
    internal partial interface IContextMenu2 : IContextMenu
    {
        [PreserveSig] int HandleMenuMsg(uint msg, nint wParam, nint lParam);
    }

    [GeneratedComInterface]
    [Guid("BCFCE0A0-EC17-11D0-8D10-00A0C90F2719")]
    internal partial interface IContextMenu3 : IContextMenu2
    {
        [PreserveSig] int HandleMenuMsg2(uint msg, nint wParam, nint lParam, nint* result);
    }
}
