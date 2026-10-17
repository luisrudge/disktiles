// The scan: one task per directory on the work-stealing thread pool, at
// most one listing per core (or the power preset's count) at once, a `+1`
// sentinel per directory so it is built only once its own listing and every
// subdirectory task are done. Hardlink dedup runs as leaves are listed and
// each directory is aggregated as it completes (measured on an old dual-core
// laptop: separate passes cost a quarter of the scan in GC); only classify
// runs after.
using System.Text;

namespace Disktiles.Core;

public sealed record ScanOptions
{
    public bool ApparentSize { get; init; }
    public bool IncludeHidden { get; init; } = true;
    public bool OneFilesystem { get; init; } = true;
    public int? MaxDepth { get; init; }
    public bool DedupHardlinks { get; init; } = true;
    public Metric Metric { get; init; } = Metric.Bytes;
    /// Follow symlinks; off by default, since following them double-counts.
    public bool FollowLinks { get; init; }
    /// Directories listed at once, as the power presets set it; null is one
    /// per core, the default.
    public int? Threads { get; init; }
    // ponytail: no memoized `known` subtree and no adaptive thread admission;
    // the default scan needs neither.
}

public sealed class ScanProgress
{
    /// Errors kept verbatim before the list is truncated; the count keeps rising.
    const int MaxErrorDetail = 50;

    long _files, _dirs, _bytes, _errors;
    volatile bool _cancelled;
    readonly Lock _messagesLock = new();
    readonly List<string> _messages = [];

    public long Files => Interlocked.Read(ref _files);
    public long Dirs => Interlocked.Read(ref _dirs);
    public long Bytes => Interlocked.Read(ref _bytes);
    public long Errors => Interlocked.Read(ref _errors);
    public bool IsCancelled => _cancelled;
    public void Cancel() => _cancelled = true;

    public void Add(ulong files, ulong dirs, ulong bytes)
    {
        Interlocked.Add(ref _files, (long)files);
        Interlocked.Add(ref _dirs, (long)dirs);
        Interlocked.Add(ref _bytes, (long)bytes);
    }

    public void RecordError(string path, int errno)
    {
        Interlocked.Increment(ref _errors);
        // Only the ones that will be kept are worth formatting.
        lock (_messagesLock)
        {
            if (_messages.Count < MaxErrorDetail)
            {
                _messages.Add($"{path}: {Core.Errors.Text(errno)}");
            }
        }
    }
}

/// One directory being walked, plus the counter that decides when it is done.
/// It is its own thread-pool work item: queuing it allocates nothing more.
/// WalkContext.List decides when it is listed.
sealed class PendingDir(string path, NameRef name, PendingDir? parent, int depth, WalkContext context)
    : IThreadPoolWorkItem
{
    public readonly string Path = path;
    public readonly PendingDir? Parent = parent;
    public readonly NameRef Name = name;
    /// Starts at 1 for the directory itself; one more per subdirectory task.
    public int Pending = 1;
    /// Reserved exactly in the arena once the listing is done (leaves +
    /// subdirectories to come); `Block` is the
    /// array the run is in. `Count` slots are claimed so far: a finished
    /// subdirectory claims one with an atomic increment, so no lock (and no
    /// Lock object per directory) is needed.
    public Run Children;
    public Node[] Block = [];
    public int Count;
    public bool ReadError;
    public readonly int Depth = depth;
    readonly WalkContext _context = context;

    /// List this directory and spawn a task per subdirectory (Walk queues
    /// them itself, on this worker's own deque).
    public void Execute() => _context.List(this);

    /// Turn a completed directory into a node, aggregated: every child is
    /// final by now, so its totals and order are too. Only the task that took
    /// `Pending` to 0 calls it, and every slot write happened before its
    /// owner's decrement, a full fence.
    public Node Build(Metric metric, byte[][] names)
    {
        // Short only when a cancelled walk left subdirectories unbuilt.
        var node = Node.Directory(Name, Children with { Count = Count }, ReadError);
        Tree.Summarize(ref node, Block.AsSpan(Children.Start, Count), metric, names);
        return node;
    }
}

