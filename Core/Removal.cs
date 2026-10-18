// Turning marked paths into deletions, safely. In this order: never remove
// something the user did not point at, never descend into a different
// filesystem, always say what happened.
//
// Managed APIs only, no syscalls of its own: the identity and mount checks
// reuse FileFacts, the stat the scan already makes. An openat walk
// (O_NOFOLLOW from the root down) has no managed equivalent, so the walk
// checks every directory on the way down for a link before it acts; see
// RemovePermanently for the race that leaves.
//
// Paths are strings compared by component (Space.Components).
using System.Collections.Concurrent;
using System.Diagnostics;

namespace Disktiles.Core;

/// One path the user asked to remove; `Bytes` as measured when marked.
public sealed record Target(string Path, ulong Bytes, bool IsDir, bool Hidden);

public enum RemovalMode { Permanent, Trash }

/// A target that will not be touched, and why.
public sealed record Blocked(string Path, string Reason);

/// The work a removal will actually do.
public sealed class Plan
{
    /// Targets to act on; one inside another is covered by it.
    public List<Target> Targets { get; } = [];
    /// Marked paths inside a target: reported, not acted on.
    public List<Target> Covered { get; } = [];
    public List<Blocked> Blocked { get; } = [];
    /// The scanned root the targets were judged against.
    public string Root { get; init; } = "";
    /// Where Root really was when planned: the worker refuses everything if
    /// it is somewhere else now (the root swapped for a link).
    public string? RealRoot { get; init; }
    /// Bytes the targets free on Root's volume: all the meter may project.
    public ulong Reclaim { get; internal set; }
    /// Bytes the targets free on other volumes, said, never projected.
    public ulong Foreign { get; internal set; }

    public ulong Bytes => Targets.Aggregate(0UL, (sum, target) => sum + target.Bytes);
    /// Bytes no volume could be found for: no saving is claimed for them.
    public ulong Unattributed => Bytes - Math.Min(Bytes, Reclaim + Foreign);
    public bool IsEmpty => Targets.Count == 0;
}

/// Which tool moves files to the desktop trash.
public enum TrashBackend { TrashPut, Gio, XdgHome, Unavailable }

/// What a running removal reports.
public abstract record RemovalEvent
{
    public sealed record Start(int Total) : RemovalEvent;
    /// `Error` is null when the item went.
    public sealed record Item(string Path, ulong Bytes, string? Error) : RemovalEvent;
    public sealed record Done(ulong Removed, ulong Bytes, int Failed) : RemovalEvent;
}

/// A removal running on its own thread.
public sealed class RemovalHandle
{
    internal readonly ConcurrentQueue<RemovalEvent> Events = new();
    internal volatile bool Cancelled;

    /// The next event, if one has arrived.
    public bool Poll(out RemovalEvent? next) => Events.TryDequeue(out next);

    /// Stop before the next target starts.
    public void Cancel() => Cancelled = true;
}

/// Paths and the media exception from one read of the mount table, so they
/// cannot disagree; `Mounts` (/proc/self/mounts) places a target's bytes.
public sealed record MountTable(List<string> Points, List<string> Media, List<Mount>? Mounts)
{
    public static readonly MountTable Empty = new([], [], null);
}

public static class Removal
{
    public static string Label(this RemovalMode mode) =>
        mode == RemovalMode.Permanent ? "Delete permanently" : "Move to trash";

    public static string Detail(this RemovalMode mode) =>
        mode == RemovalMode.Permanent ? "rm -rf: unrecoverable" : "recoverable until the trash is emptied";

    /// Build the plan for `targets`, which must all live under `root`: the
    /// tree the user was looking at is the only thing they consented to.
    public static Plan Plan(IReadOnlyList<Target> targets, string root) =>
        PlanAgainst(targets, root, MountPoints(), HomeDir());

    /// A name that was not valid UTF-8 reaches us with U+FFFD in place of
    /// its bytes (the tree decodes names lossily), so its path names another
    /// file, or none: a sibling really named with U+FFFD would be removed
    /// instead. A real U+FFFD in a name is refused too; that is the price.
    static string? Undecodable(string path) => path.Contains('�')
        ? "its name is not valid UTF-8, so its path is not exact; remove it by hand"
        : null;

