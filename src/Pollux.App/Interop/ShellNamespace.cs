using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Pollux.Core.Files;
using Pollux.Core.Navigation;

namespace Pollux.App.Interop;

/// <summary>
/// Any shell namespace folder by parsing name (This PC, Network, Control Panel, Libraries, phones over MTP): lists it,
/// names its ancestors, runs an item's default verb and copies items out with Windows' own copy dialog.
/// Source-generated COM interop like <see cref="RecycleBin"/> (Native AOT, ADR 0002). Every call blocks and needs an STA
/// thread; the Async methods bring their own.
/// </summary>
internal sealed partial class ShellNamespace : IShellNamespace
{
    public static ShellNamespace Instance { get; } = new();

    private const uint SHCONTF_FOLDERS = 0x20, SHCONTF_NONFOLDERS = 0x40, SHCONTF_INCLUDEHIDDEN = 0x80;
    private const uint SIGDN_NORMALDISPLAY = 0, SIGDN_DESKTOPABSOLUTEPARSING = 0x80028000, SIGDN_FILESYSPATH = 0x80058000;
    private const uint SFGAO_READONLY = 0x40000, SFGAO_HIDDEN = 0x80000, SFGAO_STREAM = 0x400000, SFGAO_FOLDER = 0x20000000, SFGAO_FILESYSTEM = 0x40000000;
    private const uint SEE_MASK_INVOKEIDLIST = 0xC, SEE_MASK_NOASYNC = 0x100;
    private const uint CLSCTX_INPROC_SERVER = 1;
    private const uint FOF_ALLOWUNDO = 0x40, FOF_NOCONFIRMMKDIR = 0x200;
    private static readonly Guid FmtidStorage = new("B725F130-47EF-101A-A5F1-02608C9EEBAC");
    private static readonly Guid ClsidFileOperation = new("3AD05575-8857-4850-9277-11B85BDB8E09");

    private static readonly StrategyBasedComWrappers Wrappers = new();

    private ShellNamespace()
    {
    }