sealed class WalkContext(ScanOptions options, ScanProgress progress) : IDisposable
{
    public readonly ScanOptions Options = options;
    public readonly ScanProgress Progress = progress;
    /// The root's device, set once and read under this lock once per
    /// directory wherever there is no mount table (macOS).
    public readonly Lock RootDeviceLock = new();
    public ulong? RootDevice;
    /// Mount points `one_filesystem` keeps out, from /proc/self/mounts;
    /// null where the table cannot be read, and devices are compared.
    public HashSet<string>? ForeignMounts;
    public HashSet<string> NeverScanned = [];
    public Node? Root;
    public readonly Arena Arena = new();
    /// Hardlink dedup as leaves are found: the first name met is charged.
    public readonly Seen? Seen = options.DedupHardlinks ? new Seen() : null;
    public readonly ManualResetEventSlim Done = new();
    /// Directories entered through a followed link (and the root), so a
    /// link cannot loop.
    public readonly HashSet<(ulong, ulong)> VisitedDirs = [];
    /// Windows, following links: the root's final path. A file there has no
    /// identity to dedup by, so a link leading back inside is not followed.
    public string? RealRoot;

    /// After the walk has finished (Done), however it ended.
    public void Dispose()
    {
        Done.Dispose();
        Arena.Dispose();
    }

    public bool Cancelled => Progress.IsCancelled;

    // The pool is the process's, not the scan's: it can run more threads than
    // there are cores (it adds some when workers block in syscalls), and a
    // power preset asks for fewer. So at most `_limit` directories are listed
    // at once; one that comes up past that waits on a stack, and a lister
    // takes it when it is done with its own.
    readonly int _limit = Math.Max(1, options.Threads ?? Environment.ProcessorCount);
    readonly System.Collections.Concurrent.ConcurrentStack<PendingDir> _waiting = new();
    int _listers;

    public static void Queue(PendingDir dir, bool preferLocal) => ThreadPool.UnsafeQueueUserWorkItem(dir, preferLocal);

    public void List(PendingDir dir)
    {
        if (TryEnter())
        {
            Scanner.Walk(dir, this);
        }
        else
        {
            _waiting.Push(dir);
            // Every lister was busy, unless one left meanwhile.
            if (!TryEnter())
            {
                return;
            }
        }
        // A lister leaving re-checks the stack after it has counted itself
        // out, so a push that saw every lister busy is never stranded.
        do
        {
            while (_waiting.TryPop(out var waiting))
            {
                Scanner.Walk(waiting, this);
            }
            Interlocked.Decrement(ref _listers);
        }
        while (!_waiting.IsEmpty && TryEnter());
    }

    bool TryEnter()
    {
        for (var listers = Volatile.Read(ref _listers); listers < _limit; listers = Volatile.Read(ref _listers))
        {
            if (Interlocked.CompareExchange(ref _listers, listers + 1, listers) == listers)
            {
                return true;
            }
        }
        return false;
    }

    public ulong? RootDeviceOf(string path)
    {
        lock (RootDeviceLock)
        {
            if (RootDevice is { } device)
            {
                return device;
            }
            RootDevice = FileFacts.Of(path, follow: true, out var facts) == 0 ? facts.Device : null;
            return RootDevice;
        }
    }
}

public static class Scanner
{
    /// Entries counted before the shared progress counters are touched.
    const ulong TallyEvery = 1024;

    /// Walk `root` and return the aggregated, classified tree. Blocking.
    /// `progress` is for a UI to read the counters and cancel; a cancelled
    /// scan throws OperationCanceledException instead of a partial tree.
    public static NodeRef Scan(string root, ScanOptions options, ScanProgress? progress = null)
    {
        // These sets hash paths as strings, so every path the walk builds and
        // looks up starts from one spelling.
        root = Space.Normalize(root);
        progress ??= new ScanProgress();
        using var context = new WalkContext(options, progress);

        var error = FileFacts.Of(root, follow: true, out var rootMeta);
        if (error != 0)
        {
            throw new IOException($"{root}: {Errors.Text(error)}");
        }
        if (!rootMeta.IsDir)
        {
            throw new IOException($"{root} is not a directory");
        }
        var canonical = Space.RealPath(root) ?? root;
        var never = NeverScanned(root, canonical);
        // Hidden-entry and depth filters can leave an alias as the only route
        // to files; keep every mount view when either is active.
        if (options.IncludeHidden && options.MaxDepth is null)
        {
            never.AddRange(Space.RepeatedMountsFor(root, canonical, options.OneFilesystem));
        }
        context.NeverScanned = [.. never.Select(Space.Normalize)];
        if (options.OneFilesystem)
        {
            context.RootDevice = rootMeta.Device;
            if (Space.ForeignMountsFor(canonical) is { } foreign)
            {
                context.ForeignMounts = [.. foreign.Select(Space.Normalize)];
            }
        }

        if (options.FollowLinks)
        {
            context.VisitedDirs.Add((rootMeta.Device, rootMeta.Inode));
            context.RealRoot = OperatingSystem.IsWindows() ? canonical : null;
        }

        var rootDir = new PendingDir(root, context.Arena.Local.AddName(FileName(root)), null, 0, context);
        WalkContext.Queue(rootDir, preferLocal: false);
        context.Done.Wait();
        if (progress.IsCancelled)
        {
            throw new OperationCanceledException("the scan was stopped");
        }

        var tree = context.Arena.Finish(context.Root ?? throw new IOException($"{root} produced no tree")).Root;
        // Aggregated during the walk, directory by directory, on the workers;
        // classifying needs each parent's kind, so it waits for the whole tree.
        // On the pool: the waiting thread does not work.
        Task.Run(() => Classifier.Classify(tree)).Wait();
        return tree;
    }