    /// Plan against a given read of the mount table and home, for testing.
    public static Plan PlanAgainst(IReadOnlyList<Target> targets, string root, MountTable mounts, string? home)
    {
        root = Normalize(root);
        var realRoot = Realpath(root);
        var plan = new Plan { Root = root, RealRoot = realRoot };
        if (targets.Count == 0)
        {
            return plan;
        }
        // Once per plan, not per target.
        var homeInfo = home is null ? null : Home.Of(home);
        var accepted = new List<Target>();
        foreach (var target in targets)
        {
            var path = Normalize(target.Path);
            if ((Undecodable(path) ?? Refuse(path, root, homeInfo, mounts.Points, mounts.Media)
                ?? Linked(path, root, realRoot, homeInfo)) is { } reason)
            {
                plan.Blocked.Add(new Blocked(target.Path, reason));
                continue;
            }
            accepted.Add(target with { Path = path });
        }

        // A path inside another target goes with it: keep the outer one,
        // report the inner one. Sorted by component, so parents come first.
        accepted.Sort((left, right) => CompareComponents(left.Path, right.Path));
        foreach (var target in accepted)
        {
            if (plan.Targets.Any(outer => Within(target.Path, outer.Path)))
            {
                plan.Covered.Add(target);
            }
            else
            {
                plan.Targets.Add(target);
            }
        }

        foreach (var target in plan.Targets)
        {
            switch (Attribute(mounts.Mounts, plan.Root, target.Path))
            {
                case Attribution.Scanned: plan.Reclaim += target.Bytes; break;
                case Attribution.Other: plan.Foreign += target.Bytes; break;
            }
        }
        return plan;
    }

    // ── guards ──────────────────────────────────────────────────────────

    /// Trees the operating system owns: files there belong to packages, and
    /// removing them by hand breaks the system. Refused even inside them.
    static readonly string[] SystemTrees =
    [
        "/bin", "/boot", "/dev", "/etc", "/lib", "/lib64", "/lib32", "/libx32", "/nix/store", "/gnu/store",
        "/proc", "/run", "/sbin", "/sys", "/usr", "/var/lib", "/efi",
        // Homebrew on Linux, outside every user's home.
        "/home/linuxbrew/.linuxbrew",
    ];

    /// The trees with their guard keys, computed once.
    static readonly (string Name, string Key)[] SystemTreeKeys =
        [.. SystemTrees.Select(tree => (tree, GuardKey(tree)))];

    const string SystemTools = "remove it with the tool that installed it";

    /// How the guards compare paths: lexically normalized. Only for judging:
    /// what is removed is always the path as marked.
    public static string GuardKey(string path) => Normalize(path);

    /// `path` is `base` or inside it, by whole components, as guards compare.
    static bool Within(string path, string @base) => Space.PathStartsWith(GuardKey(path), GuardKey(@base));

    static string? SystemTree(string key, string? home)
    {
        // Home is never system, wherever it lives.
        if (home is not null && Space.PathStartsWith(key, home))
        {
            return null;
        }
        foreach (var (name, tree) in SystemTreeKeys)
        {
            if (Space.PathStartsWith(key, tree))
            {
                return name;
            }
        }
        return null;
    }

    /// A system tree strictly inside `key`: removing `/opt` would take a tree below it.
    static string? SystemTreeBelow(string key)
    {
        foreach (var (name, tree) in SystemTreeKeys)
        {
            if (tree != key && Space.PathStartsWith(tree, key))
            {
                return name;
            }
        }
        return null;
    }

    /// Home as the guards compare it: by key, by real path (`/home` may be a
    /// link to `/data/home`), and by device and inode.
    public sealed record Home(string Key, string? Real, (ulong, ulong)? Id)
    {
        public static Home Of(string path) =>
            new(GuardKey(path), Realpath(path) is { } real ? GuardKey(real) : null, Identity(path, follow: true));
    }

