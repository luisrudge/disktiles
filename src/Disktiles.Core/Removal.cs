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
// Paths are strings compared by component (Space.Components). On Windows the
// guards fold case, `\\?\` spellings and short names away, refuse Windows'
// own folders, profiles, links and cloud placeholders, and the Recycle Bin
// is the shell's own (RecycleBin.cs), which asks before it would destroy.
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Enumeration;

namespace Disktiles.Core;

/// One path the user asked to remove; `Bytes` as measured when marked.
/// `Where`: what Removal.VolumeOf said of it when marked, so a mark is never
/// removed from another disk or folder that took its name since.
public sealed record Target(string Path, ulong Bytes, bool IsDir, bool Hidden, Removal.RootId? Where = null);

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
    /// The scanned root's disk and place (Removal.VolumeOf), read as the
    /// scan started: everything is refused if Root is elsewhere now.
    public Removal.RootId? Volume { get; init; }
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
public enum TrashBackend { MacOs, TrashPut, Gio, XdgHome, RecycleBin, Unavailable }

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
    public const string MacosDataVolume = "/System/Volumes/Data";

    public static string Label(this RemovalMode mode) =>
        mode == RemovalMode.Permanent ? "Delete permanently" : "Move to trash";

    public static string Detail(this RemovalMode mode) =>
        mode == RemovalMode.Permanent ? "rm -rf: unrecoverable" : "recoverable until the trash is emptied";

    /// Build the plan for `targets`, which must all live under `root`: the
    /// tree the user was looking at is the only thing they consented to.
    /// `walk`, when given, judges each directory target's contents too
    /// (CloudInside, or what a walk found before): asked once per target.
    public static Plan Plan(IReadOnlyList<Target> targets, string root, Func<string, string?>? walk = null, RootId? volume = null) =>
        PlanAgainst(targets, root, MountPoints(), HomeDir(), walk, volume);

    /// Plan against a given read of the mount table and home, for testing.
    /// A name that was not valid UTF-8 reaches us with U+FFFD in place of
    /// its bytes (the tree decodes names lossily), so its path names another
    /// file, or none: a sibling really named with U+FFFD would be removed
    /// instead. A real U+FFFD in a name is refused too; that is the price.
    static string? Undecodable(string path) => path.Contains('\uFFFD')
        ? "its name is not valid UTF-8, so its path is not exact; remove it by hand"
        : null;

    public static Plan PlanAgainst(IReadOnlyList<Target> targets, string root, MountTable mounts, string? home,
        Func<string, string?>? walk = null, RootId? volume = null)
    {
        root = Normalize(root);
        var realRoot = Realpath(root);
        var plan = new Plan { Root = root, RealRoot = realRoot, Volume = volume };
        if (targets.Count == 0)
        {
            return plan;
        }
        // Once per plan, not per target.
        var homeInfo = home is null ? null : Home.Of(home);
        var swapped = Swapped(root, volume);
        var accepted = new List<Target>();
        foreach (var target in targets)
        {
            var path = Normalize(target.Path);
            if ((swapped ?? Remarked(path, target.Where) ?? Undecodable(path) ?? Refuse(path, root, homeInfo, mounts.Points, mounts.Media)
                ?? Linked(path, root, realRoot, homeInfo) ?? walk?.Invoke(path)) is { } reason)
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
    /// One list for every Unix: a macOS tree never exists on Linux, and the
    /// other way round. `/Applications` is left out: moving an app to the
    /// Trash is how macOS uninstalls it.
    static readonly string[] SystemTrees =
    [
        "/bin", "/boot", "/dev", "/etc", "/lib", "/lib64", "/lib32", "/libx32", "/nix/store", "/gnu/store",
        "/proc", "/run", "/sbin", "/sys", "/usr", "/var/lib", "/efi",
        // Homebrew on Linux, outside every user's home.
        "/home/linuxbrew/.linuxbrew",
        // macOS.
        "/System", "/Library", "/private", "/cores", "/opt/homebrew", "/Network",
    ];

    /// The trees with their guard keys, computed once.
    static readonly (string Name, string Key)[] SystemTreeKeys =
        [.. SystemTrees.Select(tree => (tree, GuardKey(tree)))];

    const string SystemTools = "remove it with the tool that installed it";

    /// How the guards compare paths: lexically normalized and, on macOS, in
    /// one of its spellings: the Data volume folded into `/`, and without
    /// case, since its disks ignore it. Only for judging: what is removed is
    /// always the path as marked.
    public static string GuardKey(string path) => OperatingSystem.IsWindows()
        ? Normalize(path).ToLowerInvariant()
        : GuardKeyFor(path, OperatingSystem.IsMacOS());

    public static string GuardKeyFor(string path, bool macos)
    {
        var normal = Normalize(path);
        if (!macos)
        {
            return normal;
        }
        // The Data volume's own root stays as it is: it is under /System.
        if (Space.StripPrefix(normal, MacosDataVolume) is { Length: > 0 } rest)
        {
            normal = "/" + rest;
        }
        return normal.ToLowerInvariant();
    }

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

    /// A system tree strictly inside `key`: removing `/opt` takes `/opt/homebrew`.
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

    /// Home: `HOME` on Unix. On Windows always the real profile folder: MSYS2,
    /// Cygwin and editors set `HOME` to somewhere else, which would leave the
    /// profile unguarded.
    public static string? HomeDir() =>
        OperatingSystem.IsWindows()
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) is { Length: > 0 } profile ? profile : null
            : Environment.GetEnvironmentVariable("HOME") is { Length: > 0 } home ? home
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
        if (OperatingSystem.IsWindows() && RefuseOnWindows(path, key, home) is { } windows)
        {
            return windows;
        }
        if (key == homeKey || (home?.Id is { } id && Identity(path, follow: false) == id))
        {
            return "the home directory cannot be removed";
        }
        // On Linux /home is usually its own mount and refused as one; on
        // macOS /Users is on /'s volume, and removing it empties home first.
        if (homeKey is not null && Space.PathStartsWith(homeKey, key))
        {
            return "it contains the home directory";
        }
        // By spelling too: the Data volume's root keeps its name as a key.
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

    // ── Windows ─────────────────────────────────────────────────────────

    /// What Windows owns, as the system names it on this machine: its own
    /// folder, the program folders, ProgramData, and what the system drive
    /// keeps at its root for itself.
    /// Declared first: static fields start in the order they are written.
    static readonly string CompatibilityFolder =
        Path.Join(Path.GetPathRoot(Environment.SystemDirectory) is { Length: > 0 } root ? root : @"C:\", "Documents and Settings");

    static readonly (string Name, string Key)[] WindowsTreeKeys = OperatingSystem.IsWindows()
        ? [
            .. WindowsTrees().SelectMany(tree => Keys(tree).Select(key => (tree, key))),
            // A junction to the profiles folder, kept for old programs: by
            // name only, since where it leads has the profiles' own rules.
            (CompatibilityFolder, GuardKey(CompatibilityFolder)),
        ]
        : [];

    /// A protected folder's guard keys: as named, and where it really is,
    /// since a junction can move C:\Users or Program Files to another drive.
    static IEnumerable<string> Keys(string path)
    {
        var key = GuardKey(path);
        yield return key;
        if (Win32.FinalPath(path, out _) is { } final && GuardKey(final) is var real && real != key)
        {
            yield return real;
        }
    }

    /// What Windows keeps at a drive's root, as guard keys: every NTFS
    /// drive's own folders, and the boot and recovery files of a system,
    /// EFI or recovery partition given a letter (`mountvol S: /s`).
    static readonly string[] VolumeFolders =
        ["$recycle.bin", "system volume information", "efi", "boot", "recovery", "bootmgr", "bootnxt", "bootsect.bak"];

    static IEnumerable<string> WindowsTrees()
    {
        Environment.SpecialFolder[] folders =
        [
            Environment.SpecialFolder.Windows, Environment.SpecialFolder.ProgramFiles,
            Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.CommonApplicationData,
        ];
        foreach (var folder in folders)
        {
            if (Environment.GetFolderPath(folder) is { Length: > 0 } path)
            {
                yield return path;
            }
        }
        // ARM64 Windows has a third program folder, with no special folder.
        if (Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) is { Length: > 0 } programs
            && Path.GetDirectoryName(programs) is { } above && Path.Join(above, "Program Files (Arm)") is var arm
            && Directory.Exists(arm))
        {
            yield return arm;
        }
        var system = Path.GetPathRoot(Environment.SystemDirectory) is { Length: > 0 } root ? root : @"C:\";
        foreach (var name in new[] { "Config.Msi", "$WinREAgent", "$SysReset" })
        {
            yield return Path.Join(system, name);
        }
    }

    /// Every profile Windows keeps (ProfileList in the registry) and the
    /// folder they live in, as guard keys: profiles need not be in C:\Users.
    /// Default, which every new account is copied from, is one too. Public
    /// is not: it is the users' shared files, theirs to clean (the folder
    /// itself is still refused, as a child of the profiles folder).
    static readonly (string[] Folders, string[] Profiles) WindowsProfiles = ReadProfiles();

    static (string[], string[]) ReadProfiles()
    {
        if (!OperatingSystem.IsWindows())
        {
            return ([], []);
        }
        try
        {
            using var list = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList");
            if (list is null)
            {
                return ([], []);
            }
            string[] folders = list.GetValue("ProfilesDirectory") is string directory ? [.. Keys(Environment.ExpandEnvironmentVariables(directory))] : [];
            var profiles = new List<string>();
            if (list.GetValue("Default") is string { Length: > 0 } template)
            {
                profiles.AddRange(Keys(Environment.ExpandEnvironmentVariables(template)));
            }
            foreach (var name in list.GetSubKeyNames())
            {
                using var profile = list.OpenSubKey(name);
                if (profile?.GetValue("ProfileImagePath") is string { Length: > 0 } image)
                {
                    profiles.AddRange(Keys(Environment.ExpandEnvironmentVariables(image)));
                }
            }
            return (folders, [.. profiles]);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return ([], []);
        }
    }

    const string NetworkShare = "on a network share: remove it on the computer that has it";
    const string UnusualForm = "an unusual path form, which the guards cannot place; scan the drive by its letter";

    static bool IsDriveLetter(string root) => root.Length == 3 && char.IsAsciiLetter(root[0]) && root[1] == ':' && root[2] == '\\';

    /// Only local drives: a share (even `\\localhost\C$`, which is C:\) and a
    /// mapped drive are another computer's folders, or this one's by another
    /// name, and device paths (`\\?\GLOBALROOT\…`) are not placed at all.
    static string? NotLocal(string key)
    {
        if (DriveOf(key) is not { } root)
        {
            return UnusualForm;
        }
        if (!IsDriveLetter(root))
        {
            return root.StartsWith(@"\\", StringComparison.Ordinal) && !root.StartsWith(@"\\?\", StringComparison.Ordinal) && !root.StartsWith(@"\\.\", StringComparison.Ordinal)
                ? NetworkShare
                : UnusualForm;
        }
        try
        {
            return new DriveInfo(root).DriveType is DriveType.Network ? NetworkShare : null;
        }
        catch (Exception error) when (error is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return UnusualForm;
        }
    }

    /// Windows' own rules: on the path as spelled, then on the final path
    /// Windows opens for it, and last on the file's attributes.
    static string? RefuseOnWindows(string path, string key, Home? home)
    {
        if ((NotLocal(key) ?? Spelled(key, home)) is { } reason)
        {
            return reason;
        }
        // The same folder under another name: a subst drive, a junction.
        var final = Win32.FinalPath(path, out var error);
        if (final is null)
        {
            // Missing: removing it fails on its own. Anything else (a volume
            // without a drive letter, no permission): refused.
            return error is Win32.FileNotFound or Win32.PathNotFound && !Path.Exists(Space.Literal(path))
                ? null
                : $"where it really is cannot be checked ({Errors.Text(error)})";
        }
        var finalKey = GuardKey(final);
        if (finalKey != key && (NotLocal(finalKey) ?? Spelled(finalKey, home)) is { } real)
        {
            return $"{real} (it is really {final})";
        }
        if (Win32.InSyncRoot(path))
        {
            return $"in a cloud sync folder (OneDrive or similar): deleting it here deletes the cloud copy too; {InProvider}";
        }
        return LinkOrPlaceholder(path);
    }

    /// The rules on one spelling of the target.
    static string? Spelled(string key, Home? home)
    {
        var homeKey = home?.Key;
        var parts = Space.Components(key);
        if (parts.Skip(1).FirstOrDefault(MisreadByWin32) is { } misread)
        {
            return $"Windows would read {misread} as a different name, and remove that";
        }
        if (parts.Skip(1).FirstOrDefault(IsShortName) is { } shortName)
        {
            return $"{shortName} looks like a short (8.3) name, which the guards cannot place; scan the folder by its full name";
        }
        if (parts.Count > 1 && VolumeFolders.Contains(parts[1]))
        {
            return $"part of the system: Windows keeps {parts[1]} at a drive's root";
        }
        foreach (var (name, tree) in WindowsTreeKeys)
        {
            if (Space.PathStartsWith(key, tree))
            {
                return $"part of the system under {name}: {SystemTools}";
            }
            if (Space.PathStartsWith(tree, key))
            {
                return $"it contains {name}, which is part of the system";
            }
        }
        var (folders, profiles) = WindowsProfiles;
        foreach (var profile in profiles)
        {
            // Home has its own rules: inside it is the user's to clean.
            if (profile == homeKey || profile == home?.Real)
            {
                continue;
            }
            if (Space.PathStartsWith(key, profile))
            {
                return "inside another user's profile";
            }
            if (Space.PathStartsWith(profile, key))
            {
                return $"it contains the user profile {profile}";
            }
        }
        // C:\Users\Public and any other folder there: a profile by place.
        // By component, without case: Parent re-spells the drive letter.
        foreach (var profilesFolder in folders is [] && homeKey is not null && Parent(homeKey) is { } above ? [above] : folders)
        {
            if (Space.PathEquals(key, profilesFolder))
            {
                return "the folder that holds every user profile";
            }
            if (key != homeKey && key != home?.Real && Parent(key) is { } parent && Space.PathEquals(parent, profilesFolder))
            {
                return "a user profile: remove accounts in Settings";
            }
        }
        foreach (var variable in OneDriveVariables)
        {
            if (Environment.GetEnvironmentVariable(variable) is not { Length: > 0 } sync || !Path.IsPathFullyQualified(sync))
            {
                continue;
            }
            foreach (var syncKey in Keys(sync))
            {
                if (Space.PathStartsWith(key, syncKey))
                {
                    return $"inside OneDrive ({sync}): deleting it here deletes the cloud copy too; {InOneDrive}";
                }
                if (Space.PathStartsWith(syncKey, key))
                {
                    return $"it contains OneDrive ({sync}), and deleting that deletes the cloud copies too; {InOneDrive}";
                }
            }
        }
        return null;
    }

    /// Where OneDrive keeps its own sync roots: a check by name, with
    /// Win32.InSyncRoot (inside one) and CloudInside (one inside) for the rest.
    static readonly string[] OneDriveVariables = ["OneDrive", "OneDriveConsumer", "OneDriveCommercial"];

    const string InOneDrive = "remove it in OneDrive";
    const string InProvider = "remove it in the provider";

    /// `PROGRA~1`: a name Windows also accepts for `Program Files`. The scan
    /// lists long names, so one here was typed, and could hide a system tree.
    public static bool IsShortName(string name)
    {
        var tilde = name.IndexOf('~');
        if (tilde is < 1 or > 6)
        {
            return false;
        }
        var digits = name.AsSpan(tilde + 1);
        var dot = digits.IndexOf('.');
        var number = dot < 0 ? digits : digits[..dot];
        return number.Length > 0 && !number.ContainsAnyExceptInRange('0', '9') && (dot < 0 || digits.Length - dot - 1 <= 3);
    }

    public const FileAttributes RecallOnOpen = (FileAttributes)0x40000;
    const FileAttributes RecallOnDataAccess = (FileAttributes)0x400000;

    /// OneDrive (and other cloud providers) keep files online only behind
    /// these: deleting one deletes the cloud copy too.
    public static bool IsPlaceholder(FileAttributes attributes) =>
        (attributes & (FileAttributes.Offline | RecallOnOpen | RecallOnDataAccess)) != 0;

    /// A target that is a reparse point (a junction, a symbolic link, a mount
    /// point) or a cloud placeholder. Refused: the shell recycles what a link
    /// leads to, and a placeholder takes its cloud copy with it.
    static string? LinkOrPlaceholder(string path)
    {
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(Space.Literal(path));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Missing: removing it fails on its own.
            return null;
        }
        if (IsPlaceholder(attributes))
        {
            return "a cloud placeholder (OneDrive or similar): deleting it here deletes the cloud copy too; remove it in the provider";
        }
        return (attributes & FileAttributes.ReparsePoint) != 0
            ? "a link (a junction or symbolic link): remove it in Explorer, which removes the link and not what it leads to"
            : null;
    }

    /// A cloud placeholder or sync root anywhere inside the directory `path`:
    /// the Recycle Bin would take the cloud copies, and a permanent removal
    /// would stop at the first placeholder halfway. Links are not entered,
    /// as the removal never enters them. A folder that cannot be read cannot
    /// be checked, so it refuses too. One walk of the whole target, one
    /// Cloud Files call per folder: asked when the review opens and by the
    /// worker, never on every mark. Elsewhere, and for files, null.
    public static string? CloudInside(string path)
    {
        var literal = Space.Literal(path);
        if (!OperatingSystem.IsWindows() || !Directory.Exists(literal))
        {
            return null;
        }
        try
        {
            // The constructor opens the folder: inside the try.
            var walk = new FileSystemEnumerable<string>(literal, (ref entry) => entry.ToFullPath(), WalkAll)
            {
                // A link is removed as itself: where it leads is not asked.
                ShouldIncludePredicate = (ref entry) => IsPlaceholder(entry.Attributes)
                    || (entry.IsDirectory && Win32.InSyncRoot(entry.ToFullPath())
                        && ((entry.Attributes & FileAttributes.ReparsePoint) == 0 || Space.LinkTarget(entry.ToFullPath()) is null)),
                ShouldRecursePredicate = (ref entry) => (entry.Attributes & FileAttributes.ReparsePoint) == 0,
            };
            return walk.FirstOrDefault() is { } found
                ? $"it holds {Space.Normalize(found)}, a cloud placeholder or sync folder (OneDrive or similar): deleting it deletes the cloud copies too; {InProvider}"
                : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return $"a folder inside cannot be read, so it cannot be checked for cloud placeholders ({error.Message})";
        }
    }

    static readonly EnumerationOptions WalkAll = new()
    {
        AttributesToSkip = 0,
        IgnoreInaccessible = false,
        RecurseSubdirectories = true,
    };

    static readonly string[] NumberedDevices = ["com", "lpt"];
    static readonly string[] DeviceNumbers = ["1", "2", "3", "4", "5", "6", "7", "8", "9", "\u00b9", "\u00b2", "\u00b3"];

    /// A name Win32 reads as another: trailing dots and spaces are dropped,
    /// `NUL` or `COM1` is a device in any directory, and after a colon comes
    /// a stream. NTFS keeps such names, but the removal opens them via Win32.
    public static bool MisreadByWin32(string name)
    {
        if (name.EndsWith('.') || name.EndsWith(' ') || name.Contains(':'))
        {
            return true;
        }
        // `nul.txt` was a device too, before Windows 11: refused either way.
        var stem = name.Split('.')[0].TrimEnd().ToLowerInvariant();
        var numbered = NumberedDevices.Any(device => stem.StartsWith(device, StringComparison.Ordinal)
            && DeviceNumbers.Contains(stem[device.Length..]));
        return numbered || stem is "con" or "prn" or "aux" or "nul";
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
            // On Windows a folder that is there but has no final path (a
            // volume mounted without a letter) cannot be placed: refused.
            return OperatingSystem.IsWindows() && Directory.Exists(Space.Literal(parent))
                ? "its folder cannot be resolved, so where it really is cannot be checked"
                : null;
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
        !OperatingSystem.IsWindows() && FileFacts.Of(path, follow, out var facts) == 0 ? (facts.Device, facts.Inode) : null;

    /// Whether `path` is a directory on another device than its parent.
    public static bool IsMountPoint(string path)
    {
        if (OperatingSystem.IsWindows() || FileFacts.Of(path, follow: false, out var facts) != 0 || !facts.IsDir)
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

    /// The table as last read. Never blocks on the read (on macOS it runs
    /// `mount`): a stale table starts one on the pool and is used meanwhile.
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
        var linux = OperatingSystem.IsLinux();
        var mounts = linux && ReadText("/proc/self/mounts") is { } text ? Space.ParseMounts(text) : null;
        if (linux && ReadText("/proc/self/mountinfo") is { } info)
        {
            var views = Space.ParseMountinfo(info);
            return new([.. views.Select(view => GuardKey(view.Point))], MediaRoots(views), mounts);
        }
        if (mounts is not null)
        {
            return new([.. mounts.Select(mount => GuardKey(mount.Point))], [], mounts);
        }
        return new(OperatingSystem.IsMacOS() ? [.. MacosMountPoints().Select(GuardKey)] : [], [], null);
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

    /// macOS has no /proc: `mount` prints `/dev/disk3s5 on /System/Volumes/Data (apfs, …)`.
    /// The point is between the first " on " and the last " (".
    static List<string> MacosMountPoints()
    {
        try
        {
            using var mount = Process.Start(new ProcessStartInfo("/sbin/mount")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            })!;
            var output = mount.StandardOutput.ReadToEnd();
            mount.WaitForExit();
            return mount.ExitCode == 0 ? ParseMacosMounts(output) : [];
        }
        catch (Exception error) when (error is IOException or System.ComponentModel.Win32Exception)
        {
            return [];
        }
    }

    public static List<string> ParseMacosMounts(string output)
    {
        var points = new List<string>();
        foreach (var line in output.Split('\n'))
        {
            var on = line.IndexOf(" on ", StringComparison.Ordinal);
            var open = line.LastIndexOf(" (", StringComparison.Ordinal);
            if (on >= 0 && open > on + 4)
            {
                points.Add(line[(on + 4)..open]);
            }
        }
        return points;
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
            return AttributedByPlatform(root, path);
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

    /// No table: the device number. Equal is the same filesystem; on Linux a
    /// different one may still be a Btrfs subvolume of it, which only the
    /// table could tell, so nothing is claimed. ponytail: macOS compares
    /// devices, not volume roots; the Data volume's firmlinks share /'s
    /// device, which is what makes that hold.
    static Attribution AttributedByPlatform(string root, string path)
    {
        // Windows: a path's drive or share root is its volume. (A volume
        // mounted in a folder is a reparse point, never descended into.)
        if (OperatingSystem.IsWindows())
        {
            return (DriveOf(root), DriveOf(path)) is (string rootDrive, string pathDrive)
                ? Space.Names.Equals(rootDrive, pathDrive) ? Attribution.Scanned : Attribution.Other
                : Attribution.Unknown;
        }
        if (Identity(root, follow: true) is not (var own, _) || Identity(path, follow: true) is not (var other, _))
        {
            return Attribution.Unknown;
        }
        return own == other ? Attribution.Scanned : OperatingSystem.IsMacOS() ? Attribution.Other : Attribution.Unknown;
    }

    static string? DriveOf(string path) => Space.Components(path) is [var first, ..] && Space.IsRoot(first) ? first : null;

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
        if (OperatingSystem.IsWindows())
        {
            return Win32.FinalPath(path, out _);
        }
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
        TrashBackend.MacOs => "the Trash",
        TrashBackend.TrashPut => "trash-put",
        TrashBackend.Gio => "gio trash",
        TrashBackend.XdgHome => "XDG trash",
        TrashBackend.RecycleBin => "the Recycle Bin",
        _ => "no trash tool found",
    };

    public static string Detail(this TrashBackend backend) => backend switch
    {
        TrashBackend.MacOs => "the same Trash as Finder, on the item's own disk",
        TrashBackend.TrashPut => "uses trash-cli, the same trash as your file manager",
        TrashBackend.Gio => "uses GLib, the same trash as your file manager",
        TrashBackend.XdgHome => "moves into ~/.local/share/Trash on the same volume",
        TrashBackend.RecycleBin => "the same Recycle Bin as File Explorer",
        _ => "install trash-cli or keep deleting permanently",
    };

    public static bool IsAvailable(this TrashBackend backend) => backend != TrashBackend.Unavailable;

    /// The best trash on this machine. On macOS the system Trash, never the
    /// Linux tools: Homebrew's trash-put writes the XDG layout, which Finder
    /// never shows. Linux: trash-put, then gio (what Omarchy's Nautilus
    /// uses), then the XDG layout implemented here.
    public static TrashBackend DetectTrashBackend()
    {
        if (OperatingSystem.IsWindows())
        {
            return TrashBackend.RecycleBin;
        }
        if (OperatingSystem.IsMacOS())
        {
            // ponytail: /usr/bin/trash is macOS 15+; older macOS would need
            // NSFileManager, which is not reachable without interop.
            return File.Exists(MacosTrash) ? TrashBackend.MacOs : TrashBackend.Unavailable;
        }
        return Which("trash-put") ? TrashBackend.TrashPut
            : Which("gio") ? TrashBackend.Gio
            : HomeTrashDir() is not null ? TrashBackend.XdgHome
            : TrashBackend.Unavailable;
    }

    const string MacosTrash = "/usr/bin/trash";

    static bool Which(string program) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries)
            .Any(dir => File.Exists(Path.Join(dir, program)));

    static string? HomeTrashDir() =>
        Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } data ? Path.Join(data, "Trash")
        : Environment.GetEnvironmentVariable("HOME") is { Length: > 0 } home ? Path.Join(home, ".local/share/Trash")
        : null;

    public static void MoveToTrash(string path, TrashBackend backend)
    {
        switch (backend)
        {
            case TrashBackend.TrashPut: RunTool("trash-put", [], path); break;
            case TrashBackend.Gio: RunTool("gio", ["trash"], path); break;
            case TrashBackend.XdgHome:
                TrashInto(path, HomeTrashDir() ?? throw new IOException("neither XDG_DATA_HOME nor HOME is set"));
                break;
            case TrashBackend.MacOs: TrashViaMacos(path); break;
            case TrashBackend.RecycleBin: RecycleBin.Recycle(path); break;
            default: throw new IOException("no trash tool is installed; use permanent deletion instead");
        }
    }

    /// macOS's own `trash` (NSFileManager, per-volume Trash). It takes no
    /// `--`, reading it as a file name, so only an absolute path goes in:
    /// one starting with '/' can never be read as an option.
    static void TrashViaMacos(string path)
    {
        if (!path.StartsWith('/'))
        {
            throw new IOException($"{path} is not absolute, which the Trash cannot take safely");
        }
        try
        {
            RunTool(MacosTrash, ["-s"], path, separator: false);
        }
        catch (IOException error)
        {
            throw new IOException($"could not move to the Trash ({error.Message}); network and read-only disks have none, so use permanent deletion there");
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
        if (OperatingSystem.IsWindows())
        {
            throw new IOException("the XDG trash is only implemented on Unix");
        }
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

    /// Start removing the plan's targets on a worker thread.
    public static RemovalHandle Spawn(Plan plan, RemovalMode mode)
    {
        var handle = new RemovalHandle();
        var backend = mode == RemovalMode.Trash ? DetectTrashBackend() : TrashBackend.Unavailable;
        var worker = new Thread(() => Run(plan, mode, backend, handle)) { Name = "disktiles-remove", IsBackground = true };
        if (OperatingSystem.IsWindows())
        {
            // The shell's file operations expect a single-threaded apartment.
            worker.SetApartmentState(ApartmentState.STA);
        }
        worker.Start();
        return handle;
    }

    static void Run(Plan plan, RemovalMode mode, TrashBackend backend, RemovalHandle handle)
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
            : Swapped(plan.Root, plan.Volume);
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
                // On Windows every guard again: a target swapped for a
                // junction, or made a placeholder, since the review.
                error = moved ?? Remarked(target.Path, target.Where) ?? (OperatingSystem.IsWindows() || Space.PathStartsWith(target.Path, "/run/media")
                        ? Refuse(target.Path, plan.Root, home, mounts.Points, mounts.Media)
                        : null)
                    ?? Mounted(target.Path, mounts.Points)
                    ?? Linked(target.Path, plan.Root, realRoot, home)
                    ?? CloudInside(target.Path);
                if (error is null)
                {
                    if (mode == RemovalMode.Permanent)
                    {
                        RemovePermanently(target.Path, plan.Root, mounts);
                    }
                    else
                    {
                        MoveToTrash(target.Path, backend);
                    }
                }
            }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
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

    /// What tells the disk `root` is on from another under the same name,
    /// and where on it the root is (Win32.Identify), so a card swapped for
    /// another that gets the same letter, or a re-pointed subst drive, is
    /// caught. Cards cloned byte for byte still match: the remaining limit.
    public sealed record RootId(string Disk, string Real)
    {
        /// What a mark of a path that could not be read carries: it never
        /// matches, so that mark is never removed.
        public static readonly RootId Unknown = new("", "");

        /// Real without case (Space.Names): the final path comes back in
        /// the case on disk, and a case-only rename moves nothing.
        public bool Equals(RootId? other) => other is not null && Disk == other.Disk && Space.Names.Equals(Real, other.Real);

        public override int GetHashCode() => HashCode.Combine(Disk, Space.Names.GetHashCode(Real));
    }

    /// ponytail: Windows only, null elsewhere, where the mount path names
    /// the disk; two cards with one label would need (st_dev, st_ino) and more.
    public static RootId? VolumeOf(string root) =>
        OperatingSystem.IsWindows() && Win32.Identify(root) is { } id ? new RootId(id.Disk, id.Real) : null;

    static string? Swapped(string root, RootId? volume) =>
        volume is not null && VolumeOf(root) is var now && now != volume
            ? $"{root} is another disk or folder now ({now?.Real ?? "gone"}) than the one scanned: scan it again"
            : null;

    /// A mark whose path names another disk or folder than when it was marked.
    static string? Remarked(string path, RootId? where) =>
        where is not null && VolumeOf(path) is var now && now != where
            ? $"not what was marked: it is on another disk or folder now ({now?.Real ?? "gone"}); scan again and mark it again"
            : null;

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
    /// followed: they are how the user named it (macOS's /tmp is one).
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
        if (!Directory.Exists(Space.Literal(root)))
        {
            throw new DirectoryNotFoundException($"the scanned root {root} is gone");
        }
        // Literal spellings from here on: on Windows `junk.` must not open `junk`.
        var dir = Space.Literal(root);
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
            DeleteEntry(target, link: IsLink(target), directory: Directory.Exists(target));
            return;
        }
        if (!Directory.Exists(target))
        {
            throw new FileNotFoundException($"{target} is not there", target);
        }
        var volume = Volume.Of(target, mounts ?? ReadMountTable());
        RemoveContents(target, volume);
        DeleteDirectory(target);
    }

    /// Where a directory lives, as far as crossing filesystems goes. On Linux
    /// the mount table decides: an unmounted Btrfs subvolume has its own
    /// st_dev but is the same mount and goes with its directory; a bind mount
    /// keeps st_dev but is a mount and stops the removal. Without a table
    /// (macOS) the device decides.
    sealed class Volume(ulong device, HashSet<string>? points)
    {
        public static Volume Of(string dir, MountTable mounts)
        {
            FileFacts.Of(dir, follow: false, out var facts);
            return new Volume(facts.Device, OperatingSystem.IsLinux() && mounts.Points.Count > 0 ? [.. mounts.Points] : null);
        }

        /// On Windows always: a volume mounted in a folder is a reparse
        /// point, removed as a link before this is asked.
        public bool Contains(string dir) => OperatingSystem.IsWindows() || (points is not null
            ? !points.Contains(GuardKey(dir))
            : FileFacts.Of(dir, follow: false, out var facts) == 0 && facts.Device == device);
    }

    static void RemoveContents(string dir, Volume top, int depth = 0)
    {
        // Names first, then removal: unlinking while listing may skip entries.
        var entries = new DirectoryInfo(dir).EnumerateFileSystemInfos("*", Everything).ToList();
        foreach (var entry in entries)
        {
            var child = entry.FullName;
            if (OperatingSystem.IsWindows() && IsPlaceholder(entry.Attributes))
            {
                throw new IOException($"stopped at {Space.Normalize(child)}: a cloud placeholder, and deleting it would delete the cloud copy; nothing more was touched");
            }
            // A reparse point (a link, a junction, a mount point) goes as
            // itself: what it leads to is never entered.
            var link = entry.Attributes.HasFlag(FileAttributes.ReparsePoint);
            if (link || entry is not DirectoryInfo)
            {
                DeleteEntry(child, link, entry is DirectoryInfo);
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
            DeleteDirectory(child);
        }
    }

    /// A link (any reparse point, on Windows), by `LinkTarget` or, for the
    /// kinds .NET does not resolve, by its attribute.
    static bool IsLink(string path)
    {
        if (Space.LinkTarget(path) is not null)
        {
            return true;
        }
        try
        {
            return OperatingSystem.IsWindows() && (File.GetAttributes(Space.Literal(path)) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// A file, or a link as itself. Windows removes a link to a directory
    /// (a junction too, even a broken one) as an empty directory, which
    /// removes the link alone, and deletes a read-only file (git's objects)
    /// only when told to ignore the flag, or once it is off.
    static void DeleteEntry(string path, bool link, bool directory)
    {
        if (OperatingSystem.IsWindows())
        {
            if (link && directory)
            {
                DeleteDirectory(path);
                return;
            }
            if (Win32.DeleteIgnoringReadOnly(path))
            {
                return;
            }
            File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
        }
        File.Delete(path);
    }

    /// An empty directory, or a link to one. Windows refuses a read-only
    /// folder (Downloads, Music, any with a custom icon) until the flag is off.
    static void DeleteDirectory(string dir)
    {
        if (OperatingSystem.IsWindows() && File.GetAttributes(dir) is var attributes && (attributes & FileAttributes.ReadOnly) != 0)
        {
            File.SetAttributes(dir, attributes & ~FileAttributes.ReadOnly);
        }
        Directory.Delete(dir, recursive: false);
    }
}