    static readonly string[] MacNeverScanned = ["/System/Volumes/Data", "/Network"];

    /// macOS only: the Data volume's second mount, and /Network, an
    /// automount point, in `root`'s own spelling.
    static List<string> NeverScanned(string root, string canonical)
    {
        var never = new List<string>();
        if (!OperatingSystem.IsMacOS())
        {
            return never;
        }
        foreach (var skip in MacNeverScanned)
        {
            if (Space.StripPrefix(skip, canonical) is { Length: > 0 } below)
            {
                never.Add(Space.Join(root, below));
            }
        }
        return never;
    }

    /// The display name of a scanned root: its final component, or the path itself for `/`.
    static string FileName(string path)
    {
        var name = Path.GetFileName(path.TrimEnd('/'));
        return name.Length == 0 ? path : name;
    }

    /// What one directory's listing has found so far, for the progress meter.
    struct Tally
    {
        public ulong Files, Dirs, Bytes;

        public void Add(in Node node)
        {
            if (node.IsDir)
            {
                Files += node.Files;
                Dirs += node.Dirs;
            }
            else
            {
                Files += 1;
            }
            Bytes += node.Bytes;
        }

        public void Flush(ScanProgress progress)
        {
            progress.Add(Files, Dirs, Bytes);
            this = default;
        }
    }

    enum Outcome { Subdirectory, Entry, Skipped }

    /// Per-worker scratch for one listing; Walk never nests on a thread, and
    /// reusing them saves two growing lists per directory. The leaves are
    /// kept up to 16 Ki (896 KiB), so one huge directory is not held forever.
    [ThreadStatic] static List<Node>? t_leaves;
    [ThreadStatic] static List<PendingDir>? t_subdirs;
    const int MaxScratch = 16 * 1024;

    internal static void Walk(PendingDir dir, WalkContext context)
    {
        if (context.Cancelled)
        {
            SignalDone(dir, context);
            return;
        }
        var subdirs = t_subdirs ??= [];
        var leaves = t_leaves ??= [];
        var writer = context.Arena.Local;
        var tally = new Tally();

        // The platform's lister, chosen by an OperatingSystem check, which the
        // JIT and AOT fold to a constant.
        int openError;
        if (OperatingSystem.IsLinux())
        {
            if ((openError = LinuxDirReader.Open(dir.Path, out var reader)) == 0)
            {
                ListThenClose(ref reader, dir, context, writer, subdirs, leaves, ref tally);
            }
        }
        else if (OperatingSystem.IsMacOS())
        {
            if ((openError = MacDirReader.Open(dir.Path, out var reader)) == 0)
            {
                ListThenClose(ref reader, dir, context, writer, subdirs, leaves, ref tally);
            }
        }
        else if ((openError = ManagedDirReader.Open(dir.Path, out var reader)) == 0)
        {
            ListThenClose(ref reader, dir, context, writer, subdirs, leaves, ref tally);
        }
        if (openError != 0)
        {
            context.Progress.RecordError(dir.Path, openError);
            dir.ReadError = true;
        }
        tally.Flush(context.Progress);

        var descend = context.Options.MaxDepth is not { } maxDepth || dir.Depth < maxDepth;
        // Sized once, exactly, for the leaves and every subdirectory to come,
        // before any subdirectory task can add its node: no slack.
        var spawned = descend && !context.Cancelled ? subdirs.Count : 0;
        dir.Children = writer.Reserve(leaves.Count + spawned, out dir.Block);
        leaves.CopyTo(dir.Block.AsSpan(dir.Children.Start));
        dir.Count = leaves.Count;
        leaves.Clear();
        if (leaves.Capacity > MaxScratch)
        {
            t_leaves = null;
        }
        // One add for every subdirectory, before the first can finish.
        Interlocked.Add(ref dir.Pending, spawned);
        // `preferLocal` puts them on this worker's own deque, popped LIFO and
        // stolen FIFO.
        for (var i = 0; i < spawned; i++)
        {
            WalkContext.Queue(subdirs[i], preferLocal: true);
        }
        subdirs.Clear();
        SignalDone(dir, context);
    }