    /// Home: `HOME`.
    public static string? HomeDir() =>
        Environment.GetEnvironmentVariable("HOME") is { Length: > 0 } home ? home
        : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) is { Length: > 0 } fallback ? fallback : null;

    public static string? Refuse(string path, string root, Home? home, IReadOnlyCollection<string> mounts,
        IReadOnlyCollection<string> media)
    {
        var key = GuardKey(path);
        var rootKey = GuardKey(root);
        var homeKey = home?.Key;
        if (Parent(path) is null)
        {
            return "the filesystem root cannot be removed";
        }
        if (key == rootKey)
        {
            return "the scanned root cannot be removed";
        }
        if (key == homeKey || (home?.Id is { } id && Identity(path, follow: false) == id))
        {
            return "the home directory cannot be removed";
        }
        // /home is usually its own mount and refused as one.
        if (homeKey is not null && Space.PathStartsWith(homeKey, key))
        {
            return "it contains the home directory";
        }
        if (!Space.PathStartsWith(key, rootKey) && !Space.PathStartsWith(path, root))
        {
            return "outside the scanned root";
        }
        if (SystemTree(key, homeKey) is { } system
            && !(system == "/run" && OnMedia(key, mounts, media) && Realpath(path) is { } real && Space.PathEquals(real, path)))
        {
            return $"part of the system under {system}: {SystemTools}";
        }
        if (SystemTreeBelow(key) is { } below)
        {
            return $"it contains {below}, which is part of the system";
        }
        if (mounts.Contains(key) || IsMountPoint(path))
        {
            return "a mount point: removing it would cross onto another filesystem";
        }
        if (MountBelow(key, mounts) is { } mount)
        {
            return $"{mount} is mounted inside it: removing it would reach into another filesystem";
        }
        return null;
    }

    /// Why `path`, inside `root` as spelled, is somewhere else in fact: a link
    /// on the way down makes a spelling inside the root name something
    /// outside it, and the trash follows it. Links above the root are the
    /// user's own choice, so real is compared with real. Anything that cannot
    /// be resolved passes: removing a path that is gone fails on its own.
    public static string? Linked(string path, string root, string? realRoot, Home? home)
    {
        if (Parent(path) is not { } parent || FileName(path) is not { } name)
        {
            return null;
        }
        if (Realpath(parent) is not { } realParent)
        {
            return null;
        }
        var real = GuardKey(Space.Join(realParent, name));
        if (realRoot is not null && Space.StripPrefix(path, root) is { } rest
            && real != GuardKey(Space.Join(realRoot, rest)))
        {
            return $"reached through a link: it is really {real}";
        }
        if (home?.Real is { } homeReal && Space.PathStartsWith(homeReal, real))
        {
            return "it contains the home directory";
        }
        return null;
    }

    /// (device, inode), from the scan's own stat; `follow` decides whether a
    /// link answers for itself or for what it points to.
    static (ulong, ulong)? Identity(string path, bool follow) =>
        FileFacts.Of(path, follow, out var facts) == 0 ? (facts.Device, facts.Inode) : null;

    /// Whether `path` is a directory on another device than its parent.
    public static bool IsMountPoint(string path)
    {
        if (FileFacts.Of(path, follow: false, out var facts) != 0 || !facts.IsDir)
        {
            return false;
        }
        if (Parent(path) is not { } parent)
        {
            return true;
        }
        // A directory whose parent cannot be stated is not worth the risk.
        return FileFacts.Of(parent, follow: false, out var above) != 0 || above.Device != facts.Device;
    }

    /// A mount point strictly inside `key`: a scan that stays on one
    /// filesystem never showed it, so the user cannot have meant it.
    public static string? MountBelow(string key, IEnumerable<string> mounts) =>
        mounts.FirstOrDefault(mount => mount != key && Space.PathStartsWith(mount, key));

    /// Only real disks the desktop mounted under /run/media are removable
    /// there. Missing information fails closed.
    public static List<string> MediaRoots(List<MountView> mounts)
    {
        string[] disks = ["ext2", "ext3", "ext4", "btrfs", "xfs", "f2fs", "vfat", "exfat", "ntfs", "ntfs3", "fuseblk", "fuse.ntfs-3g"];
        return
        [
            .. mounts.Where(mount => Space.PathStartsWith(mount.Point, "/run/media")
                && !Space.PathEquals(mount.Point, "/run/media")
                // An overmounted disk can stay in mountinfo: ambiguous stacked
                // views never grant an exception.
                && mounts.Count(other => other.Point == mount.Point) == 1
                && mount.FsRoot == "/"
                && mount.Source.StartsWith("/dev/", StringComparison.Ordinal)
                && disks.Contains(mount.FsType)
                && !mounts.Any(other => !Space.PathStartsWith(other.Point, "/run/media")
                    && (other.Point == "/" || SystemTree(other.Point, null) is not null)
                    && (other.Device == mount.Device || other.Source == mount.Source)))
                .Select(mount => mount.Point),
        ];
    }

    /// Nested mounts do not inherit their parent's exception.
    public static bool OnMedia(string key, IEnumerable<string> mounts, IReadOnlyCollection<string> media)
    {
        var point = mounts.Where(point => Space.PathStartsWith(key, point))
            .MaxBy(point => Space.Components(point).Count);
        return point is not null && key != point && media.Contains(point);
    }

    // ── the mount table ─────────────────────────────────────────────────

    /// How long a read is used before it is read again. A disk plugged in
    /// is noticed within this, and the worker reads again before it acts.
    static readonly TimeSpan MountsFresh = TimeSpan.FromSeconds(2);
    static readonly Lock CacheLock = new();
    static MountTable? s_cached;
    static long s_readAt;
    static bool s_refreshing;

    /// The table as last read. Never blocks on the read: a stale table
    /// starts one on the pool and is used meanwhile.
    public static MountTable MountPoints()
    {
        lock (CacheLock)
        {
            var fresh = s_cached is not null && Stopwatch.GetElapsedTime(s_readAt) < MountsFresh;
            if (!fresh && !s_refreshing)
            {
                s_refreshing = true;
                Task.Run(() =>
                {
                    var table = ReadMountTable();
                    lock (CacheLock)
                    {
                        (s_cached, s_readAt, s_refreshing) = (table, Stopwatch.GetTimestamp(), false);
                    }
                });
            }
            return s_cached ?? MountTable.Empty;
        }
    }

    /// Start the first read, so it is there by the time anything is marked.
    public static void PrimeMountPoints() => MountPoints();

    public static MountTable ReadMountTable()
    {
        // The table the scan reads for its volume, so a projection is judged
        // by the rule the tree was built under.
        var mounts = ReadText("/proc/self/mounts") is { } text ? Space.ParseMounts(text) : null;
        if (ReadText("/proc/self/mountinfo") is { } info)
        {
            var views = Space.ParseMountinfo(info);
            return new([.. views.Select(view => GuardKey(view.Point))], MediaRoots(views), mounts);
        }
        return mounts is not null ? new([.. mounts.Select(mount => GuardKey(mount.Point))], [], mounts) : MountTable.Empty;
    }

    static string? ReadText(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // ── which volume the bytes come back to ─────────────────────────────

    public enum Attribution { Scanned, Other, Unknown }

    /// The scan's own rule decides: a volume is the mount source and type,
    /// not a device number, so /home on its own Btrfs subvolume is part of
    /// the / a user scans, and another disk, a tmpfs or a snapshot is not.
    public static Attribution Attribute(List<Mount>? mounts, string root, string path)
    {
        if (mounts is null)
        {
            return AttributedByDevice(root, path);
        }
        if (Covering(mounts, root) is not { } own || Covering(mounts, path) is not { } other)
        {
            return Attribution.Unknown;
        }
        return own.Point == other.Point || (own.Source == other.Source && own.FsType == other.FsType && !Space.IsSnapshot(other))
            ? Attribution.Scanned
            : Attribution.Other;
    }

    static Mount? Covering(List<Mount> mounts, string path) =>
        mounts.Where(mount => Space.PathStartsWith(path, mount.Point)).MaxBy(mount => mount.Point.Length);

    /// No table: the device number. Equal is the same filesystem; a
    /// different one may still be a Btrfs subvolume of it, which only the
    /// table could tell, so nothing is claimed.
    static Attribution AttributedByDevice(string root, string path)
    {
        if (Identity(root, follow: true) is not (var own, _) || Identity(path, follow: true) is not (var other, _))
        {
            return Attribution.Unknown;
        }
        return own == other ? Attribution.Scanned : Attribution.Unknown;
    }

    // ── paths ───────────────────────────────────────────────────────────

    /// Resolve `.` and `..` without the filesystem, so a link can never
    /// redirect a guard. `/..` is `/`; a leading `..` on a relative path stays.
    public static string Normalize(string path)
    {
        var parts = new List<string>();
        var all = Space.Components(path);
        var rooted = all is [var first, ..] && Space.IsRoot(first);
        foreach (var part in all)
        {
            if (parts.Count == 0 && rooted)
            {
                parts.Add(part);
                continue;
            }
            if (part == "..")
            {
                if (parts.Count > 0 && parts[^1] != ".." && !Space.IsRoot(parts[^1]))
                {
                    parts.RemoveAt(parts.Count - 1);
                }
                else if (!rooted)
                {
                    parts.Add("..");
                }
                continue;
            }
            parts.Add(part);
        }
        return Space.Join(parts);
    }

    /// The path without its last component: null for `/` and the empty path.
    public static string? Parent(string path)
    {
        var parts = Space.Components(path);
        if (parts.Count == 0 || parts is [var only] && Space.IsRoot(only))
        {
            return null;
        }
        parts.RemoveAt(parts.Count - 1);
        return Space.Join(parts);
    }

    public static string? FileName(string path) =>
        Space.Components(path) is { Count: > 0 } parts && parts[^1] != ".." && !Space.IsRoot(parts[^1]) ? parts[^1] : null;

    static int CompareComponents(string left, string right)
    {
        var (a, b) = (Space.Components(left), Space.Components(right));
        for (var i = 0; i < Math.Min(a.Count, b.Count); i++)
        {
            if (Space.Names.Compare(a[i], b[i]) is var order and not 0)
            {
                return order;
            }
        }
        return a.Count.CompareTo(b.Count);
    }

    /// `fs::canonicalize` from managed calls: every link resolved, one
    /// component at a time; null when a part is missing, or after 40 links.
    public static string? Realpath(string path)
    {
        var full = Space.Components(Path.GetFullPath(path));
        var pending = new Stack<string>(full.Where(part => !Space.IsRoot(part)).Reverse());
        var resolved = full is [var root, ..] && Space.IsRoot(root) ? root : "/";
        for (var links = 0; pending.TryPop(out var part);)
        {
            if (part == "..")
            {
                resolved = Parent(resolved) ?? resolved;
                continue;
            }
            var next = Space.Join(resolved, part);
            if (Space.LinkTarget(next) is { } target)
            {
                if (++links > 40)
                {
                    return null;
                }
                var steps = Space.Components(target);
                foreach (var step in steps.Where(step => !Space.IsRoot(step)).Reverse())
                {
                    pending.Push(step);
                }
                if (steps is [var targetRoot, ..] && Space.IsRoot(targetRoot))
                {
                    resolved = targetRoot;
                }
                continue;
            }
            // A file can only be the last part.
            if (!Directory.Exists(next) && !(pending.Count == 0 && File.Exists(next)))
            {
                return null;
            }
            resolved = next;
        }
        return resolved;
    }

    // ── the trash ───────────────────────────────────────────────────────

    public static string Label(this TrashBackend backend) => backend switch
    {
        TrashBackend.TrashPut => "trash-put",
        TrashBackend.Gio => "gio trash",
        TrashBackend.XdgHome => "XDG trash",
        _ => "no trash tool found",
    };

    public static string Detail(this TrashBackend backend) => backend switch
    {
        TrashBackend.TrashPut => "uses trash-cli, the same trash as your file manager",
        TrashBackend.Gio => "uses GLib, the same trash as your file manager",
        TrashBackend.XdgHome => "moves into ~/.local/share/Trash on the same volume",
        _ => "install trash-cli or keep deleting permanently",
    };

    public static bool IsAvailable(this TrashBackend backend) => backend != TrashBackend.Unavailable;

    /// The best trash on this machine: trash-put, then gio (what Omarchy's
    /// Nautilus uses), then the XDG layout implemented here.
    public static TrashBackend DetectTrashBackend() =>
        Which("trash-put") ? TrashBackend.TrashPut
        : Which("gio") ? TrashBackend.Gio
        : HomeTrashDir() is not null ? TrashBackend.XdgHome
        : TrashBackend.Unavailable;

    static bool Which(string program) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries)
            .Any(dir => File.Exists(Path.Join(dir, program)));

    static string? HomeTrashDir() =>
        Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } data ? Path.Join(data, "Trash")
        : Environment.GetEnvironmentVariable("HOME") is { Length: > 0 } home ? Path.Join(home, ".local/share/Trash")
        : null;

    /// `trash`: the XDG trash to use instead of the user's (tests).
    public static void MoveToTrash(string path, TrashBackend backend, string? trash = null)
    {
        switch (backend)
        {
            case TrashBackend.TrashPut: RunTool("trash-put", [], path); break;
            case TrashBackend.Gio: RunTool("gio", ["trash"], path); break;
            case TrashBackend.XdgHome:
                TrashInto(path, trash ?? HomeTrashDir() ?? throw new IOException("neither XDG_DATA_HOME nor HOME is set"));
                break;
            default: throw new IOException("no trash tool is installed; use permanent deletion instead");
        }
    }

    /// One trash tool on one path, never through a shell. `--` before the
    /// path, so a file named `-rf` is read as a path and never as an option.
    public static void RunTool(string program, string[] arguments, string path, bool separator = true)
    {
        var start = new ProcessStartInfo(program)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        if (separator)
        {
            start.ArgumentList.Add("--");
        }
        start.ArgumentList.Add(path);
        string stderr;
        int code;
        try
        {
            using var tool = Process.Start(start)!;
            tool.StandardInput.Close();
            var output = tool.StandardOutput.ReadToEndAsync();
            stderr = tool.StandardError.ReadToEnd();
            output.Wait();
            tool.WaitForExit();
            code = tool.ExitCode;
        }
        catch (System.ComponentModel.Win32Exception error)
        {
            throw new IOException($"{program} could not run: {error.Message}");
        }
        if (code != 0)
        {
            throw new IOException($"{program} failed: {stderr.Trim()}");
        }
    }

    /// The XDG trash layout, for machines without trash-cli or GLib. Same
    /// volume only: the specification forbids copying into a trash.
    public static void TrashInto(string path, string trash)
    {
        var files = Path.Join(trash, "files");
        var info = Path.Join(trash, "info");
        Directory.CreateDirectory(files);
        Directory.CreateDirectory(info);
        if (FileFacts.Of(path, follow: false, out var source) != 0)
        {
            throw new FileNotFoundException($"{path} is not there");
        }
        if (FileFacts.Of(files, follow: true, out var bin) != 0 || bin.Device != source.Device)
        {
            throw new IOException("on a different filesystem than the trash; use permanent deletion");
        }
        var name = FileName(path) ?? throw new IOException("refusing to trash a path without a name");
        var (candidate, trashedName) = UniqueName(files, name);
        // Same device, checked above: a rename, never a copy.
        Move(path, candidate);
        var infoPath = Path.Join(info, trashedName + ".trashinfo");
        try
        {
            File.WriteAllText(infoPath, $"[Trash Info]\nPath={PercentEncode(path)}\nDeletionDate={DateTime.Now:yyyy-MM-ddTHH:mm:ss}\n");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Put it back rather than leave an entry the trash cannot restore.
            Move(candidate, path);
            throw;
        }

        // Both rename(2), which moves a link as itself. File.Move refuses a
        // link to a directory, so that one goes as a directory.
        static void Move(string from, string to)
        {
            if (Directory.Exists(from))
            {
                Directory.Move(from, to);
            }
            else
            {
                File.Move(from, to);
            }
        }
    }

    /// `name`, or `name.1`, `name.2`, … until the name is free in `dir`.
    public static (string Path, string Name) UniqueName(string dir, string name)
    {
        for (var index = 0; index < 10_000; index++)
        {
            var candidate = index == 0 ? name : $"{name}.{index}";
            var path = Path.Join(dir, candidate);
            if (!File.Exists(path) && !Directory.Exists(path))
            {
                return (path, candidate);
            }
        }
        return (Path.Join(dir, name), name);
    }

    /// For a .trashinfo: everything outside the unreserved set is escaped,
    /// so spaces and newlines survive.
    public static string PercentEncode(string input)
    {
        var output = new System.Text.StringBuilder(input.Length);
        foreach (var b in System.Text.Encoding.UTF8.GetBytes(input))
        {
            if (char.IsAsciiLetterOrDigit((char)b) || b is (byte)'/' or (byte)'-' or (byte)'.' or (byte)'_' or (byte)'~')
            {
                output.Append((char)b);
            }
            else
            {
                output.Append('%').Append(b.ToString("X2"));
            }
        }
        return output.ToString();
    }

    // ── removing ────────────────────────────────────────────────────────

    /// Start removing the plan's targets on a worker thread. `backend` and
    /// `trashDir` are for tests: a trash of their own, never the user's.
    public static RemovalHandle Spawn(Plan plan, RemovalMode mode, TrashBackend? backend = null, string? trashDir = null)
    {
        var handle = new RemovalHandle();
        var trash = mode == RemovalMode.Trash ? backend ?? DetectTrashBackend() : TrashBackend.Unavailable;
        var worker = new Thread(() => Run(plan, mode, trash, trashDir, handle)) { Name = "disktiles-remove", IsBackground = true };
        worker.Start();
        return handle;
    }

    static void Run(Plan plan, RemovalMode mode, TrashBackend backend, string? trashDir, RemovalHandle handle)
    {
        handle.Events.Enqueue(new RemovalEvent.Start(plan.Targets.Count));
        ulong removed = 0, bytes = 0;
        var failed = 0;
        // The plan was judged against a table up to two seconds old, or none
        // yet: judged again against a fresh read before anything is touched.
        // Links too: one put on the way down since would lead the trash out.
        var mounts = ReadMountTable();
        var home = HomeDir() is { } dir ? Home.Of(dir) : null;
        var realRoot = Realpath(plan.Root);
        var moved = (realRoot is null ? null : GuardKey(realRoot)) != (plan.RealRoot is null ? null : GuardKey(plan.RealRoot))
            ? $"the scanned root {plan.Root} is somewhere else now ({realRoot ?? "gone"}, was {plan.RealRoot}): nothing was touched"
            : null;
        foreach (var target in plan.Targets)
        {
            if (handle.Cancelled)
            {
                break;
            }
            string? error;
            try
            {
                // The /run/media exception must still hold: the disk may
                // have been unmounted since the review was shown.
                error = moved ?? (Space.PathStartsWith(target.Path, "/run/media")
                        ? Refuse(target.Path, plan.Root, home, mounts.Points, mounts.Media)
                        : null)
                    ?? Mounted(target.Path, mounts.Points)
                    ?? Linked(target.Path, plan.Root, realRoot, home);
                if (error is null)
                {
                    if (mode == RemovalMode.Permanent)
                    {
                        RemovePermanently(target.Path, plan.Root, mounts);
                    }
                    else
                    {
                        MoveToTrash(target.Path, backend, trashDir);
                    }
                }
            }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
            {
                error = failure.Message;
            }
            if (error is null)
            {
                removed++;
                bytes += target.Bytes;
            }
            else
            {
                failed++;
            }
            handle.Events.Enqueue(new RemovalEvent.Item(target.Path, target.Bytes, error));
        }
        handle.Events.Enqueue(new RemovalEvent.Done(removed, bytes, failed));
    }

    /// The mount-point rules of Refuse, asked again at removal time.
    public static string? Mounted(string path, IEnumerable<string> mounts) =>
        IsMountPoint(path)
            ? "it became a mount point since it was marked"
            : MountBelow(GuardKey(path), mounts) is { } mount
                ? $"{mount} is mounted inside it: removing it would reach into another filesystem"
                : null;

    static readonly EnumerationOptions Everything = new()
    {
        // Dotfiles too: Unix calls them hidden, and the defaults skip them.
        AttributesToSkip = 0,
        IgnoreInaccessible = false,
        RecurseSubdirectories = false,
    };

    /// `rm -rf --one-file-system`: a link is unlinked, never followed, and
    /// nothing on another filesystem is touched. Every directory from `root`
    /// down to `path` must still be a directory and not a link: the scan
    /// does not follow links, so one there now was put there after it, and
    /// following it would remove what it points to. Links above the root are
    /// followed: they are how the user named it.
    ///
    /// ponytail: a link swapped in between the check and the removal is
    /// still followed; closing that race takes openat(O_NOFOLLOW) at each
    /// step, and managed .NET has no openat.
    /// The worker's Linked check right before narrows it to that window.
    public static void RemovePermanently(string path, string root, MountTable? mounts = null)
    {
        var name = FileName(path) ?? throw new IOException($"{path} is not an absolute, normalized path");
        var below = (Parent(path) is { } parent ? Space.StripPrefix(parent, root) : null)
            ?? throw new IOException($"{path} is not inside the scanned root {root}");
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"the scanned root {root} is gone");
        }
        var dir = root;
        foreach (var part in Space.Components(below))
        {
            if (part is "." or ".." || Space.IsRoot(part))
            {
                throw new IOException($"{path} is not an absolute, normalized path");
            }
            dir = Path.Join(dir, part);
            if (IsLink(dir) || File.Exists(dir))
            {
                throw new IOException($"{path} changed since the scan: {part} is no longer a directory, so nothing was removed");
            }
            if (!Directory.Exists(dir))
            {
                throw new DirectoryNotFoundException($"{dir} is not there");
            }
        }
        var target = Path.Join(dir, name);
        if (IsLink(target) || File.Exists(target))
        {
            File.Delete(target);
            return;
        }
        if (!Directory.Exists(target))
        {
            throw new FileNotFoundException($"{target} is not there", target);
        }
        var volume = Volume.Of(target, mounts ?? ReadMountTable());
        RemoveContents(target, volume);
        Directory.Delete(target, recursive: false);
    }

    /// Where a directory lives, as far as crossing filesystems goes: the
    /// mount table decides. An unmounted Btrfs subvolume has its own st_dev
    /// but is the same mount and goes with its directory; a bind mount keeps
    /// st_dev but is a mount and stops the removal. Without a table the
    /// device decides.
    sealed class Volume(ulong device, HashSet<string>? points)
    {
        public static Volume Of(string dir, MountTable mounts)
        {
            FileFacts.Of(dir, follow: false, out var facts);
            return new Volume(facts.Device, mounts.Points.Count > 0 ? [.. mounts.Points] : null);
        }

        public bool Contains(string dir) => points is not null
            ? !points.Contains(GuardKey(dir))
            : FileFacts.Of(dir, follow: false, out var facts) == 0 && facts.Device == device;
    }

    static void RemoveContents(string dir, Volume top, int depth = 0)
    {
        // Names first, then removal: unlinking while listing may skip entries.
        var entries = new DirectoryInfo(dir).EnumerateFileSystemInfos("*", Everything).ToList();
        foreach (var entry in entries)
        {
            var child = entry.FullName;
            // A link goes as itself: what it leads to is never entered.
            var link = entry.Attributes.HasFlag(FileAttributes.ReparsePoint);
            if (link || entry is not DirectoryInfo)
            {
                File.Delete(child);
                continue;
            }
            if (!top.Contains(child))
            {
                throw new IOException($"stopped at {Space.Normalize(child)}: another filesystem is mounted there, and nothing on it was touched");
            }
            // Thousands of levels deep: on a stack of its own, every thousand.
            if (depth % Tree.LevelsPerStack == Tree.LevelsPerStack - 1)
            {
                Tree.OnFreshStack(() => RemoveContents(child, top, depth + 1));
            }
            else
            {
                RemoveContents(child, top, depth + 1);
            }
            Directory.Delete(child, recursive: false);
        }
    }

    /// A link, by `LinkTarget`.
    static bool IsLink(string path) => Space.LinkTarget(path) is not null;
}
