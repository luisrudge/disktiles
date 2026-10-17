// Mounts as a Linux scan sees them: which mount points below the root are
// another volume (ForeignMounts), and which show a directory the scan already
// reaches (RepeatedMounts). Paths compare by component.
namespace Disktiles.Core;

public sealed record Mount(string Source, string Point, string FsType, string Options);

public sealed record MountView(string Device, string Source, string FsType, string Point, string FsRoot);

/// A mounted volume the picker can switch the scan to.
public sealed record Volume(string Point, string? Device, SpaceInfo? Space);

public static class Space
{
    /// `foreign_mounts_for`: null where /proc/self/mounts cannot be read.
    public static List<string>? ForeignMountsFor(string root)
    {
        if (!OperatingSystem.IsLinux())
        {
            return null;
        }
        string table;
        try
        {
            table = File.ReadAllText("/proc/self/mounts");
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        return ForeignMounts(ParseMounts(table), root);
    }

    public static List<Mount> ParseMounts(string table)
    {
        var mounts = new List<Mount>();
        foreach (var line in table.AsSpan().EnumerateLines())
        {
            var fields = line.ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 3)
            {
                continue;
            }
            // Spaces in mount points are escaped as \040.
            mounts.Add(new Mount(fields[0], fields[1].Replace("\\040", " "), fields[2],
                fields.Length > 3 ? fields[3] : ""));
        }
        return mounts;
    }

    /// Mount points below `root` that are not on `root`'s volume (the mount
    /// source, since btrfs gives every subvolume its own st_dev), plus snapshots.
    public static List<string> ForeignMounts(List<Mount> mounts, string root)
    {
        Mount? own = null;
        foreach (var mount in mounts)
        {
            // `max_by_key` keeps the last of equal keys.
            if (PathStartsWith(root, mount.Point) && (own is null || mount.Point.Length >= own.Point.Length))
            {
                own = mount;
            }
        }
        if (own is null)
        {
            return [];
        }
        var foreign = new List<string>();
        foreach (var mount in mounts)
        {
            if (!PathEquals(mount.Point, root) && PathStartsWith(mount.Point, root)
                && (mount.Source != own.Source || mount.FsType != own.FsType || IsSnapshot(mount)))
            {
                foreign.Add(mount.Point);
            }
        }
        return foreign;
    }