    /// The listing loop, generic over the lister: monomorphized, no
    /// interface dispatch.
    static void ListThenClose<T>(ref T reader, PendingDir dir, WalkContext context, Arena.Writer writer,
        List<PendingDir> subdirs, List<Node> leaves, ref Tally tally)
        where T : IDirReader, allows ref struct
    {
        try
        {
            ListAll(ref reader, dir, context, writer, subdirs, leaves, ref tally);
        }
        finally
        {
            reader.Dispose();
        }
    }

    static void ListAll<TReader>(ref TReader reader, PendingDir dir, WalkContext context, Arena.Writer writer,
        List<PendingDir> subdirs, List<Node> leaves, ref Tally tally)
        where TReader : IDirReader, allows ref struct
    {
        while (reader.Next(out var entry, out var error))
        {
            if (context.Cancelled)
            {
                break;
            }
            if (error != 0)
            {
                context.Progress.RecordError(dir.Path, error);
                continue;
            }
            switch (Classify(ref reader, context, dir.Path, ref entry, out var path, out var node, out var identity))
            {
                case Outcome.Subdirectory:
                    tally.Dirs += 1;
                    subdirs.Add(new PendingDir(path!, writer.AddName(entry.Name), dir, dir.Depth + 1, context));
                    break;
                case Outcome.Entry:
                    node.Name = writer.AddName(entry.Name);
                    // Counted for progress as listed, before dedup.
                    tally.Add(in node);
                    // Every name of a file has the file's size, so one that
                    // weighs nothing need not be remembered to be charged once.
                    if (identity.Inode != 0 && node.Bytes > 0 && context.Seen is { } seen
                        && !seen.Insert(identity.Device, identity.Inode))
                    {
                        node.Bytes = 0;
                    }
                    leaves.Add(node);
                    break;
            }
            if (tally.Files + tally.Dirs >= TallyEvery)
            {
                tally.Flush(context.Progress);
            }
        }
    }

    /// One task for `dir` is done; the last one builds the node and bubbles up.
    static void SignalDone(PendingDir dir, WalkContext context)
    {
        while (true)
        {
            if (Interlocked.Decrement(ref dir.Pending) != 0)
            {
                return;
            }
            var node = dir.Build(context.Options.Metric, context.Arena.Names);
            if (dir.Parent is not { } parent)
            {
                context.Root = node;
                context.Done.Set();
                return;
            }
            parent.Block[parent.Children.Start + Interlocked.Increment(ref parent.Count) - 1] = node;
            // A loop, not recursion: the walk up needs no stack.
            dir = parent;
        }
    }

    /// Decide what to do with an entry listed in `dir`. A leaf's name is
    /// added by the caller once it is kept; `identity` is its `(device,
    /// inode)` when the walk could meet it again, for dedup.
    static Outcome Classify<TReader>(ref TReader reader, WalkContext context, string dir, ref Entry entry,
        out string? path, out Node node, out (ulong Device, ulong Inode) identity)
        where TReader : IDirReader, allows ref struct
    {
        path = null;
        node = default;
        identity = default;
        var options = context.Options;
        if (!options.IncludeHidden && entry.Name is [(byte)'.', ..])
        {
            return Outcome.Skipped;
        }
        switch (entry.Kind)
        {
            case EntryKind.Link:
                path = Join(dir, entry.Name);
                return ClassifySymlink(context, path, out node, out identity);
            case EntryKind.Directory:
                path = Join(dir, entry.Name);
                return ClassifyDir(context, in entry, path);
        }
        var error = reader.Facts(ref entry);
        if (error != 0)
        {
            context.Progress.RecordError(Join(dir, entry.Name), error);
            return Outcome.Skipped;
        }
        var kind = entry.Kind == EntryKind.File ? NodeKind.File : NodeKind.Other;
        var size = options.ApparentSize ? entry.Apparent : entry.Allocated;
        // A followed link reaches a file a second way, whatever its link count says.
        node = LeafNode(kind, size, entry.Device, entry.Inode, entry.Modified, track: entry.Shared || options.FollowLinks, out identity);
        return Outcome.Entry;
    }

