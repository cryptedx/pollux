using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Win32;
using Pollux.App.Interop;
using Pollux.Core.Files;
using Pollux.Core.Navigation;
using System.Runtime.InteropServices.WindowsRuntime;

namespace Pollux.App.Services;

/// <summary>
/// Shell icons for file list rows (Details) and as the fallback under a loading thumbnail (Tiles, Large Icons). One
/// background STA thread looks them up, newest request first; an ordinary file's icon is found by its type alone,
/// without touching the disk. Bitmaps are cached on the UI thread, which all Pollux windows share, once per distinct
/// system image list icon, so a folder of 10,000 .txt files holds one bitmap. Only realized list containers ask.
/// </summary>
internal static class Icons
{
    // UI thread only: which image an icon key showed (null: the shell had none), the images, the callbacks waiting.
    private static readonly IconCache<IconKey, ImageId> Keys = new(4096);
    private static readonly IconCache<ImageId, ImageSource> Images = new(512);
    private static readonly Dictionary<IconKey, List<Action<ImageSource>>> Waiting = [];
    private static readonly Dictionary<string, bool> PerFileTypes = new(StringComparer.Ordinal);
    private static readonly ThumbnailRequests<Job> Requests = new(512);
    private static Thread? _worker;

    private sealed record Job(IconKey Key, DispatcherQueue Ui);

    /// <summary>One icon of one system image list with one overlay; equal for every entry that shows it.</summary>
    private sealed record ImageId(int Index, int Overlay, ShellImageList List);

    private sealed record Found(ImageId Id, ShellThumbnail.Pixels Pixels);

    /// <summary>UI thread. The key of <paramref name="entry"/>'s icon drawn <paramref name="dips"/> wide at a display scale.</summary>
    public static IconKey KeyFor(FileEntry entry, int dips, double scale)
    {
        // A mapped network drive (Z:\) looks local, but reading files there can hang the icon thread.
        var pixels = IconSizes.Pixels(dips, scale);
        // A shell item without a path (a Control Panel applet, a phone) has its own icon, found by its parsing name.
        if (Locations.IsShell(entry.FullPath)) return new IconKey(entry.FullPath, true, entry.IsDirectory, default, false, pixels);
        var onNetworkDrive = Drives.For(entry.FullPath)?.Type == DriveType.Network;
        return IconKey.For(entry, pixels, onNetworkDrive, IsPerFileType);
    }

    /// <summary>
    /// UI thread. The cached icon, or null: then it is looked up (unless the shell had none last time) and
    /// <paramref name="loaded"/> runs on the UI thread once it is ready. Requests for one key share one lookup.
    /// </summary>
    public static ImageSource? Get(IconKey key, DispatcherQueue ui, Action<ImageSource> loaded)
    {
        if (Keys.TryGet(key, out var id))
        {
            if (id is null) return null;
            if (Images.TryGet(id, out var image) && image is not null) return image;
        }

        if (Waiting.TryGetValue(key, out var callbacks)) callbacks.Add(loaded);
        else Waiting[key] = [loaded];

        // Pushing a waiting key again moves it to the front; a request dropped from a full queue is asked for anew
        // when its entry is realized again.
        if (Requests.Push(new Job(key, ui)) is { } dropped) Waiting.Remove(dropped.Key);
        EnsureWorker();
        return null;
    }

