using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Pollux.Core.Recycle;

namespace Pollux.App.Interop;

/// <summary>
/// The Windows Recycle Bin through the shell folder FOLDERID_RecycleBinFolder: lists its entries across all drives and
/// restores or deletes them with the bin's own verbs, so Windows keeps its records ($I files) consistent.
/// Source-generated COM interop like <see cref="ShellContextMenu"/> (Native AOT, ADR 0002).
/// </summary>
internal static unsafe partial class RecycleBin
{
    private static readonly Guid FolderIdRecycleBin = new("B7534046-3ECB-4C18-BE4E-64CD4CB7D6AC");
    private const uint SHCONTF_FOLDERS = 0x20, SHCONTF_NONFOLDERS = 0x40, SHCONTF_INCLUDEHIDDEN = 0x80;
    private const uint SIGDN_NORMALDISPLAY = 0, SIGDN_FILESYSPATH = 0x80058000, SIGDN_DESKTOPABSOLUTEPARSING = 0x80028000;
    private const uint CMF_NORMAL = 0, CMIC_MASK_FLAG_NO_UI = 0x400;
    private const uint SHERB_NOCONFIRMATION = 0x1;

    private static readonly StrategyBasedComWrappers Wrappers = new();

    /// <summary>
    /// Every entry of the Recycle Bin. Runs on its own STA thread: shell folders expect one, and a full bin takes a
    /// while (each entry's $I record is read for its original path, size and deletion time).
    /// </summary>
    public static Task<List<RecycledItem>> ListAsync() => OnStaThread(() => List());

    /// <summary>
    /// Restores the entries (by recycled path) to where they were deleted from; Windows asks on a name clash. Returns how many it ran on.
    /// Runs on its own STA thread: finding the entries lists the whole bin again, which freezes the window with a big bin.
    /// <paramref name="owner"/> is only the owner of Windows' own dialogs.
    /// </summary>
    public static Task<int> RestoreAsync(nint owner, IReadOnlyCollection<string> recycledPaths) => OnStaThread(() => Invoke(owner, recycledPaths, "undelete", 0));

    /// <summary>Deletes the entries (by recycled path) for good, without Windows' own confirmation (Pollux asked). Returns how many it ran on.</summary>
    public static Task<int> DeletePermanentlyAsync(nint owner, IReadOnlyCollection<string> recycledPaths) => OnStaThread(() => Invoke(owner, recycledPaths, "delete", CMIC_MASK_FLAG_NO_UI));

    /// <summary>How many entries the Recycle Bin holds on all drives and their size; null when Windows cannot tell.</summary>
    public static (long Count, long Bytes)? Query()
    {
        var info = new SHQUERYRBINFO { cbSize = (uint)sizeof(SHQUERYRBINFO) };
        return SHQueryRecycleBinW(null, &info) == 0 ? (info.i64NumItems, info.i64Size) : null;
    }

    /// <summary>Empties the Recycle Bin on all drives with Windows' progress but without its confirmation (Pollux asked).</summary>
    public static Task<bool> EmptyAsync(nint owner) => OnStaThread(() => SHEmptyRecycleBinW(owner, null, SHERB_NOCONFIRMATION) == 0);