    public IEnumerable<FileEntry> Enumerate(string parsingName, CancellationToken cancellationToken)
    {
        // An iterator cannot hold pointers, so the COM work is in the helpers below and only handles pass through here.
        var (pidl, folderPtr, enumPtr) = Open(parsingName);
        RecycleBin.IEnumIDList? items = null;
        try
        {
            if (enumPtr == 0) yield break;
            // Unique instances released in the finally blocks: this STA thread ends after the listing, so no finalizer may release them later.
            items = (RecycleBin.IEnumIDList)Wrappers.GetOrCreateObjectForComInstance(enumPtr, CreateObjectFlags.UniqueInstance);
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var child = Next(items);
                if (child == 0) yield break;
                FileEntry? entry;
                try
                {
                    entry = Describe(pidl, child);
                }
                finally
                {
                    Marshal.FreeCoTaskMem(child);
                }

                if (entry is not null) yield return entry;
            }
        }
        finally
        {
            if ((object?)items is ComObject com) com.FinalRelease();
            if (enumPtr != 0) Marshal.Release(enumPtr);
            if (folderPtr != 0) Marshal.Release(folderPtr);
            Marshal.FreeCoTaskMem(pidl);
        }
    }

    public unsafe IReadOnlyList<LocationSegment> Segments(string location)
    {
        var pidl = Parse(Locations.ParsingName(location));
        var result = new List<LocationSegment>();
        try
        {
            // Walk up to (not including) the desktop, whose ID list is empty.
            while (*(ushort*)pidl != 0)
            {
                var parsing = NameOf(pidl, SIGDN_DESKTOPABSOLUTEPARSING);
                if (parsing is null) break;
                var label = NameOf(pidl, SIGDN_NORMALDISPLAY) ?? parsing;
                result.Add(new LocationSegment(label, Locations.FromShell(parsing, NameOf(pidl, SIGDN_FILESYSPATH))));
                if (!ILRemoveLastID(pidl)) break;
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(pidl);
        }

        result.Reverse();
        if (result.Count > 0) result[^1] = result[^1] with { Location = location };
        return result;
    }

    public string? FileSystemPath(string location)
    {
        try
        {
            var pidl = Parse(Locations.ParsingName(location));
            try { return NameOf(pidl, SIGDN_FILESYSPATH); }
            finally { Marshal.FreeCoTaskMem(pidl); }
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>Runs the default verb of a shell item (a Control Panel applet, a file on a phone) as Explorer's double-click does; false when Windows refused.</summary>
    public static Task<bool> InvokeDefaultAsync(nint owner, string parsingName) => InvokeVerbAsync(owner, parsingName, null);

    /// <summary>Runs a verb ("properties") of a shell item, or its default verb when <paramref name="verb"/> is null; false when Windows refused.</summary>
    public static unsafe Task<bool> InvokeVerbAsync(nint owner, string parsingName, string? verb) => StaThread.Run(() =>
    {
        nint pidl;
        try
        {
            pidl = Parse(parsingName);
        }
        catch (IOException)
        {
            return false;
        }

        var verbPtr = verb is null ? 0 : Marshal.StringToHGlobalUni(verb);
        try
        {
            var info = new SHELLEXECUTEINFOW
            {
                cbSize = (uint)sizeof(SHELLEXECUTEINFOW),
                fMask = SEE_MASK_INVOKEIDLIST | SEE_MASK_NOASYNC,
                hwnd = owner,
                lpVerb = verbPtr,
                nShow = 1,
                lpIDList = pidl,
            };
            return ShellExecuteExW(&info);
        }
        finally
        {
            if (verbPtr != 0) Marshal.FreeHGlobal(verbPtr);
            Marshal.FreeCoTaskMem(pidl);
        }
    });

    /// <summary>
    /// Copies shell items (locations from a listing, filesystem or not) into a folder with IFileOperation, so Windows
    /// shows its progress and conflict dialogs and talks to the phone. False when nothing ran or the user cancelled.
    /// </summary>
    public static unsafe Task<bool> CopyToFolderAsync(nint owner, IReadOnlyList<string> locations, string targetFolder) => StaThread.Run(() =>
    {
        if (locations.Count == 0) return false;
        var pidls = new List<nint>(locations.Count);
        nint operationPtr = 0, arrayPtr = 0, targetPtr = 0;
        IFileOperation? operation = null;
        try
        {
            foreach (var location in locations) pidls.Add(Parse(Locations.ParsingName(location)));
            var iidItem = typeof(IShellItem).GUID;
            Check(SHCreateItemFromParsingName(targetFolder, 0, &iidItem, out targetPtr), targetFolder);
            fixed (nint* list = pidls.ToArray())
            {
                Check(SHCreateShellItemArrayFromIDLists((uint)pidls.Count, list, out arrayPtr), "selection");
            }

            var clsid = ClsidFileOperation;
            var iidOperation = typeof(IFileOperation).GUID;
            Check(CoCreateInstance(&clsid, 0, CLSCTX_INPROC_SERVER, &iidOperation, out operationPtr), "IFileOperation");
            operation = (IFileOperation)Wrappers.GetOrCreateObjectForComInstance(operationPtr, CreateObjectFlags.UniqueInstance);
            if (owner != 0) _ = operation.SetOwnerWindow(owner);
            _ = operation.SetOperationFlags(FOF_ALLOWUNDO | FOF_NOCONFIRMMKDIR);
            if (operation.CopyItems(arrayPtr, targetPtr) < 0 || operation.PerformOperations() < 0) return false;
            return operation.GetAnyOperationsAborted(out var aborted) >= 0 && aborted == 0;
        }
        catch (Exception ex) when (ex is IOException or COMException or InvalidCastException)
        {
            Services.CrashLog.Write(ex);
            return false;
        }
        finally
        {
            if ((object?)operation is ComObject com) com.FinalRelease();
            if (operationPtr != 0) Marshal.Release(operationPtr);
            if (arrayPtr != 0) Marshal.Release(arrayPtr);
            if (targetPtr != 0) Marshal.Release(targetPtr);
            foreach (var p in pidls) Marshal.FreeCoTaskMem(p);
        }
    });

    /// <summary>The real shell context menu for items of a shell listing (by their locations); see <see cref="ShellContextMenu.Show"/>.</summary>
    public static bool ShowContextMenu(nint owner, IReadOnlyList<string> locations, int x, int y, bool extended) =>
        ShellContextMenu.Show(owner, [.. locations.Select(Locations.ParsingName)], x, y, extended);

    /// <summary>The folder's ID list, its IShellFolder and an enumerator of all its items, hidden ones included (0 when it is empty). The caller releases all three.</summary>
    private static unsafe (nint Pidl, nint Folder, nint Enum) Open(string parsingName)
    {
        var pidl = Parse(parsingName);
        nint folderPtr = 0, enumPtr = 0;
        try
        {
            var iidFolder = typeof(ShellContextMenu.IShellFolder).GUID;
            Check(SHBindToObject(0, pidl, 0, &iidFolder, out folderPtr), parsingName);
            var folder = (ShellContextMenu.IShellFolder)Wrappers.GetOrCreateObjectForComInstance(folderPtr, CreateObjectFlags.UniqueInstance);
            try
            {
                // S_FALSE and no enumerator: the folder is empty.
                Check(folder.EnumObjects(0, SHCONTF_FOLDERS | SHCONTF_NONFOLDERS | SHCONTF_INCLUDEHIDDEN, (nint)(&enumPtr)), parsingName);
            }
            finally
            {
                if ((object)folder is ComObject com) com.FinalRelease();
            }

            return (pidl, folderPtr, enumPtr);
        }
        catch
        {
            if (folderPtr != 0) Marshal.Release(folderPtr);
            Marshal.FreeCoTaskMem(pidl);
            throw;
        }
    }

    /// <summary>The next child ID list (the caller frees it), or 0 at the end.</summary>
    private static unsafe nint Next(RecycleBin.IEnumIDList items)
    {
        nint child = 0;
        uint fetched = 0;
        return items.Next(1, &child, &fetched) == 0 && fetched == 1 ? child : 0;
    }

    /// <summary>An absolute ID list for a parsing name (the caller frees it); <see cref="IOException"/> when the shell cannot resolve it.</summary>
    private static unsafe nint Parse(string parsingName)
    {
        var iidItem = typeof(IShellItem).GUID;
        Check(SHCreateItemFromParsingName(parsingName, 0, &iidItem, out var itemPtr), parsingName);
        try
        {
            Check(SHGetIDListFromObject(itemPtr, out var pidl), parsingName);
            return pidl;
        }
        finally
        {
            Marshal.Release(itemPtr);
        }
    }

    /// <summary>One child as a <see cref="FileEntry"/>: shell name, location (filesystem path if any), folder flag, size and times when the shell has them.</summary>
    private static unsafe FileEntry? Describe(nint folderPidl, nint child)
    {
        var absolute = ILCombine(folderPidl, child);
        if (absolute == 0) return null;
        nint itemPtr = 0;
        try
        {
            var iid = typeof(IShellItem2).GUID;
            if (SHCreateItemFromIDList(absolute, &iid, out itemPtr) != 0 || itemPtr == 0) return null;
            var item = (IShellItem2)Wrappers.GetOrCreateObjectForComInstance(itemPtr, CreateObjectFlags.UniqueInstance);
            try
            {
                var parsing = NameOf(absolute, SIGDN_DESKTOPABSOLUTEPARSING);
                if (parsing is null) return null;
                var name = NameOf(absolute, SIGDN_NORMALDISPLAY) ?? parsing;
                if (item.GetAttributes(SFGAO_FOLDER | SFGAO_FILESYSTEM | SFGAO_STREAM | SFGAO_HIDDEN | SFGAO_READONLY, out var sfgao) < 0) sfgao = 0;
                var fileSystem = (sfgao & SFGAO_FILESYSTEM) != 0;
                var path = fileSystem ? NameOf(absolute, SIGDN_FILESYSPATH) : null;
                // A library is backed by a .library-ms file but browsed as a folder.
                if (path is not null && path.EndsWith(".library-ms", StringComparison.OrdinalIgnoreCase)) path = null;
                var isDirectory = (sfgao & SFGAO_FOLDER) != 0 && ((sfgao & SFGAO_STREAM) == 0 || path is null);

                var attributes = (FileAttributes)0;
                var key = new PROPERTYKEY { fmtid = FmtidStorage, pid = 13 };
                if (path is not null && item.GetUInt32(&key, out var fileAttributes) == 0) attributes = (FileAttributes)fileAttributes;
                else attributes = (isDirectory ? FileAttributes.Directory : FileAttributes.Normal) | ((sfgao & SFGAO_READONLY) != 0 || path is null ? FileAttributes.ReadOnly : 0);
                if ((sfgao & SFGAO_HIDDEN) != 0) attributes |= FileAttributes.Hidden;

                key.pid = 12;
                var size = !isDirectory && item.GetUInt64(&key, out var bytes) == 0 ? (long)bytes : 0;
                key.pid = 14;
                var modified = item.GetFileTime(&key, out var written) == 0 ? DateTime.FromFileTime(written) : default;
                key.pid = 15;
                var created = item.GetFileTime(&key, out var born) == 0 ? DateTime.FromFileTime(born) : default;
                return new FileEntry(name, Locations.FromShell(parsing, path), isDirectory, size, modified, attributes, created);
            }
            finally
            {
                if ((object)item is ComObject com) com.FinalRelease();
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or ArgumentOutOfRangeException)
        {
            Services.CrashLog.Trace($"shell item skipped: 0x{ex.HResult:X8} {ex.Message}");
            return null;
        }
        finally
        {
            if (itemPtr != 0) Marshal.Release(itemPtr);
            Marshal.FreeCoTaskMem(absolute);
        }
    }

    private static string? NameOf(nint pidl, uint sigdn)
    {
        if (SHGetNameFromIDList(pidl, sigdn, out var name) != 0 || name == 0) return null;
        try { return Marshal.PtrToStringUni(name); }
        finally { Marshal.FreeCoTaskMem(name); }
    }

    private static void Check(int hr, string what)
    {
        if (hr < 0) throw new IOException($"Windows could not open {what} (0x{hr:X8}).", hr);
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PROPERTYKEY
    {
        public Guid fmtid;
        public uint pid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SHELLEXECUTEINFOW
    {
        public uint cbSize;
        public uint fMask;
        public nint hwnd;
        public nint lpVerb;
        public nint lpFile;
        public nint lpParameters;
        public nint lpDirectory;
        public int nShow;
        public nint hInstApp;
        public nint lpIDList;
        public nint lpClass;
        public nint hkeyClass;
        public uint dwHotKey;
        public nint hIconOrMonitor;
        public nint hProcess;
    }

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static unsafe partial int SHCreateItemFromParsingName(string path, nint bindCtx, Guid* riid, out nint ppv);

    [LibraryImport("shell32.dll")]
    private static unsafe partial int SHCreateItemFromIDList(nint pidl, Guid* riid, out nint ppv);

    [LibraryImport("shell32.dll")]
    private static partial int SHGetIDListFromObject(nint unknown, out nint pidl);

    [LibraryImport("shell32.dll")]
    private static unsafe partial int SHBindToObject(nint parent, nint pidl, nint bindCtx, Guid* riid, out nint ppv);

    [LibraryImport("shell32.dll")]
    private static unsafe partial int SHCreateShellItemArrayFromIDLists(uint count, nint* pidls, out nint array);

    [LibraryImport("shell32.dll")]
    private static partial nint ILCombine(nint pidl1, nint pidl2);

    [LibraryImport("shell32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ILRemoveLastID(nint pidl);

    [LibraryImport("shell32.dll")]
    private static partial int SHGetNameFromIDList(nint pidl, uint sigdn, out nint name);

    [LibraryImport("shell32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool ShellExecuteExW(SHELLEXECUTEINFOW* info);

    [LibraryImport("ole32.dll")]
    private static unsafe partial int CoCreateInstance(Guid* clsid, nint outer, uint context, Guid* riid, out nint ppv);

    [GeneratedComInterface]
    [Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
    internal unsafe partial interface IShellItem
    {
        [PreserveSig] int BindToHandler(nint bindCtx, Guid* handler, Guid* riid, out nint ppv);
        [PreserveSig] int GetParent(out nint parent);
        [PreserveSig] int GetDisplayName(uint sigdn, out nint name);
        [PreserveSig] int GetAttributes(uint mask, out uint attributes);
        [PreserveSig] int Compare(nint other, uint hint, out int order);
    }

    [GeneratedComInterface]
    [Guid("7E9FB0D3-919F-4307-AB2E-9B1860310C93")]
    internal unsafe partial interface IShellItem2 : IShellItem
    {
        [PreserveSig] int GetPropertyStore(int flags, Guid* riid, out nint ppv);
        [PreserveSig] int GetPropertyStoreWithCreateObject(int flags, nint createObject, Guid* riid, out nint ppv);
        [PreserveSig] int GetPropertyStoreForKeys(nint keys, uint count, int flags, Guid* riid, out nint ppv);
        [PreserveSig] int GetPropertyDescriptionList(PROPERTYKEY* key, Guid* riid, out nint ppv);
        [PreserveSig] int Update(nint bindCtx);
        [PreserveSig] int GetProperty(PROPERTYKEY* key, nint value);
        [PreserveSig] int GetCLSID(PROPERTYKEY* key, Guid* clsid);
        [PreserveSig] int GetFileTime(PROPERTYKEY* key, out long fileTime);
        [PreserveSig] int GetInt32(PROPERTYKEY* key, out int value);
        [PreserveSig] int GetString(PROPERTYKEY* key, out nint value);
        [PreserveSig] int GetUInt32(PROPERTYKEY* key, out uint value);
        [PreserveSig] int GetUInt64(PROPERTYKEY* key, out ulong value);
        [PreserveSig] int GetBool(PROPERTYKEY* key, out int value);
    }

    [GeneratedComInterface]
    [Guid("947AAB5F-0A5C-4C13-B4D6-4BF7836FC9F8")]
    internal partial interface IFileOperation
    {
        [PreserveSig] int Advise(nint sink, out uint cookie);
        [PreserveSig] int Unadvise(uint cookie);
        [PreserveSig] int SetOperationFlags(uint flags);
        [PreserveSig] int SetProgressMessage(nint message);
        [PreserveSig] int SetProgressDialog(nint dialog);
        [PreserveSig] int SetProperties(nint properties);
        [PreserveSig] int SetOwnerWindow(nint owner);
        [PreserveSig] int ApplyPropertiesToItem(nint item);
        [PreserveSig] int ApplyPropertiesToItems(nint items);
        [PreserveSig] int RenameItem(nint item, nint newName, nint sink);
        [PreserveSig] int RenameItems(nint items, nint newName);
        [PreserveSig] int MoveItem(nint item, nint destination, nint newName, nint sink);
        [PreserveSig] int MoveItems(nint items, nint destination);
        [PreserveSig] int CopyItem(nint item, nint destination, nint copyName, nint sink);
        [PreserveSig] int CopyItems(nint items, nint destination);
        [PreserveSig] int DeleteItem(nint item, nint sink);
        [PreserveSig] int DeleteItems(nint items);
        [PreserveSig] int NewItem(nint destination, uint attributes, nint name, nint template, nint sink);
        [PreserveSig] int PerformOperations();
        [PreserveSig] int GetAnyOperationsAborted(out int aborted);
    }
}