    /// `dir/name`, the name decoded on the stack: only directories, links and
    /// errors need a path, so leaves never make a string at all.
    static string Join(string dir, ReadOnlySpan<byte> name)
    {
        // ponytail: invalid UTF-8 is replaced, so such a name's path is lossy.
        Span<char> chars = stackalloc char[Encoding.UTF8.GetMaxCharCount(Math.Min(name.Length, 1024))];
        return name.Length <= 1024
            ? Path.Join(dir, chars[..Encoding.UTF8.GetChars(name, chars)])
            : Path.Join(dir, Encoding.UTF8.GetString(name));
    }

    static Outcome ClassifyDir(WalkContext context, scoped in Entry entry, string path)
    {
        if (context.NeverScanned.Contains(path))
        {
            return Outcome.Skipped;
        }
        // macOS (EVICTED_IS_KNOWN) and Windows (RecallOnOpen) can tell; the
        // listing carries the flag.
        if ((OperatingSystem.IsMacOS() || OperatingSystem.IsWindows()) && entry.Evicted)
        {
            return Outcome.Skipped;
        }
        if (context.Options.OneFilesystem)
        {
            if (context.ForeignMounts is { } foreign)
            {
                // By path, before anything reads the directory, so an
                // automount point is never triggered. No stat at all.
                if (foreign.Contains(path))
                {
                    return Outcome.Skipped;
                }
            }
            else if (context.RootDeviceOf(path) is { } rootDevice && entry.Device != rootDevice)
            {
                return Outcome.Skipped;
            }
        }
        return Outcome.Subdirectory;
    }

    /// Not followed: the link occupies only its target string. A stat of the
    /// full path without following it, as `fs::symlink_metadata` does.
    static Outcome ClassifySymlink(WalkContext context, string path, out Node node, out (ulong, ulong) identity)
    {
        node = default;
        identity = default;
        if (context.Options.FollowLinks)
        {
            return FollowSymlink(context, path, out node, out identity);
        }
        var error = FileFacts.Of(path, follow: false, out var meta);
        if (error != 0)
        {
            context.Progress.RecordError(path, error);
            return Outcome.Skipped;
        }
        var size = context.Options.ApparentSize ? meta.Size : Entry.SaturatingMul512(meta.Blocks);
        node = LeafNode(NodeKind.Symlink, size, meta.Device, meta.Inode, meta.MtimeSec, track: meta.Links > 1, out identity);
        return Outcome.Entry;
    }

    /// A followed link: a link to a directory is walked like one, once
    /// per directory it leads to; a link to anything else is charged as what
    /// it leads to, tracked so the same file reached directly counts once.
    static Outcome FollowSymlink(WalkContext context, string path, out Node node, out (ulong, ulong) identity)
    {
        node = default;
        identity = default;
        var error = FileFacts.Of(path, follow: true, out var meta);
        if (error != 0)
        {
            // A dangling link is normal, not an error worth counting.
            if (error != Errors.NotFound)
            {
                context.Progress.RecordError(path, error);
            }
            return Outcome.Skipped;
        }
        if (meta.IsDir)
        {
            // Inside the root: the walk counts it where it is.
            if (context.RealRoot is { } real && Win32.FinalPath(path, out _) is { } final && Space.PathStartsWith(final, real))
            {
                return Outcome.Skipped;
            }
            lock (context.VisitedDirs)
            {
                return context.VisitedDirs.Add((meta.Device, meta.Inode)) ? Outcome.Subdirectory : Outcome.Skipped;
            }
        }
        var kind = (meta.Mode & FileFacts.TypeMask) == FileFacts.TypeRegular ? NodeKind.File : NodeKind.Other;
        var size = context.Options.ApparentSize ? meta.Size : Entry.SaturatingMul512(meta.Blocks);
        node = LeafNode(kind, size, meta.Device, meta.Inode, meta.MtimeSec, track: true, out identity);
        return Outcome.Entry;
    }

    /// A leaf, not named yet; its identity is kept only when the walk could
    /// meet it again.
    static Node LeafNode(NodeKind kind, ulong size, ulong device, ulong inode, long modified, bool track,
        out (ulong, ulong) identity)
    {
        identity = track && inode != 0 ? (device, inode) : default;
        var node = Node.Entry(default, kind, size);
        node.Modified = modified;
        return node;
    }
}
