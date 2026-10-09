using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pollux.Core.Clipboard;

/// <summary>
/// Keeps the Clipboard History on disk, encrypted: one history file plus one file per image, so a new Clip
/// never rewrites the images. Local only (ADR 0001); lives under %LocalAppData%\Pollux\clipboard.
/// </summary>
public sealed class ClipStore(string directory, IClipProtector protector)
{
    private const string HistoryFileName = "history.dat";

    private string HistoryFile => Path.Combine(directory, HistoryFileName);
    private string ImageDirectory => Path.Combine(directory, "images");

    public static string DirectoryFor(PolluxPaths paths) => Path.Combine(paths.DataDirectory, "clipboard");

    private const int ReadAttempts = 3;
    private static readonly TimeSpan ReadRetryDelay = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// True after <see cref="Load"/> could not read the existing history (locked or access denied, even after retrying).
    /// Saving is refused until a later Load succeeds, so a temporary read error never overwrites the stored Clips.
    /// </summary>
    public bool LoadFailed { get; private set; }

    /// <summary>
    /// Reads the Clipboard History. Missing storage gives an empty history; undecryptable storage is moved aside as *.broken.
    /// Storage that cannot be read right now (locked, access denied) is retried, then gives an empty history and sets <see cref="LoadFailed"/>.
    /// </summary>
    public ClipboardHistory Load()
    {
        var file = HistoryFile;
        for (var attempt = 1; ; attempt++)
        {
            if (!File.Exists(file))
            {
                LoadFailed = false;
                return new ClipboardHistory();
            }

            try
            {
                var json = protector.Unprotect(File.ReadAllBytes(file));
                var state = JsonSerializer.Deserialize(json, ClipJson.Default.ClipHistoryFile) ?? new ClipHistoryFile();
                LoadFailed = false;
                return ClipboardHistory.Restore(state.Kept, state.Recent);
            }
            catch (Exception ex) when (ex is JsonException or CryptographicException)
            {
                TryMoveAside(file);
                LoadFailed = false;
                return new ClipboardHistory();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt >= ReadAttempts)
                {
                    LoadFailed = true;
                    return new ClipboardHistory();
                }

                Thread.Sleep(ReadRetryDelay);
            }
        }
    }

    /// <summary>
    /// Writes the Clipboard History (<see cref="Snapshot"/> then <see cref="Write"/>). Returns false, writing nothing,
    /// while <see cref="LoadFailed"/>.
    /// </summary>
    public bool Save(ClipboardHistory history)
    {
        if (Snapshot(history) is not { } snapshot)
        {
            return false;
        }

        Write(snapshot);
        return true;
    }

    /// <summary>
    /// Copies what <see cref="Write"/> needs, so the write can run on another thread while the history keeps changing.
    /// Cheap: Clip texts are shared, not copied. Null while <see cref="LoadFailed"/>.
    /// </summary>
    public ClipSnapshot? Snapshot(ClipboardHistory history)
    {
        if (LoadFailed)
        {
            return null;
        }

        var images = history.All.Where(c => c.Kind == ClipKind.Image).ToList();
        return new ClipSnapshot(
            history.Kept.Select(c => c.CopyForStorage()).ToList(),
            history.Recent.Select(c => c.CopyForStorage()).ToList(),
            images);
    }

    /// <summary>
    /// Writes a snapshot; safe to call off the UI thread (one write at a time). Images of new Clips go to their own files
    /// and are then dropped from memory (read them with <see cref="ReadImage"/>); image files of Clips no longer in the
    /// history are deleted.
    /// </summary>
    public void Write(ClipSnapshot snapshot)
    {
        Directory.CreateDirectory(directory);
        foreach (var clip in snapshot.Images)
        {
            if (clip.Image is { } image)
            {
                var path = ImagePath(clip);
                if (!File.Exists(path))
                {
                    Directory.CreateDirectory(ImageDirectory);
                    AtomicFile.WriteAllBytes(path, protector.Protect(image));
                }

                clip.Image = null;
            }
        }

        var state = new ClipHistoryFile { Kept = snapshot.Kept, Recent = snapshot.Recent };
        AtomicFile.WriteAllBytes(HistoryFile, protector.Protect(JsonSerializer.SerializeToUtf8Bytes(state, ClipJson.Default.ClipHistoryFile)));

        DeleteOrphanImages(snapshot.Images);
    }

    /// <summary>The image bytes of an image Clip, or null when they are gone.</summary>
    public byte[]? ReadImage(Clip clip)
    {
        if (clip.Image is { } inMemory)
        {
            return inMemory;
        }

        try
        {
            var path = ImagePath(clip);
            return File.Exists(path) ? protector.Unprotect(File.ReadAllBytes(path)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        {
            return null;
        }
    }

    private string ImagePath(Clip clip) => Path.Combine(ImageDirectory, clip.Id + ".dat");

    private void DeleteOrphanImages(List<Clip> clips)
    {
        if (!Directory.Exists(ImageDirectory))
        {
            return;
        }

        var live = clips.Where(c => c.Kind == ClipKind.Image).Select(ImagePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(ImageDirectory))
        {
            if (!live.Contains(file))
            {
                TryDelete(file);
            }
        }
    }

    private static void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Retried on the next save.
        }
    }

    private static void TryMoveAside(string file)
    {
        try
        {
            File.Move(file, file + ".broken", overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Overwritten by the next save then.
        }
    }
}

/// <summary>A copy of the Clipboard History taken on the UI thread, written by <see cref="ClipStore.Write"/>.</summary>
public sealed class ClipSnapshot
{
    internal ClipSnapshot(List<Clip> kept, List<Clip> recent, List<Clip> images)
    {
        Kept = kept;
        Recent = recent;
        Images = images;
    }

    internal List<Clip> Kept { get; }
    internal List<Clip> Recent { get; }

    /// <summary>The live image Clips: their in-memory bytes are written once, then dropped.</summary>
    internal List<Clip> Images { get; }
}

/// <summary>Contents of the encrypted history file.</summary>
internal sealed class ClipHistoryFile
{
    public int Version { get; set; } = 1;
    public List<Clip> Kept { get; set; } = [];
    public List<Clip> Recent { get; set; } = [];
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true)]
[JsonSerializable(typeof(ClipHistoryFile))]
internal sealed partial class ClipJson : JsonSerializerContext;
