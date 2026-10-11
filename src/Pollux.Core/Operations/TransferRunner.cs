using System.Diagnostics;

namespace Pollux.Core.Operations;

/// <summary>
/// Executes a <see cref="TransferJob"/>: plans the work (totals for progress), then copies or moves file by file.
/// Moves within one volume are renames; everything else is a streamed copy (plus delete for moves).
/// Partial files are removed on cancel; folders are merged; file conflicts follow the policy or ask.
/// </summary>
public static class TransferRunner
{
    private sealed record Item(string Source, string Target, bool IsDirectory, long Size, bool FastMove, bool TopLevel);

    public static async Task RunAsync(
        TransferJob job,
        Func<TransferConflict, Task<ConflictDecision>> ask,
        CancellationToken cancellationToken,
        int chunkSize = 1024 * 1024)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, job.CancellationToken);
        var ct = linked.Token;
        var request = job.Request;
        job.SetState(TransferState.Running);
        try
        {
            var target = Path.GetFullPath(request.TargetDirectory);
            if (!Directory.Exists(target)) throw new DirectoryNotFoundException($"Target folder not found: {target}");

            foreach (var source in request.Sources)
            {
                if (Directory.Exists(source) && IsSameOrUnder(target, Path.GetFullPath(source)) && !SameFolder(Path.GetDirectoryName(Path.GetFullPath(source).TrimEnd(Path.DirectorySeparatorChar))!, target))
                {
                    job.SetState(TransferState.Failed, $"Cannot copy or move \"{Path.GetFileName(source)}\" into itself.");
                    return;
                }
            }

            var unreadable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var plan = await Task.Run(() => Plan(job, target, unreadable), ct).ConfigureAwait(false);
            var totalBytes = plan.Sum(i => i.IsDirectory ? 0 : i.Size);
            var totalFiles = plan.Count(i => !i.IsDirectory || i.FastMove);
            var state = new RunState(job, ask, totalBytes, totalFiles, chunkSize);
            foreach (var dir in unreadable) MarkIncomplete(state, Path.Combine(dir, "x"));
            state.Report("");

            foreach (var item in plan)
            {
                ct.ThrowIfCancellationRequested();
                await job.WaitWhilePausedAsync(ct).ConfigureAwait(false);
                if (!await ExecuteAsync(state, item, ct).ConfigureAwait(false)) return; // cancelled by the user's conflict choice
            }

            if (request.Kind == TransferKind.Move) RemoveEmptySourceFolders(plan, state);
            state.Report("");
            job.SetState(TransferState.Completed);
        }
        catch (OperationCanceledException)
        {
            job.SetState(TransferState.Cancelled);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            job.SetState(TransferState.Failed, ex.Message);
        }
    }

    // ---------------- planning ----------------

    private static List<Item> Plan(TransferJob job, string target, HashSet<string> unreadable)
    {
        var request = job.Request;
        var items = new List<Item>();
        foreach (var raw in request.Sources)
        {
            var source = Path.GetFullPath(raw).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var isDir = Directory.Exists(source);
            if (!isDir && !File.Exists(source))
            {
                job.AddError($"Not found: {source}");
                continue;
            }

            var name = Path.GetFileName(source);
            var sameFolder = SameFolder(Path.GetDirectoryName(source)!, target);
            if (sameFolder && request.Kind == TransferKind.Move) continue; // moving onto itself is a no-op
            var dest = Path.Combine(target, sameFolder ? CopyName(target, name, isDir) : name);

            var fastMove = request.Kind == TransferKind.Move && SameVolume(source, target) && !Exists(dest);
            if (fastMove)
            {
                items.Add(new Item(source, dest, isDir, isDir ? 0 : new FileInfo(source).Length, FastMove: true, TopLevel: true));
                continue;
            }

            if (!isDir)
            {
                items.Add(new Item(source, dest, false, new FileInfo(source).Length, false, TopLevel: true));
                continue;
            }

            items.Add(new Item(source, dest, true, 0, false, TopLevel: true));

            // Walk folder by folder so an unreadable folder is reported (and kept on move) instead of silently skipped.
            var pending = new Stack<string>();
            pending.Push(source);
            var options = new EnumerationOptions { RecurseSubdirectories = false, IgnoreInaccessible = false, AttributesToSkip = 0 };
            while (pending.Count > 0)
            {
                var dir = pending.Pop();
                List<FileSystemInfo> children;
                try
                {
                    children = new DirectoryInfo(dir).EnumerateFileSystemInfos("*", options).ToList();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    job.AddError($"{dir}: {ex.Message}");
                    unreadable.Add(dir);
                    continue;
                }

                foreach (var e in children)
                {
                    var rel = Path.GetRelativePath(source, e.FullName);
                    var isSubDir = e is DirectoryInfo;
                    items.Add(new Item(e.FullName, Path.Combine(dest, rel), isSubDir, isSubDir ? 0 : ((FileInfo)e).Length, false, false));
                    if (isSubDir && !e.Attributes.HasFlag(FileAttributes.ReparsePoint)) pending.Push(e.FullName);
                }
            }
        }

        return items;
    }

    // ---------------- execution ----------------

    private sealed class RunState(TransferJob job, Func<TransferConflict, Task<ConflictDecision>> ask, long totalBytes, int totalFiles, int chunkSize)
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        public TransferJob Job { get; } = job;
        public Func<TransferConflict, Task<ConflictDecision>> Ask { get; } = ask;
        public int ChunkSize { get; } = chunkSize;
        public long DoneBytes { get; set; }
        public int DoneFiles { get; set; }
        public ConflictChoice? Remembered { get; set; }
        public HashSet<string> SkippedFolders { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> IncompleteFolders { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Folders that were placed under another name ("Keep both"); their planned contents follow them.</summary>
        public List<(string From, string To)> RenamedTargets { get; } = [];

        public string Retarget(string target)
        {
            foreach (var (from, to) in RenamedTargets)
            {
                if (target.StartsWith(from + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return to + target[from.Length..];
            }

            return target;
        }

        private double _sampleTime;
        private long _sampleBytes;
        private double _speed;

        /// <summary>Speed is measured over the last ~0.5 s, so pauses and slow starts do not distort it.</summary>
        public void Report(string current)
        {
            var now = _clock.Elapsed.TotalSeconds;
            var dt = now - _sampleTime;
            if (dt >= 0.5)
            {
                if (dt < 3) _speed = (DoneBytes - _sampleBytes) / dt; // longer gaps are pauses or prompts: just restart sampling
                _sampleTime = now;
                _sampleBytes = DoneBytes;
            }

            Job.Report(new TransferProgress(totalBytes, DoneBytes, totalFiles, DoneFiles, current, _speed));
        }
    }

    /// <summary>Returns false when the user chose Cancel in a conflict.</summary>
    private static async Task<bool> ExecuteAsync(RunState s, Item item, CancellationToken ct)
    {
        if (s.SkippedFolders.Any(f => IsSameOrUnder(item.Source, f))) return true;
        var request = s.Job.Request;
        try
        {
            if (item.FastMove)
            {
                if (item.IsDirectory) Directory.Move(item.Source, item.Target);
                else File.Move(item.Source, item.Target);
                s.Job.Log(new TransferStep(TransferStepKind.Entry, item.Source, item.Target));
                s.DoneFiles++;
                s.DoneBytes += item.Size;
                s.Report(item.Target);
                return true;
            }

            if (item.IsDirectory)
            {
                var dirTarget = s.Retarget(item.Target);
                if (File.Exists(dirTarget))
                {
                    // A file is in the way of a folder: never delete it, place the folder next to it ("Keep both").
                    var renamed = Path.Combine(Path.GetDirectoryName(dirTarget)!, UniqueName(Path.GetDirectoryName(dirTarget)!, Path.GetFileName(dirTarget), isDirectory: true));
                    s.RenamedTargets.Insert(0, (dirTarget, renamed));
                    dirTarget = renamed;
                }

                var existed = Directory.Exists(dirTarget);
                Directory.CreateDirectory(dirTarget);
                s.Job.Log(new TransferStep(TransferStepKind.Folder, item.Source, dirTarget, existed));
                return true;
            }

            var target = s.Retarget(item.Target);
            var replaced = false;
            if (Directory.Exists(target))
            {
                // A folder is in the way of a file: never delete the folder, keep both.
                target = Path.Combine(Path.GetDirectoryName(target)!, UniqueName(Path.GetDirectoryName(target)!, Path.GetFileName(target)));
            }
            else if (File.Exists(target))
            {
                var choice = await ResolveAsync(s, item).ConfigureAwait(false);
                switch (choice)
                {
                    case ConflictChoice.Cancel:
                        return Cancel(s);
                    case ConflictChoice.Skip:
                        s.DoneFiles++;
                        s.DoneBytes += item.Size;
                        MarkIncomplete(s, item.Source);
                        s.Report(target);
                        return true;
                    case ConflictChoice.Rename:
                        target = Path.Combine(Path.GetDirectoryName(target)!, UniqueName(Path.GetDirectoryName(target)!, Path.GetFileName(target)));
                        break;
                }

                replaced = choice == ConflictChoice.Overwrite;
            }

            await CopyFileAsync(s, item.Source, target, item.Size, ct).ConfigureAwait(false);
            if (request.Kind == TransferKind.Move) File.Delete(item.Source);
            s.Job.Log(new TransferStep(TransferStepKind.File, item.Source, target, replaced));
            s.DoneFiles++;
            s.Report(target);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            s.Job.AddError($"{item.Source}: {ex.Message}");
            MarkIncomplete(s, item.Source);
            s.DoneFiles += item.IsDirectory ? 0 : 1;
            return true;
        }
    }

    private static bool Cancel(RunState s)
    {
        s.Job.SetState(TransferState.Cancelled);
        return false;
    }

    private static void MarkIncomplete(RunState s, string source)
    {
        for (var dir = Path.GetDirectoryName(source); dir is not null; dir = Path.GetDirectoryName(dir)) s.IncompleteFolders.Add(dir);
    }

    private static async Task<ConflictChoice> ResolveAsync(RunState s, Item item)
    {
        var policy = s.Job.Request.Policy;
        switch (policy)
        {
            case ConflictPolicy.Overwrite: return ConflictChoice.Overwrite;
            case ConflictPolicy.Skip: return ConflictChoice.Skip;
            case ConflictPolicy.Rename: return ConflictChoice.Rename;
            case ConflictPolicy.OverwriteIfNewer:
                return File.GetLastWriteTimeUtc(item.Source) > File.GetLastWriteTimeUtc(s.Retarget(item.Target)) ? ConflictChoice.Overwrite : ConflictChoice.Skip;
        }

        if (s.Remembered is { } remembered) return remembered;
        var existing = s.Retarget(item.Target);
        var targetIsDir = Directory.Exists(existing);
        var conflict = new TransferConflict(
            item.Source,
            existing,
            item.IsDirectory,
            item.Size,
            item.IsDirectory ? Directory.GetLastWriteTime(item.Source) : File.GetLastWriteTime(item.Source),
            targetIsDir ? 0 : new FileInfo(existing).Length,
            targetIsDir ? Directory.GetLastWriteTime(existing) : File.GetLastWriteTime(existing));
        var previous = s.Job.State;
        s.Job.SetState(TransferState.Paused);
        var decision = await s.Ask(conflict).ConfigureAwait(false);
        if (previous == TransferState.Running) s.Job.SetState(TransferState.Running);
        if (decision.ApplyToAll && decision.Choice != ConflictChoice.Cancel) s.Remembered = decision.Choice;
        return decision.Choice;
    }

    /// <summary>
    /// Copies into a temporary sibling and swaps it in only when complete, so cancelling or failing a Replace
    /// never damages the existing file.
    /// </summary>
    private static async Task CopyFileAsync(RunState s, string source, string target, long size, CancellationToken ct)
    {
        var temp = Path.Combine(Path.GetDirectoryName(target)!, $".{Path.GetFileName(target)}.{Guid.NewGuid():N}.pollux-part");
        var created = File.GetCreationTimeUtc(source);
        var written = File.GetLastWriteTimeUtc(source);
        var attributes = File.GetAttributes(source);
        var completed = false;
        try
        {
            if (UsesWindowsCopy(attributes, OperatingSystem.IsWindows()))
            {
                // CopyFileEx keeps the file encrypted (and all streams); no per-chunk progress or mid-file cancel here.
                ct.ThrowIfCancellationRequested();
                File.Copy(source, temp, overwrite: false);
                File.SetAttributes(temp, FileAttributes.Hidden);
                s.DoneBytes += size;
                s.Report(target);
            }
            else
            {
                await StreamCopyAsync(s, source, temp, target, size, ct).ConfigureAwait(false);
                ZoneMark.Copy(source, temp); // before the swap and before a read-only attribute could block the write
            }

            File.SetCreationTimeUtc(temp, created);
            File.SetLastWriteTimeUtc(temp, written);
            if (File.Exists(target)) File.SetAttributes(target, FileAttributes.Normal); // a read-only file could not be replaced
            File.Move(temp, target, overwrite: true);
            File.SetAttributes(target, attributes);
            completed = true;
        }
        finally
        {
            if (!completed)
            {
                try { File.Delete(temp); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    /// <summary>
    /// True when a file must be copied with the Windows copy API instead of streams: an EFS-encrypted source would
    /// otherwise turn into a plaintext duplicate (S-10).
    /// </summary>
    internal static bool UsesWindowsCopy(FileAttributes sourceAttributes, bool isWindows) =>
        isWindows && sourceAttributes.HasFlag(FileAttributes.Encrypted);

    /// <summary>Copies the main data stream chunk by chunk, with progress, pause and cancel.</summary>
    private static async Task StreamCopyAsync(RunState s, string source, string temp, string target, long size, CancellationToken ct)
    {
        var buffer = new byte[Math.Max(4096, Math.Min(s.ChunkSize, size == 0 ? 4096 : size))];
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (OperatingSystem.IsWindows()) File.SetAttributes(temp, FileAttributes.Hidden); // not shown in listings while copying
        if (size > 0) output.SetLength(size);
        int read;
        while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            s.DoneBytes += read;
            s.Report(target);
            await s.Job.WaitWhilePausedAsync(ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
        }

        output.SetLength(output.Position);
    }

    private static void RemoveEmptySourceFolders(List<Item> plan, RunState s)
    {
        foreach (var dir in plan.Where(i => i.IsDirectory && !i.FastMove).Select(i => i.Source).OrderByDescending(p => p.Length))
        {
            if (s.IncompleteFolders.Contains(dir) || s.SkippedFolders.Contains(dir)) continue;
            try
            {
                if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    // ---------------- names & paths ----------------

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    private static string CopyName(string folder, string name, bool isDirectory)
    {
        var stem = isDirectory ? name : Path.GetFileNameWithoutExtension(name);
        var ext = isDirectory ? "" : Path.GetExtension(name);
        var candidate = $"{stem} - Copy{ext}";
        for (var i = 2; Exists(Path.Combine(folder, candidate)); i++) candidate = $"{stem} - Copy ({i}){ext}";
        return candidate;
    }

    public static string UniqueName(string folder, string name, bool isDirectory = false)
    {
        var stem = isDirectory ? name : Path.GetFileNameWithoutExtension(name);
        var ext = isDirectory ? "" : Path.GetExtension(name);
        var candidate = name;
        for (var i = 2; Exists(Path.Combine(folder, candidate)); i++) candidate = $"{stem} ({i}){ext}";
        return candidate;
    }

    private static bool SameFolder(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);

    private static bool IsSameOrUnder(string path, string root)
    {
        var p = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        var r = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        return p.Equals(r, StringComparison.OrdinalIgnoreCase) || p.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static bool SameVolume(string a, string b) =>
        string.Equals(Path.GetPathRoot(Path.GetFullPath(a)), Path.GetPathRoot(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);
}