    public static bool IsSnapshot(Mount mount)
    {
        if (Path.GetFileName(mount.Point.TrimEnd('/')) == ".snapshots")
        {
            return true;
        }
        foreach (var option in mount.Options.Split(','))
        {
            if (option.StartsWith("subvol=", StringComparison.Ordinal) && option.Contains("snapshots", StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    /// `repeated_mounts_for`: empty where there is no /proc/self/mountinfo.
    public static List<string> RepeatedMountsFor(string root, string canonical, bool oneFilesystem)
    {
        if (!OperatingSystem.IsLinux())
        {
            return [];
        }
        string table;
        try
        {
            table = File.ReadAllText("/proc/self/mountinfo");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
        var excluded = oneFilesystem ? ForeignMountsFor(canonical) ?? [] : [];
        var repeated = new List<string>();
        foreach (var path in RepeatedMountsExcluding(ParseMountinfo(table), canonical, excluded))
        {
            if (StripPrefix(path, canonical) is { } below)
            {
                repeated.Add(Join(root, below));
            }
        }
        return repeated;
    }

    public static List<MountView> ParseMountinfo(string table)
    {
        var views = new List<MountView>();
        foreach (var line in table.AsSpan().EnumerateLines())
        {
            var text = line.ToString();
            var split = text.IndexOf(" - ", StringComparison.Ordinal);
            if (split < 0)
            {
                continue;
            }
            var left = text[..split].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            var right = text[(split + 3)..].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (left.Length < 5 || right.Length < 2)
            {
                continue;
            }
            views.Add(new MountView(left[2], UnescapeOctal(right[1]), right[0], UnescapeOctal(left[4]),
                UnescapeOctal(left[3])));
        }
        return views;
    }

    /// Backslash and three octal digits, decoded in one pass.
    static string UnescapeOctal(string field)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(field);
        var output = new List<byte>(bytes.Length);
        for (var index = 0; index < bytes.Length;)
        {
            if (bytes[index] == '\\' && index + 4 <= bytes.Length
                && bytes[(index + 1)..(index + 4)] is var digits && digits.All(d => d is >= (byte)'0' and <= (byte)'7')
                && (digits[0] - '0') * 64 + (digits[1] - '0') * 8 + (digits[2] - '0') is var value and <= 255)
            {
                output.Add((byte)value);
                index += 4;
            }
            else
            {
                output.Add(bytes[index]);
                index += 1;
            }
        }
        return System.Text.Encoding.UTF8.GetString([.. output]);
    }

    static bool SameFilesystem(MountView left, MountView right) =>
        left.FsType == right.FsType
        && (left.Device == right.Device
            // Btrfs assigns device numbers per subvolume; its block device
            // identifies the filesystem shared by those different roots.
            || (left.FsType == "btrfs" && left.Source.StartsWith("/dev/", StringComparison.Ordinal)
                && left.Source == right.Source));

    static MountView? CoveringMount(List<MountView> mounts, string path)
    {
        MountView? best = null;
        foreach (var mount in mounts)
        {
            if (PathStartsWith(path, mount.Point)
                && (best is null || Components(mount.Point).Count >= Components(best.Point).Count))
            {
                best = mount;
            }
        }
        return best;
    }

    static List<string> RepeatedMountsExcluding(List<MountView> mounts, string root, List<string> excluded)
    {
        var own = CoveringMount(mounts, root);
        var visible = mounts
            .Where(mount => (PathStartsWith(mount.Point, root) || mount == own)
                && !excluded.Any(skip => PathStartsWith(mount.Point, skip)))
            .OrderBy(mount => Components(mount.FsRoot).Count)
            .ThenBy(mount => Components(mount.Point).Count)
            .ThenBy(mount => mount.Point, ComponentComparer.Instance)
            .ToList();
        var kept = new List<MountView>();
        var repeated = new List<string>();
        foreach (var mount in visible)
        {
            string? elsewhere = null;
            foreach (var wider in kept)
            {
                if (!SameFilesystem(wider, mount) || StripPrefix(mount.FsRoot, wider.FsRoot) is not { } inside)
                {
                    continue;
                }
                var path = Join(wider.Point, inside);
                if (!PathStartsWith(path, root) || PathEquals(path, mount.Point)
                    || excluded.Concat(repeated).Any(skip => PathStartsWith(path, skip)))
                {
                    continue;
                }
                // Another mount can hide the supposed original directory.
                if (CoveringMount(mounts, path) is not { } covering || StripPrefix(path, covering.Point) is not { } relative)
                {
                    continue;
                }
                if (SameFilesystem(covering, mount) && PathEquals(Join(covering.FsRoot, relative), mount.FsRoot))
                {
                    elsewhere = path;
                    break;
                }
            }
            var skip = elsewhere is null
                ? null
                : !PathEquals(mount.Point, root) && PathStartsWith(mount.Point, root) ? mount.Point : elsewhere;
            if (skip is not null && !PathStartsWith(root, skip)
                // A duplicate view can contain independently mounted data.
                && !mounts.Any(child => !PathEquals(child.Point, skip) && PathStartsWith(child.Point, skip)))
            {
                if (!PathEquals(skip, mount.Point))
                {
                    kept.Add(mount);
                }
                repeated.Add(skip);
            }
            else
            {
                kept.Add(mount);
            }
        }
        return repeated;
    }

    static List<Mount>? MountTable()
    {
        if (!OperatingSystem.IsLinux())
        {
            return null;
        }
        try
        {
            return ParseMounts(File.ReadAllText("/proc/self/mounts"));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// `volume_root`: the shortest mount point above `path` with the same
    /// source as the one it is on. On Omarchy, `/home` (the @home subvolume)
    /// answers `/`.
    public static string? VolumeRoot(List<Mount> mounts, string path)
    {
        Mount? own = null;
        foreach (var mount in mounts)
        {
            if (PathStartsWith(path, mount.Point) && (own is null || mount.Point.Length >= own.Point.Length))
            {
                own = mount;
            }
        }
        if (own is null)
        {
            return null;
        }
        Mount? top = null;
        foreach (var mount in mounts)
        {
            if (mount.Source == own.Source && mount.FsType == own.FsType && PathStartsWith(path, mount.Point)
                && (top is null || mount.Point.Length < top.Point.Length))
            {
                top = mount;
            }
        }
        return top?.Point;
    }

    /// `volume_root_for` this machine: from the mount table on Linux; elsewhere
    /// the longest drive root above it (macOS's Data volume is `/`'s).
    public static string? VolumeRootFor(string path)
    {
        path = Space.RealPath(path) ?? path;
        if (OperatingSystem.IsLinux())
        {
            return MountTable() is { } mounts ? VolumeRoot(mounts, path) : null;
        }
        if (OperatingSystem.IsWindows())
        {
            return Path.GetPathRoot(path);
        }
        string? best = null;
        foreach (var drive in DriveInfo.GetDrives())
        {
            var point = drive.RootDirectory.FullName;
            if (PathStartsWith(path, point) && (best is null || point.Length > best.Length))
            {
                best = point;
            }
        }
        return best == MacDataVolume ? "/" : best;
    }

    const string MacDataVolume = "/System/Volumes/Data";

    /// `volumes`: every volume worth offering as a scan root, fullest first.
    public static List<Volume> Volumes()
    {
        if (OperatingSystem.IsLinux())
        {
            return MountTable() is { } mounts ? VolumesIn(mounts) : [];
        }
        // macOS and Windows: what the runtime lists as a ready fixed,
        // removable or network drive, less macOS's second view of Data.
        var volumes = DriveInfo.GetDrives()
            .Where(drive => drive.DriveType is DriveType.Fixed or DriveType.Removable or DriveType.Network
                && drive.RootDirectory.FullName != MacDataVolume && drive.IsReady)
            .Select(drive => new Volume(drive.RootDirectory.FullName, null, SpaceInfo.Of(drive.RootDirectory.FullName)))
            .ToList();
        SortByFreeSpace(volumes);
        return volumes;
    }

    /// `volumes_in`: one entry per device (a btrfs disk at `/`, `/home`,
    /// `/var/log` is one volume, at its shortest mount point), without pseudo
    /// filesystems, snapshots or automount points.
    public static List<Volume> VolumesIn(List<Mount> mounts)
    {
        var seen = new List<Mount>();
        foreach (var mount in mounts)
        {
            if (!IsVolumeCandidate(mount))
            {
                continue;
            }
            var known = seen.FindIndex(other => other.Source == mount.Source && other.FsType == mount.FsType);
            if (known < 0)
            {
                seen.Add(mount);
            }
            else if (mount.Point.Length < seen[known].Point.Length)
            {
                seen[known] = mount;
            }
        }
        var volumes = seen.Select(mount => new Volume(mount.Point, mount.Source, SpaceInfo.Of(mount.Point))).ToList();
        SortByFreeSpace(volumes);
        return volumes;
    }

    // The scarcest room is the most interesting to a cleanup tool; an
    // unreadable volume sorts last.
    static void SortByFreeSpace(List<Volume> volumes) => volumes.Sort((left, right) =>
        (left.Space is null).CompareTo(right.Space is null) is not 0 and var unread ? unread
        : (left.Space?.Available ?? 0).CompareTo(right.Space?.Available ?? 0) is not 0 and var free ? free
        : string.CompareOrdinal(left.Point, right.Point));

    static readonly HashSet<string> Pseudo =
    [
        "autofs", "cgroup", "cgroup2", "configfs", "debugfs", "devpts", "devtmpfs", "fuse.portal", "fusectl",
        "hugetlbfs", "mqueue", "nsfs", "overlay", "proc", "pstore", "securityfs", "sysfs", "tmpfs", "tracefs",
        // What an Omarchy install also mounts, which would otherwise be
        // offered as volumes.
        "binfmt_misc", "bpf", "efivarfs", "fuse.gvfsd-fuse", "ramfs", "rpc_pipefs",
    ];

    static bool IsVolumeCandidate(Mount mount) =>
        !IsSnapshot(mount)
        && !mount.Options.Split(',').Any(option => option == "automounted" || option.StartsWith("autofs", StringComparison.Ordinal))
        && !Pseudo.Contains(mount.FsType);

    /// `realpath(3)`: absolute, every link resolved; null if it is missing.
    /// On Windows the final path Windows itself opens (Win32.FinalPath).
    public static string? RealPath(string path) => OperatingSystem.IsWindows() ? Removal.Realpath(path) : Native.RealPath(path);

    /// `path` made absolute, lexically: on Windows Path.GetFullPath trims
    /// `junk.` to `junk`, another folder. Only its root (none, `C:` or `\`)
    /// goes through GetFullPath, which knows the current directories.
    public static string Absolute(string path)
    {
        if (Path.IsPathFullyQualified(path))
        {
            return Removal.Normalize(path);
        }
        var root = Path.GetPathRoot(path) ?? "";
        return Removal.Normalize(Path.Join(Path.GetFullPath(root.Length > 0 ? root : "."), path[root.Length..]));
    }

    /// The spelling Win32 takes literally. Plain Win32 paths drop a trailing
    /// dot or space, so `C:\a\junk.` opens `C:\a\junk`; `\\?\C:\a\junk.` opens
    /// `junk.`. Every file call on a path from the tree goes through this.
    /// Elsewhere, and for anything not fully qualified, the path itself.
    public static string Literal(string path)
    {
        if (!OperatingSystem.IsWindows() || path.StartsWith(@"\\?\", StringComparison.Ordinal) || !Path.IsPathFullyQualified(path))
        {
            return path;
        }
        var full = path.Replace('/', '\\');
        return full.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + full[2..] : @"\\?\" + full;
    }

    /// What the link at `path` points to; null when it is not a link, or not
    /// there at all (where Windows would throw for a missing folder above it).
    public static string? LinkTarget(string path)
    {
        try
        {
            return new FileInfo(Literal(path)).LinkTarget;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// How names compare: by case on Linux and macOS's case-sensitive
    /// volumes alike (the walk keeps names as found), without it on Windows.
    /// ponytail: a Windows folder made case-sensitive (fsutil
    /// setCaseSensitiveInfo) can hold Foo and foo, which marks and coverage
    /// take for one: one may stay unremoved, or be covered by the other's
    /// mark. A known limit; asking each folder for its flag would lift it.
    public static readonly StringComparer Names = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    static readonly char[] Separators = OperatingSystem.IsWindows() ? ['\\', '/'] : ['/'];

    /// The root component: `/`, or on Windows a drive or share root such as
    /// `C:\` (as Components spells it).
    public static bool IsRoot(string part) => part == "/" || (OperatingSystem.IsWindows() && part.EndsWith('\\'));

    /// Path by components: "/a//b/" is ["/", "a", "b"]; on Windows
    /// "c:/a\\b" is ["C:\\", "a", "b"] (C# string spelling).
    public static List<string> Components(string path)
    {
        var parts = new List<string>();
        var rest = path;
        if (OperatingSystem.IsWindows())
        {
            // `\\?\C:\x` and `\\.\C:\x` are C:\x without Win32's parsing; so
            // the guards see one spelling, they become it. `\\?\UNC\s\x` is
            // `\\s\x`. Any other device path (`\\?\GLOBALROOT\`,
            // `\\?\Volume{…}\`) keeps its own root, which no guard accepts.
            if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            {
                path = @"\\" + path[8..];
            }
            else if ((path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal))
                && path.Length >= 7 && char.IsAsciiLetter(path[4]) && path[5] == ':' && path[6] is '\\' or '/')
            {
                path = path[4..];
            }
            rest = path;
            if (Path.GetPathRoot(path) is { Length: > 0 } root && Path.IsPathFullyQualified(path))
            {
                var spelled = root.Replace('/', '\\').TrimEnd('\\') + "\\";
                parts.Add(spelled.Length > 1 && spelled[1] == ':' ? char.ToUpperInvariant(spelled[0]) + spelled[1..] : spelled);
                rest = path[root.Length..];
            }
        }
        else if (path.StartsWith('/'))
        {
            parts.Add("/");
        }
        foreach (var part in rest.Split(Separators, StringSplitOptions.RemoveEmptyEntries))
        {
            if (part != ".")
            {
                parts.Add(part);
            }
        }
        return parts;
    }

    public static string Normalize(string path) => Join(Components(path));

    /// The path the components spell: a root, then the rest joined by the
    /// platform's separator.
    public static string Join(IReadOnlyList<string> parts) => parts.Count > 0 && IsRoot(parts[0])
        ? parts[0] + string.Join(Path.DirectorySeparatorChar, parts.Skip(1))
        : string.Join(Path.DirectorySeparatorChar, parts);

    /// `PathBuf::join`, normalized; an empty `below` is the base itself.
    public static string Join(string path, string below) =>
        below.Length == 0 ? Normalize(path) : Components(below) is [var root, ..] && IsRoot(root) ? Normalize(below) : Normalize(Path.Join(path, below));

    public static bool PathEquals(string left, string right) => Components(left).SequenceEqual(Components(right), Names);

    public static bool PathStartsWith(string path, string prefix)
    {
        var (whole, head) = (Components(path), Components(prefix));
        return head.Count <= whole.Count && whole.Take(head.Count).SequenceEqual(head, Names);
    }

    /// `Path::strip_prefix`: what is left, "" for the path itself, null if not a prefix.
    public static string? StripPrefix(string path, string prefix)
    {
        var (whole, head) = (Components(path), Components(prefix));
        return head.Count <= whole.Count && whole.Take(head.Count).SequenceEqual(head, Names)
            ? string.Join(Path.DirectorySeparatorChar, whole.Skip(head.Count))
            : null;
    }

    sealed class ComponentComparer : IComparer<string>
    {
        public static readonly ComponentComparer Instance = new();

        public int Compare(string? left, string? right)
        {
            var (a, b) = (Components(left!), Components(right!));
            for (var i = 0; i < Math.Min(a.Count, b.Count); i++)
            {
                var order = Names.Compare(a[i], b[i]);
                if (order != 0)
                {
                    return order;
                }
            }
            return a.Count.CompareTo(b.Count);
        }
    }
}