    private static Task<T> OnStaThread<T>(Func<T> work)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                done.SetResult(work());
            }
            catch (Exception ex)
            {
                done.SetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "Pollux Recycle Bin worker",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task;
    }

    private static List<RecycledItem> List()
    {
        var items = new List<RecycledItem>();
        if (!OpenBin(out var binPidl, out var folderPtr)) return items;
        List<(nint Child, string Path)> entries = [];
        try
        {
            var folder = (ShellContextMenu.IShellFolder)Wrappers.GetOrCreateObjectForComInstance(folderPtr, CreateObjectFlags.None);
            entries = Entries(folder, binPidl);
            foreach (var (child, path) in entries) items.Add(Describe(binPidl, child, path));
            return items;
        }
        finally
        {
            Free(binPidl, folderPtr, entries);
        }
    }

    /// <summary>Runs a verb of the bin's context menu on the entries with these recycled paths (all in one call, like Explorer).</summary>
    private static int Invoke(nint owner, IReadOnlyCollection<string> recycledPaths, string verb, uint mask)
    {
        if (recycledPaths.Count == 0 || !OpenBin(out var binPidl, out var folderPtr)) return 0;
        var wanted = new HashSet<string>(recycledPaths, StringComparer.OrdinalIgnoreCase);
        List<(nint Child, string Path)> entries = [];
        nint menuPtr = 0, hmenu = 0, verbText = 0;
        try
        {
            var folder = (ShellContextMenu.IShellFolder)Wrappers.GetOrCreateObjectForComInstance(folderPtr, CreateObjectFlags.None);
            entries = Entries(folder, binPidl);
            var picked = entries.Where(e => wanted.Contains(e.Path)).Select(e => e.Child).ToArray();
            if (picked.Length == 0) return 0;

            var iidMenu = typeof(ShellContextMenu.IContextMenu).GUID;
            fixed (nint* children = picked)
            {
                if (folder.GetUIObjectOf(owner, (uint)picked.Length, children, &iidMenu, 0, out menuPtr) != 0) return 0;
            }

            var menu = (ShellContextMenu.IContextMenu)Wrappers.GetOrCreateObjectForComInstance(menuPtr, CreateObjectFlags.None);
            // The bin's handler only knows its verbs once it has built its menu.
            hmenu = Native.CreatePopupMenu();
            if (menu.QueryContextMenu(hmenu, 0, 1, 0x7FFF, CMF_NORMAL) < 0) return 0;

            verbText = Marshal.StringToHGlobalAnsi(verb);
            var info = new ShellContextMenu.CMINVOKECOMMANDINFO
            {
                cbSize = (uint)sizeof(ShellContextMenu.CMINVOKECOMMANDINFO),
                fMask = mask,
                hwnd = owner,
                lpVerb = verbText,
                nShow = 1,
            };
            return menu.InvokeCommand(&info) == 0 ? picked.Length : 0;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            Services.CrashLog.Write(ex);
            return 0;
        }
        finally
        {
            if (hmenu != 0) Native.DestroyMenu(hmenu);
            if (verbText != 0) Marshal.FreeHGlobal(verbText);
            if (menuPtr != 0) Marshal.Release(menuPtr);
            Free(binPidl, folderPtr, entries);
        }
    }

    private static bool OpenBin(out nint binPidl, out nint folderPtr)
    {
        folderPtr = 0;
        var id = FolderIdRecycleBin;
        if (SHGetKnownFolderIDList(&id, 0, 0, out binPidl) != 0 || binPidl == 0) return false;
        var iidFolder = typeof(ShellContextMenu.IShellFolder).GUID;
        if (SHBindToObject(0, binPidl, 0, &iidFolder, out folderPtr) == 0 && folderPtr != 0) return true;
        Marshal.FreeCoTaskMem(binPidl);
        binPidl = 0;
        folderPtr = 0;
        return false;
    }

    /// <summary>
    /// The bin's entries as child IDs (the caller frees them) with the path of the "$R…" file or folder holding each
    /// (its shell parsing name if it has none); listing and restore/delete both identify entries by it.
    /// </summary>
    private static List<(nint Child, string Path)> Entries(ShellContextMenu.IShellFolder folder, nint binPidl)
    {
        var list = new List<(nint, string)>();
        nint enumPtr = 0;
        // S_FALSE and no enumerator: the bin is empty.
        if (folder.EnumObjects(0, SHCONTF_FOLDERS | SHCONTF_NONFOLDERS | SHCONTF_INCLUDEHIDDEN, (nint)(&enumPtr)) < 0 || enumPtr == 0) return list;
        try
        {
            var items = (IEnumIDList)Wrappers.GetOrCreateObjectForComInstance(enumPtr, CreateObjectFlags.None);
            nint child = 0;
            uint fetched = 0;
            while (items.Next(1, &child, &fetched) == 0 && fetched == 1 && child != 0)
            {
                var path = NameOf(binPidl, child, SIGDN_FILESYSPATH) ?? NameOf(binPidl, child, SIGDN_DESKTOPABSOLUTEPARSING);
                if (path is null) Marshal.FreeCoTaskMem(child);
                else list.Add((child, path));
                child = 0;
            }
        }
        finally
        {
            Marshal.Release(enumPtr);
        }

        return list;
    }

    /// <summary>One entry from its $I record; without a readable record, the shell's name and the file's own size and time.</summary>
    private static RecycledItem Describe(nint binPidl, nint child, string recycledPath)
    {
        var isDirectory = Directory.Exists(recycledPath);
        try
        {
            if (RecycleInfoFile.InfoPathFor(recycledPath) is { } info && RecycleInfoFile.Parse(File.ReadAllBytes(info), recycledPath, isDirectory) is { } item)
            {
                return item;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // Fall back to what the shell and the file itself tell.
        }

        var name = NameOf(binPidl, child, SIGDN_NORMALDISPLAY) ?? Path.GetFileName(recycledPath);
        long size = 0;
        var time = DateTime.UtcNow;
        try
        {
            if (!isDirectory) size = new FileInfo(recycledPath).Length;
            time = File.GetLastWriteTimeUtc(recycledPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // Size and time stay unknown.
        }

        return new RecycledItem(name, time, size, isDirectory, recycledPath);
    }

    private static string? NameOf(nint binPidl, nint child, uint sigdn)
    {
        var absolute = ILCombine(binPidl, child);
        if (absolute == 0) return null;
        try
        {
            if (SHGetNameFromIDList(absolute, sigdn, out var name) != 0 || name == 0) return null;
            try { return Marshal.PtrToStringUni(name); }
            finally { Marshal.FreeCoTaskMem(name); }
        }
        finally
        {
            Marshal.FreeCoTaskMem(absolute);
        }
    }

    private static void Free(nint binPidl, nint folderPtr, List<(nint Child, string Path)> entries)
    {
        foreach (var (child, _) in entries) Marshal.FreeCoTaskMem(child);
        if (folderPtr != 0) Marshal.Release(folderPtr);
        if (binPidl != 0) Marshal.FreeCoTaskMem(binPidl);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SHQUERYRBINFO
    {
        public uint cbSize;
        public long i64Size;
        public long i64NumItems;
    }

    [LibraryImport("shell32.dll")]
    private static partial int SHGetKnownFolderIDList(Guid* folderId, uint flags, nint token, out nint pidl);

    [LibraryImport("shell32.dll")]
    private static partial int SHBindToObject(nint parent, nint pidl, nint bindCtx, Guid* riid, out nint ppv);

    [LibraryImport("shell32.dll")]
    private static partial nint ILCombine(nint pidl1, nint pidl2);

    [LibraryImport("shell32.dll")]
    private static partial int SHGetNameFromIDList(nint pidl, uint sigdn, out nint name);

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SHQueryRecycleBinW(string? rootPath, SHQUERYRBINFO* info);

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SHEmptyRecycleBinW(nint owner, string? rootPath, uint flags);

    [GeneratedComInterface]
    [Guid("000214F2-0000-0000-C000-000000000046")]
    internal partial interface IEnumIDList
    {
        [PreserveSig] int Next(uint count, nint* items, uint* fetched);
        [PreserveSig] int Skip(uint count);
        [PreserveSig] int Reset();
        [PreserveSig] int Clone(out nint enumerator);
    }
}