    /// <summary>
    /// True when the type registers an icon per file (DefaultIcon "%1" or an icon handler). Read from the registry
    /// once per extension: an in-memory hive lookup, cheap enough for the UI thread.
    /// </summary>
    private static bool IsPerFileType(string extension)
    {
        if (PerFileTypes.TryGetValue(extension, out var known)) return known;
        var perFile = false;
        try
        {
            using var type = Registry.ClassesRoot.OpenSubKey(extension);
            if (type?.GetValue(null) is string { Length: > 0 } progId)
            {
                using var prog = Registry.ClassesRoot.OpenSubKey(progId);
                using var defaultIcon = prog?.OpenSubKey("DefaultIcon");
                using var handler = prog?.OpenSubKey(@"ShellEx\IconHandler");
                perFile = IconKeys.IsPerFileType(defaultIcon?.GetValue(null) as string, handler is not null);
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
            CrashLog.Trace($"icon type {extension}: {ex.Message}");
        }

        PerFileTypes[extension] = perFile;
        return perFile;
    }

    private static void EnsureWorker()
    {
        if (_worker is not null) return;
        _worker = new Thread(Run) { IsBackground = true, Name = "Pollux icons" };
        // SHGetFileInfo and icon handlers are apartment-threaded COM.
        _worker.SetApartmentState(ApartmentState.STA);
        _worker.Start();
    }

    private static void Run()
    {
        while (Requests.Take(CancellationToken.None) is { } job)
        {
            Found? found = null;
            try
            {
                found = Resolve(job.Key);
            }
            catch (Exception ex)
            {
                // The thread serves every Pane: one bad file must not stop it.
                CrashLog.Trace($"icon {job.Key.Name} failed: {ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}");
            }

            var done = found;
            job.Ui.TryEnqueue(DispatcherQueuePriority.Low, () => Deliver(job.Key, done));
        }
    }

    /// <summary>Icon thread. An entry's own icon when it is safe to read, else its type's.</summary>
    private static Found? Resolve(IconKey key)
    {
        var sizes = ShellIcon.Sizes();
        var list = IconSizes.Pick(key.Pixels, sizes.Small, sizes.Large, sizes.ExtraLarge);
        var own = Locations.IsShell(key.Name) ? ShellIcon.FindShell(Locations.ParsingName(key.Name))
            : key.PerPath && IsSafeToRead(key) ? ShellIcon.Find(key.Name, key.IsDirectory, fromDisk: true) : null;
        var found = own ?? ShellIcon.Find(key.TypeName, key.IsDirectory, fromDisk: false);
        if (found is not { } f) return null;
        // A type lookup knows no overlay: a shortcut there gets the shell's arrow by hand.
        var overlay = own is null && key.LinkOverlay ? ShellIcon.LinkOverlay : f.Overlay;
        return ShellIcon.Draw(list, f.Index, overlay) is { } pixels ? new Found(new ImageId(f.Index, overlay, list), pixels) : null;
    }

    /// <summary>
    /// Icon thread. False when the entry's icon comes from another machine: a shortcut to a share or with its icon
    /// there, a .url or desktop.ini naming a UNC or web icon. Showing a folder must not open network connections (ADR 0001).
    /// </summary>
    private static bool IsSafeToRead(IconKey key)
    {
        var path = key.Name;
        try
        {
            if (key.IsDirectory)
            {
                return IconKeys.IsDriveRoot(path)
                    ? !ShellIcon.IsNetworkDrive(char.ToUpperInvariant(path[0]))
                    : IniIsLocal(Path.Combine(path, "desktop.ini"));
            }

            return Path.GetExtension(path) switch
            {
                ".lnk" or ".pif" => ShellIcon.ReadLink(path) is { } link && IsLocal(link.Target) && IsLocal(link.Icon),
                ".url" or ".website" => IniIsLocal(path),
                _ => true,
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IniIsLocal(string file)
    {
        var info = new FileInfo(file);
        if (!info.Exists) return true;
        if (info.Length > 64 * 1024) return false;
        return IconKeys.IniIconLocation(File.ReadAllText(file)) is not { } location || IsLocal(location);
    }

    private static bool IsLocal(string location) =>
        IconKeys.IsLocalLocation(Environment.ExpandEnvironmentVariables(location), ShellIcon.IsNetworkDrive);

    private static void Deliver(IconKey key, Found? found)
    {
        ImageSource? source = null;
        if (found is not null && !(Images.TryGet(found.Id, out source) && source is not null))
        {
            try
            {
                // A WriteableBitmap, like Thumbnails: a cached source is shown again by new Image elements.
                var bitmap = new WriteableBitmap(found.Pixels.Width, found.Pixels.Height);
                using (var stream = bitmap.PixelBuffer.AsStream()) stream.Write(found.Pixels.Bgra);
                bitmap.Invalidate();
                source = bitmap;
                Images.Add(found.Id, source);
            }
            catch (Exception ex)
            {
                CrashLog.Trace($"icon {key.Name} not shown: {ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}");
            }
        }

        // A null entry remembers that the shell has no icon, so scrolling does not ask again.
        Keys.Add(key, source is null ? null : found!.Id);
        if (!Waiting.Remove(key, out var callbacks) || source is null) return;
        foreach (var callback in callbacks) callback(source);
    }
}
