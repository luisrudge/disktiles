// The tests of Removal, Export and Marks, against fixture
// trees this program makes in the temp folder and removes when done. Nothing outside
// that fixture is ever removed, trashed or moved: plans against real paths
// (/, home, /etc) only plan, and the trash tools are fake scripts.
using System.Diagnostics;
using Disktiles.Core;

var fixtures = Path.Join(OperatingSystem.IsWindows() ? Path.GetTempPath() : "/tmp", $"disktiles-removal-tests-{Environment.ProcessId}");
Directory.CreateDirectory(fixtures);
var (passed, failed) = (0, 0);
var linux = OperatingSystem.IsLinux();
var macos = OperatingSystem.IsMacOS();
var windows = OperatingSystem.IsWindows();

// Removal is off on Windows, and these tests are written for Unix paths:
// there, only the tests marked `windows` run.
void Test(string name, Action body, bool when = true, bool onWindows = false)
{
    if (!when || (windows && !onWindows))
    {
        Console.WriteLine($"skip  {name}");
        return;
    }
    try
    {
        body();
        passed++;
        Console.WriteLine($"ok    {name}");
    }
    catch (Exception error)
    {
        failed++;
        Console.WriteLine($"FAIL  {name}: {error.Message}");
    }
}

static void Assert(bool condition, string what)
{
    if (!condition)
    {
        throw new InvalidOperationException(what);
    }
}

static void Equal<T>(T expected, T actual, string what = "")
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"{what} expected <{expected}>, got <{actual}>");
    }
}

// a/b/, a/one.bin (10 B), a/c.bin (20 B), other/.
string Fixture()
{
    var root = Path.Join(fixtures, Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(Path.Join(root, "a/b"));
    File.WriteAllBytes(Path.Join(root, "a/one.bin"), new byte[10]);
    File.WriteAllBytes(Path.Join(root, "a/c.bin"), new byte[20]);
    Directory.CreateDirectory(Path.Join(root, "other"));
    return root;
}

string Empty()
{
    var dir = Path.Join(fixtures, Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(dir);
    return dir;
}

static Target T(string path, ulong bytes) => new(path, bytes, Directory.Exists(path), false);

static void LinkDir(string target, string link) => Directory.CreateSymbolicLink(link, target);

// A junction: what Windows makes without admin rights, and what a profile
// already holds (Application Data, My Documents).
static void Junction(string link, string target) => Cmd("mklink", "/J", link, target);

static void Cmd(params string[] arguments)
{
    var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
    start.ArgumentList.Add("/c");
    foreach (var argument in arguments)
    {
        start.ArgumentList.Add(argument);
    }
    using var command = Process.Start(start)!;
    command.WaitForExit();
    if (command.ExitCode != 0)
    {
        throw new InvalidOperationException($"{string.Join(' ', arguments)} failed: {command.StandardError.ReadToEnd()}{command.StandardOutput.ReadToEnd()}");
    }
}

static string Expect<TError>(Action body) where TError : Exception
{
    try
    {
        body();
    }
    catch (TError error)
    {
        return error.Message;
    }
    throw new InvalidOperationException($"expected {typeof(TError).Name}");
}

List<RemovalEvent> Drain(RemovalHandle handle)
{
    var events = new List<RemovalEvent>();
    var watch = Stopwatch.StartNew();
    while (watch.ElapsedMilliseconds < 5000 && events.LastOrDefault() is not RemovalEvent.Done)
    {
        while (handle.Poll(out var next))
        {
            events.Add(next!);
        }
        Thread.Sleep(1);
    }
    return events;
}

var noMounts = (IReadOnlyCollection<string>)[];

// ── Removal ─────────────────────────────────────────────────────────────

Test("desktop_media_exception_requires_an_independent_disk", () =>
{
    var views = Space.ParseMountinfo("""
        1 0 8:1 / / rw - ext4 /dev/root rw
        2 1 0:20 / /run rw - tmpfs tmpfs rw
        3 2 8:2 / /run/media/user/Games rw - ext4 /dev/sdb rw
        4 2 8:1 / /run/media/user/System rw - ext4 /dev/root rw
        5 2 8:2 /folder /run/media/user/Bind rw - ext4 /dev/sdb rw
        6 2 0:21 / /run/media/user/Virtual rw - tmpfs tmpfs rw
        7 3 8:1 /etc /run/media/user/Games/nested rw - ext4 /dev/root rw
        """);
    var media = Removal.MediaRoots(views);
    Assert(media.SequenceEqual(["/run/media/user/Games"]), string.Join(",", media));
    var points = views.Select(view => view.Point).ToList();
    Assert(Removal.OnMedia("/run/media/user/Games/game", points, media), "game on media");
    foreach (var path in new[]
    {
        "/run/media/user/Games", "/run/media/user/Games/nested/file", "/run/media/user/System/etc",
        "/run/media/user/Bind/file", "/run/media/user/Virtual/file", "/run/media/user/NotMounted/file", "/run/user/1000/file",
    })
    {
        Assert(!Removal.OnMedia(path, points, media), path);
    }
    var stacked = views.ToList();
    stacked.Add(views[3] with { Point = "/run/media/user/Games" });
    Assert(Removal.MediaRoots(stacked).Count == 0, "overmounted disk");
    Assert(Removal.MediaRoots([]).Count == 0, "empty");
});

Test("a_plan_keeps_only_the_outermost_targets", () =>
{
    var root = Fixture();
    var inner = T(Path.Join(root, "a/b"), 5);
    var outer = T(Path.Join(root, "a"), 30);
    var separate = T(Path.Join(root, "other"), 0);
    var plan = Removal.Plan([inner, outer, separate], root);
    Equal(2, plan.Targets.Count);
    Assert(plan.Targets.Contains(outer) && plan.Targets.Contains(separate), "outer and separate");
    Equal(1, plan.Covered.Count);
    Equal(inner.Path, plan.Covered[0].Path);
    Equal(30UL, plan.Bytes);
});

Test("only_the_scanned_volumes_marks_are_projected", () =>
{
    const string table = """
        /dev/mapper/root / btrfs rw,subvol=/@ 0 0
        /dev/mapper/root /home btrfs rw,subvol=/@home 0 0
        /dev/mapper/root /.snapshots btrfs rw,subvol=/@snapshots 0 0
        /dev/mapper/data /data ext4 rw 0 0
        """;
    var parsed = Space.ParseMounts(table);
    var mounts = new MountTable([.. parsed.Select(mount => Removal.GuardKey(mount.Point))], [], parsed);
    var plan = Removal.PlanAgainst([T("/home/user/junk", 100), T("/data/junk", 200), T("/.snapshots/7/junk", 400)], "/", mounts, Removal.HomeDir());
    Assert(plan.Blocked.Count == 0, "a mark inside another volume is legal: " + string.Join("; ", plan.Blocked));
    Equal(700UL, plan.Bytes);
    Equal(100UL, plan.Reclaim, "the Btrfs subvolume is this disk");
    Equal(600UL, plan.Foreign, "another disk, and a snapshot");
    Equal(0UL, plan.Unattributed);

    // A mark inside another goes with it: counted once, as in the total.
    plan = Removal.PlanAgainst([T("/home/user/junk", 100), T("/home/user/junk/deeper", 60)], "/", mounts, Removal.HomeDir());
    Equal(1, plan.Covered.Count);
    Equal(100UL, plan.Bytes);
    Equal(100UL, plan.Reclaim);

    // Where the table cannot place the paths, nothing is claimed.
    var partial = new MountTable([], [], Space.ParseMounts("/dev/mapper/data /data ext4 rw 0 0\n"));
    plan = Removal.PlanAgainst([T("/disktree-untabled/junk", 50)], "/disktree-untabled", partial, Removal.HomeDir());
    Equal(50UL, plan.Bytes);
    Equal(0UL, plan.Reclaim);
    Equal(0UL, plan.Foreign);
    Equal(50UL, plan.Unattributed, "no saving may be claimed");
});

Test("windows_paths_by_component", () =>
{
    Equal(4, Space.Components(@"c:/Users\luis\\x").Count);
    Equal(@"C:\", Space.Components(@"c:/Users\luis\\x")[0]);
    Equal(@"C:\Users\luis\x", Space.Normalize(@"c:/Users\luis\\x\"));
    Equal(@"C:\", Space.Normalize(@"C:\"));
    Assert(Space.PathStartsWith(@"C:\USERS\luis\x", @"c:\users"), "names compare without case");
    Equal(@"luis\x", Space.StripPrefix(@"C:\Users\luis\x", @"C:\Users"));
    Equal(@"C:\Users\luis", Removal.Normalize(@"C:\Users\luis\x\.."));
    Equal(@"C:\", Removal.Parent(@"C:\Users"));
    Equal(null, Removal.Parent(@"C:\"));
}, when: windows, onWindows: true);

Test("windows_system_folders_are_refused", () =>
{
    var home = Removal.Home.Of(@"C:\Users\someone");
    var windir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    var programs = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
    var data = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
    foreach (var (path, root) in new[]
    {
        (Path.Join(windir, "Temp", "x"), windir),
        (Path.Join(windir, "Temp", "x").ToUpperInvariant(), windir.ToLowerInvariant()),
        (@"\\?\" + Path.Join(windir, "x"), windir),
        (Path.Join(programs, "App"), programs),
        (Path.Join(data, "App"), data),
        (@"C:\$Recycle.Bin\S-1-5", @"C:\"),
        (@"D:\System Volume Information\x", @"D:\"),
        (@"S:\EFI\Microsoft\Boot", @"S:\"),
        (@"R:\Recovery", @"R:\"),
        (@"S:\bootmgr", @"S:\"),
        (@"S:\BOOTSECT.BAK", @"S:\"),
    })
    {
        var reason = Removal.Refuse(path, root, home, noMounts, noMounts);
        Assert(reason is not null && reason.Contains("system"), $"{path}: {reason}");
    }
    Assert(Removal.Refuse(@"C:\", @"C:\", home, noMounts, noMounts) is not null, "the drive root");
}, when: windows, onWindows: true);

Test("windows_profiles_short_and_misread_names_are_refused", () =>
{
    var home = Removal.Home.Of(@"C:\Users\someone");
    string? Why(string path, string root) => Removal.Refuse(path, root, home, noMounts, noMounts);
    Assert(Why(@"C:\Users\Public", @"C:\Users")?.Contains("profile") == true, $"another profile: {Why(@"C:\Users\Public", @"C:\Users")}");
    Assert(Why(@"C:\USERS\SOMEONE", @"C:\Users")?.Contains("home") == true, "home, in another case");
    Assert(Why(@"C:\Users", @"C:\")?.Contains("profile") == true, "the profiles folder");
    Assert(Why(@"C:\PROGRA~1\App", @"C:\")?.Contains("short") == true, "a short name");
    Assert(Why(@"C:\Users\someone\nul.txt", @"C:\Users\someone")?.Contains("different name") == true, "a device name");
    Equal(null, Why(@"C:\Users\someone\junk", @"C:\Users\someone"), "an ordinary folder in home");
    Assert(Removal.IsShortName("PROGRA~1") && Removal.IsShortName("DOCUME~1.TXT"), "short names");
    Assert(!Removal.IsShortName("my~notes") && !Removal.IsShortName("a~1.html5") && !Removal.IsShortName("~1"), "long names");
}, when: windows, onWindows: true);

Test("windows_links_and_placeholders_are_refused", () =>
{
    var root = Fixture();
    var outside = Empty();
    File.WriteAllText(Path.Join(outside, "keep.txt"), "keep");
    // The real home: the fixture is in the temp folder, inside it.
    var home = Removal.Home.Of(Removal.HomeDir()!);
    Junction(Path.Join(root, "link"), outside);
    Assert(Removal.Refuse(Path.Join(root, "link"), root, home, noMounts, noMounts)?.Contains("link") == true, $"a junction: {Removal.Refuse(Path.Join(root, "link"), root, home, noMounts, noMounts)}");
    var file = Path.Join(root, "a", "one.bin");
    File.SetAttributes(file, File.GetAttributes(file) | FileAttributes.Offline);
    Assert(Removal.Refuse(file, root, home, noMounts, noMounts)?.Contains("placeholder") == true, "a placeholder");
    File.SetAttributes(file, FileAttributes.Normal);
    var plan = Removal.PlanAgainst([T(Path.Join(root, "link", "keep.txt"), 4)], root, MountTable.Empty, Removal.HomeDir());
    Equal(1, plan.Blocked.Count, "a path through the junction");
    Assert(plan.Blocked[0].Reason.Contains("through a link"), plan.Blocked[0].Reason);
}, when: windows, onWindows: true);

Test("windows_permanent_removal_never_follows_a_junction", () =>
{
    var root = Fixture();
    var outside = Empty();
    File.WriteAllText(Path.Join(outside, "keep.txt"), "keep");
    var victim = Path.Join(root, "a");
    Junction(Path.Join(victim, "out"), outside);
    var readOnly = Path.Join(victim, "read-only.txt");
    File.WriteAllText(readOnly, "x");
    File.SetAttributes(readOnly, FileAttributes.ReadOnly);
    Removal.RemovePermanently(victim, root);
    Assert(!Directory.Exists(victim), "removed, read-only file and all");
    Assert(File.Exists(Path.Join(outside, "keep.txt")), "what the junction led to is untouched");
}, when: windows, onWindows: true);

Test("windows_bytes_count_toward_the_drive_they_are_on", () =>
{
    var plan = Removal.PlanAgainst([T(@"C:\Users\someone\junk", 100)], @"C:\Users\someone", MountTable.Empty, @"C:\Users\someone");
    Equal(100UL, plan.Reclaim, "same drive");
    plan = Removal.PlanAgainst([T(@"C:\Users\someone\junk", 100)], @"c:\users\someone", MountTable.Empty, @"C:\Users\someone");
    Equal(100UL, plan.Reclaim, "same drive, another case");
}, when: windows, onWindows: true);

Test("windows_names_ending_in_a_dot_are_their_own", () =>
{
    var root = Empty();
    var plain = Path.Join(root, "a", "junk");
    var dotted = plain + ".";
    Directory.CreateDirectory(plain);
    File.WriteAllText(Path.Join(plain, "keep.txt"), "keep");
    Directory.CreateDirectory(Space.Literal(dotted));
    File.WriteAllText(Space.Literal(Path.Join(dotted, "trash.txt")), "trash");
    var tree = Scanner.Scan(root, new ScanOptions());
    var a = tree.Children[tree.Children.IndexOf("a")];
    var junkDot = a.Children[a.Children.IndexOf("junk.")];
    Equal("trash.txt", junkDot.Children[0].Name, "junk. lists its own file");
    var plan = Removal.PlanAgainst([T(dotted, 5)], root, MountTable.Empty, Removal.HomeDir());
    Equal(1, plan.Blocked.Count, "junk. is refused");
    Removal.RemovePermanently(plain, root);
    Assert(!Directory.Exists(plain) && File.Exists(Space.Literal(Path.Join(dotted, "trash.txt"))), "junk went, junk. stayed");
    Removal.RemovePermanently(Path.Join(root, "a"), root);
    Assert(!Directory.Exists(Space.Literal(Path.Join(root, "a"))), "a went, junk. with it");
}, when: windows, onWindows: true);

Test("windows_home_is_the_profile_whatever_HOME_says", () =>
{
    var saved = Environment.GetEnvironmentVariable("HOME");
    Environment.SetEnvironmentVariable("HOME", @"C:\msys64\home\someone");
    try
    {
        Equal(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Removal.HomeDir());
    }
    finally
    {
        Environment.SetEnvironmentVariable("HOME", saved);
    }
}, when: windows, onWindows: true);

Test("windows_real_profiles_are_refused", () =>
{
    var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    var users = Removal.Parent(profile)!;
    var home = Removal.Home.Of(profile);
    string? Why(string path, string root) => Removal.Refuse(path, root, home, noMounts, noMounts);
    Assert(Why(profile, users) is not null, "home");
    Assert(Why(users, Removal.Parent(users)!) is not null, "the profiles folder");
    Assert(Why(Path.Join(users, "Public"), users)?.Contains("profile") == true, $"Public: {Why(Path.Join(users, "Public"), users)}");
    Equal(null, Why(Path.Join(profile, "disktiles-no-such-folder"), profile), "inside home");
}, when: windows, onWindows: true);

Test("windows_shares_and_device_paths_are_refused", () =>
{
    var home = Removal.Home.Of(@"C:\Users\someone");
    foreach (var (path, root) in new[]
    {
        (@"\\localhost\C$\Windows\System32\notepad.exe", @"\\localhost\C$\Windows"),
        (@"\\?\UNC\localhost\C$\Temp\x", @"\\?\UNC\localhost\C$"),
        (@"\\?\GLOBALROOT\Device\HarddiskVolume3\Temp\x", @"\\?\GLOBALROOT\Device\HarddiskVolume3"),
        (@"\\?\Volume{00000000-0000-0000-0000-000000000000}\Temp\x", @"\\?\Volume{00000000-0000-0000-0000-000000000000}\"),
        (@"C:Temp\x", @"C:Temp"),
    })
    {
        Assert(Removal.Refuse(path, root, home, noMounts, noMounts) is not null, $"{path} allowed");
    }
}, when: windows, onWindows: true);

Test("windows_a_subst_drive_is_judged_by_what_it_names", () =>
{
    var used = DriveInfo.GetDrives().Select(drive => char.ToUpperInvariant(drive.Name[0])).ToHashSet();
    var letter = Enumerable.Range('M', 14).Select(c => (char)c).First(c => !used.Contains(c)) + ":";
    Cmd("subst", letter, Environment.GetFolderPath(Environment.SpecialFolder.Windows));
    try
    {
        var reason = Removal.Refuse($@"{letter}\System32", $@"{letter}\", Removal.Home.Of(@"C:\Users\someone"), noMounts, noMounts);
        Assert(reason?.Contains("system") == true, $"{letter}\\System32: {reason}");
    }
    finally
    {
        Cmd("subst", letter, "/d");
    }
}, when: windows, onWindows: true);

Test("windows_the_worker_refuses_a_target_swapped_for_a_junction", () =>
{
    var root = Fixture();
    var outside = Empty();
    File.WriteAllText(Path.Join(outside, "keep.txt"), "keep");
    var target = Path.Join(root, "a");
    var plan = Removal.PlanAgainst([T(target, 30)], root, MountTable.Empty, Removal.HomeDir());
    Equal(1, plan.Targets.Count, "planned");
    Directory.Delete(target, recursive: true);
    Junction(target, outside);
    var events = Drain(Removal.Spawn(plan, RemovalMode.Permanent));
    Assert(events.OfType<RemovalEvent.Item>().Single().Error is not null, "refused at removal time");
    Assert(File.Exists(Path.Join(outside, "keep.txt")), "what it leads to is untouched");
}, when: windows, onWindows: true);

Test("windows_read_only_folders_are_removed", () =>
{
    var root = Fixture();
    var a = Path.Join(root, "a");
    File.SetAttributes(Path.Join(a, "b"), File.GetAttributes(Path.Join(a, "b")) | FileAttributes.ReadOnly);
    File.SetAttributes(a, File.GetAttributes(a) | FileAttributes.ReadOnly);
    Removal.RemovePermanently(a, root);
    Assert(!Directory.Exists(a), "removed");
}, when: windows, onWindows: true);

Test("windows_following_links_visits_each_folder_once", () =>
{
    var root = Empty();
    var outside = Empty();
    File.WriteAllBytes(Path.Join(outside, "f.bin"), new byte[100]);
    Junction(Path.Join(root, "one"), outside);
    Junction(Path.Join(root, "two"), outside);
    var followed = Scanner.Scan(root, new ScanOptions { FollowLinks = true });
    Equal(1UL, (ulong)followed.Files, "what both lead to, counted once");

    var inside = Empty();
    Directory.CreateDirectory(Path.Join(inside, "sub"));
    File.WriteAllBytes(Path.Join(inside, "sub", "f.bin"), new byte[100]);
    Junction(Path.Join(inside, "back"), Path.Join(inside, "sub"));
    Junction(Path.Join(inside, "sub", "loop"), Path.Join(inside, "sub"));
    var once = Scanner.Scan(inside, new ScanOptions { FollowLinks = true, ApparentSize = true });
    Equal(1UL, (ulong)once.Files, "a link back inside the root, counted where it is");
    Equal(100UL, once.Bytes);
}, when: windows, onWindows: true);

Test("windows_recycle_bin_takes_a_file", () =>
{
    var file = Path.Join(Empty(), $"disktiles-recycle-test-{Guid.NewGuid():N}.txt");
    File.WriteAllText(file, "x");
    Removal.MoveToTrash(file, TrashBackend.RecycleBin);
    Assert(!File.Exists(file), "gone from its folder");
}, when: windows, onWindows: true);

Test("windows_onedrive_is_refused_inside_and_around", () =>
{
    var root = Empty();
    var sync = Path.Join(root, "OneDrive");
    // Named for binclean.ps1, in case the Recycle Bin ever took it.
    var target = Path.Join(sync, $"disktiles-recycle-test-{Guid.NewGuid():N}");
    Directory.CreateDirectory(target);
    var home = Removal.Home.Of(Removal.HomeDir()!);
    var plan = Removal.PlanAgainst([T(target, 0)], root, MountTable.Empty, Removal.HomeDir());
    Equal(1, plan.Targets.Count, "planned before OneDrive was there");
    var saved = Environment.GetEnvironmentVariable("OneDriveConsumer");
    Environment.SetEnvironmentVariable("OneDriveConsumer", sync.ToUpperInvariant());
    try
    {
        Assert(Removal.Refuse(target, root, home, noMounts, noMounts)?.Contains("OneDrive") == true, "inside");
        Assert(Removal.Refuse(sync, root, home, noMounts, noMounts)?.Contains("OneDrive") == true, "the sync root");
        Assert(Removal.Refuse(root, Removal.Parent(root)!, home, noMounts, noMounts)?.Contains("OneDrive") == true, "around it");
        foreach (var mode in new[] { RemovalMode.Permanent, RemovalMode.Trash })
        {
            var item = Drain(Removal.Spawn(plan, mode)).OfType<RemovalEvent.Item>().Single();
            Assert(item.Error?.Contains("OneDrive") == true, $"{mode}: the worker refuses it: {item.Error}");
            Assert(Directory.Exists(target), $"{mode}: untouched");
        }
    }
    finally
    {
        Environment.SetEnvironmentVariable("OneDriveConsumer", saved);
    }
}, when: windows, onWindows: true);

Test("windows_a_placeholder_inside_refuses_the_whole_target", () =>
{
    var root = Empty();
    var target = Path.Join(root, $"disktiles-recycle-test-{Guid.NewGuid():N}");
    var deep = Path.Join(target, "deep");
    Directory.CreateDirectory(deep);
    File.WriteAllBytes(Path.Join(target, "first.bin"), new byte[1]);
    var cloud = Path.Join(deep, "cloud.bin");
    File.WriteAllBytes(cloud, []);
    File.SetAttributes(cloud, FileAttributes.Offline);
    var shallow = Removal.PlanAgainst([T(target, 1)], root, MountTable.Empty, Removal.HomeDir());
    Equal(1, shallow.Targets.Count, "a plan without the walk lets it through");
    var walked = Removal.PlanAgainst([T(target, 1)], root, MountTable.Empty, Removal.HomeDir(), Removal.CloudInside);
    Assert(walked.Blocked.SingleOrDefault()?.Reason.Contains("placeholder") == true, "the walk blocks it");
    foreach (var mode in new[] { RemovalMode.Permanent, RemovalMode.Trash })
    {
        var item = Drain(Removal.Spawn(shallow, mode)).OfType<RemovalEvent.Item>().Single();
        Assert(item.Error?.Contains("placeholder") == true, $"{mode}: the worker refuses it: {item.Error}");
        Assert(File.Exists(Path.Join(target, "first.bin")) && File.Exists(cloud), $"{mode}: nothing touched");
    }
    File.SetAttributes(cloud, FileAttributes.Normal);
}, when: windows, onWindows: true);

Test("windows_the_default_profile_is_refused", () =>
{
    if (!OperatingSystem.IsWindows())
    {
        return;
    }
    using var list = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList")!;
    var template = Environment.ExpandEnvironmentVariables((string)list.GetValue("Default")!);
    var home = Removal.Home.Of(Removal.HomeDir()!);
    var reason = Removal.Refuse(Path.Join(template, "NTUSER.DAT"), Removal.Parent(template)!, home, noMounts, noMounts);
    Assert(reason?.Contains("profile") == true, $"{template}\\NTUSER.DAT: {reason}");
    Assert(Removal.Refuse(@"C:\bootmgr", @"C:\", home, noMounts, noMounts)?.Contains("system") == true, "bootmgr");
}, when: windows, onWindows: true);

Test("windows_onedrive_behind_a_junction_is_refused_where_it_is", () =>
{
    var root = Empty();
    var real = Path.Join(root, "real");
    Directory.CreateDirectory(Path.Join(real, "Documents"));
    Junction(Path.Join(root, "link"), real);
    var saved = Environment.GetEnvironmentVariable("OneDriveConsumer");
    Environment.SetEnvironmentVariable("OneDriveConsumer", Path.Join(root, "link"));
    try
    {
        var home = Removal.Home.Of(Removal.HomeDir()!);
        Assert(Removal.Refuse(Path.Join(real, "Documents"), root, home, noMounts, noMounts)?.Contains("OneDrive") == true, "inside, by its real name");
        Assert(Removal.Refuse(real, root, home, noMounts, noMounts)?.Contains("OneDrive") == true, "the real folder");
    }
    finally
    {
        Environment.SetEnvironmentVariable("OneDriveConsumer", saved);
    }
}, when: windows, onWindows: true);

// The real OneDrive of this machine, if it has one: only planned, never removed.
var oneDrive = Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "OneDrive");
Test("windows_a_sync_root_is_known_without_its_variables", () =>
{
    string[] variables = ["OneDrive", "OneDriveConsumer", "OneDriveCommercial"];
    var saved = variables.Select(Environment.GetEnvironmentVariable).ToArray();
    foreach (var variable in variables)
    {
        Environment.SetEnvironmentVariable(variable, null);
    }
    try
    {
        var profile = Removal.Parent(oneDrive)!;
        var inside = Directory.EnumerateDirectories(oneDrive).FirstOrDefault() ?? oneDrive;
        var reason = Removal.Refuse(inside, profile, Removal.Home.Of(profile), noMounts, noMounts);
        Assert(reason?.Contains("cloud") == true, $"{inside}: {reason}");
        Assert(Removal.CloudInside(profile)?.Contains("sync folder") == true, "the profile holds it");
        // A junction to it is removed as a link: no reason to refuse.
        var project = Empty();
        var link = Path.Join(project, "docs");
        Junction(link, oneDrive);
        try
        {
            Equal(null, Removal.CloudInside(project), "a link into it");
        }
        finally
        {
            Directory.Delete(link);
        }
    }
    finally
    {
        for (var i = 0; i < variables.Length; i++)
        {
            Environment.SetEnvironmentVariable(variables[i], saved[i]);
        }
    }
}, when: windows && Directory.Exists(oneDrive), onWindows: true);

Test("windows_a_share_has_an_identity_of_its_own", () =>
{
    var local = Empty();
    var share = @"\\localhost\" + local[0] + "$" + local[2..];
    var shared = Removal.VolumeOf(share);
    Assert(shared is not null && shared.Real.StartsWith(@"\Device\", StringComparison.Ordinal), $"by its NT device: {shared}");
    Assert(shared != Removal.VolumeOf(local), "a letter moving from the share to the disk shows");
}, when: windows && Directory.Exists(@"\\localhost\C$\Windows"), onWindows: true);

Test("windows_a_rename_of_case_alone_moves_nothing", () =>
{
    var root = Empty();
    var junk = Path.Join(root, "Proj", "junk");
    Directory.CreateDirectory(junk);
    var marked = T(junk, 0) with { Where = Removal.VolumeOf(junk) };
    var volume = Removal.VolumeOf(root);
    Directory.Move(Path.Join(root, "Proj"), Path.Join(root, "Proj-"));
    Directory.Move(Path.Join(root, "Proj-"), Path.Join(root, "proj"));
    Equal(1, Removal.Plan([marked], root, volume: volume).Targets.Count, "still the same folder");
    Assert(Removal.VolumeOf(junk) != Removal.RootId.Unknown, "a mark that could not be read never matches");
}, when: windows, onWindows: true);

Test("windows_another_disk_under_the_same_letter_is_refused", () =>
{
    // A card swapped for another that gets the same letter, as a subst
    // drive re-pointed from one folder to another.
    var cardA = Empty();
    var cardB = Empty();
    Directory.CreateDirectory(Path.Join(cardA, "DCIM"));
    Directory.CreateDirectory(Path.Join(cardB, "DCIM"));
    File.WriteAllText(Path.Join(cardB, "DCIM", "keep.txt"), "keep");
    var used = DriveInfo.GetDrives().Select(drive => char.ToUpperInvariant(drive.Name[0])).ToHashSet();
    var letter = Enumerable.Range('M', 14).Select(c => (char)c).First(c => !used.Contains(c)) + ":";
    var root = letter + @"\";
    Cmd("subst", letter, cardA);
    try
    {
        var scanned = Removal.VolumeOf(root);
        Assert(scanned is not null && scanned.Disk.Split(' ')[^1].Length == 8, $"the disk, serial and all: {scanned}");
        Equal(Removal.VolumeOf(cardA)!.Disk, scanned!.Disk, "the drive it is a folder of");
        Equal(1, Removal.Plan([T(Path.Join(root, "DCIM"), 0)], root, volume: scanned).Targets.Count, "the same card");
        var early = Removal.Plan([T(Path.Join(root, "DCIM"), 0)], root, volume: scanned);
        var marked = T(Path.Join(root, "DCIM"), 0) with { Where = Removal.VolumeOf(Path.Join(root, "DCIM")) };
        Cmd("subst", letter, "/d");
        Cmd("subst", letter, cardB);
        Assert(Removal.VolumeOf(root) is { } after && after.Disk == scanned.Disk && after.Real != scanned.Real, "the same disk, another folder");
        var plan = Removal.Plan([T(Path.Join(root, "DCIM"), 0)], root, volume: scanned);
        Assert(plan.Blocked.SingleOrDefault()?.Reason.Contains("scan it again") == true, "planned on the other card");
        // Scanned again: the root matches the new card, the mark does not.
        var rescanned = Removal.Plan([marked], root, volume: Removal.VolumeOf(root));
        Assert(rescanned.Blocked.SingleOrDefault()?.Reason.Contains("not what was marked") == true, $"a mark from the other card: {string.Join("; ", rescanned.Blocked)}");
        var item = Drain(Removal.Spawn(early, RemovalMode.Permanent)).OfType<RemovalEvent.Item>().Single();
        Assert(item.Error is not null, "the worker refuses it");
        Assert(File.Exists(Path.Join(cardB, "DCIM", "keep.txt")), "the other card is untouched");
    }
    finally
    {
        Cmd("subst", letter, "/d");
    }
}, when: windows, onWindows: true);

Test("windows_the_worker_refuses_a_root_swapped_for_a_junction", () =>
{
    var parent = Empty();
    var root = Path.Join(parent, "root");
    var other = Path.Join(parent, "other");
    Directory.CreateDirectory(Path.Join(root, "a", "b"));
    Directory.CreateDirectory(Path.Join(other, "a", "b"));
    File.WriteAllText(Path.Join(other, "a", "b", "keep.txt"), "keep");
    var plan = Removal.PlanAgainst([T(Path.Join(root, "a", "b"), 0)], root, MountTable.Empty, Removal.HomeDir());
    Equal(1, plan.Targets.Count, "planned");
    Directory.Move(root, root + "-old");
    Junction(root, other);
    var item = Drain(Removal.Spawn(plan, RemovalMode.Permanent)).OfType<RemovalEvent.Item>().Single();
    Assert(item.Error?.Contains("somewhere else") == true, $"refused: {item.Error}");
    Assert(File.Exists(Path.Join(other, "a", "b", "keep.txt")), "the other folder is untouched");
}, when: windows, onWindows: true);

Test("windows_online_only_files_take_no_space", () =>
{
    var root = Empty();
    var cloud = Path.Join(root, "cloud.bin");
    File.WriteAllBytes(cloud, new byte[4096]);
    File.WriteAllBytes(Path.Join(root, "local.bin"), new byte[100]);
    File.SetAttributes(cloud, FileAttributes.Offline);
    Equal(100UL, Scanner.Scan(root, new ScanOptions()).Bytes, "only the local file");
    File.SetAttributes(cloud, FileAttributes.Normal);
}, when: windows, onWindows: true);

Test("windows_the_recycle_bin_takes_a_page_without_its_files_folder", () =>
{
    // Both named for binclean.ps1, in case the Recycle Bin took the folder.
    var page = Path.Join(Empty(), $"disktiles-recycle-test-{Guid.NewGuid():N}");
    File.WriteAllText(page + ".htm", "x");
    Directory.CreateDirectory(page + "_files");
    Removal.MoveToTrash(page + ".htm", TrashBackend.RecycleBin);
    Assert(!File.Exists(page + ".htm"), "the page went");
    Assert(Directory.Exists(page + "_files"), "its unmarked folder stayed");
}, when: windows, onWindows: true);

Test("windows_removal_leaves_hard_links_outside_read_only", () =>
{
    var root = Fixture();
    var origin = Path.Join(Empty(), "pack.idx");
    File.WriteAllText(origin, "x");
    File.SetAttributes(origin, FileAttributes.ReadOnly);
    Cmd("mklink", "/H", Path.Join(root, "a", "pack.idx"), origin);
    Removal.RemovePermanently(Path.Join(root, "a"), root);
    Assert(!Directory.Exists(Path.Join(root, "a")), "removed");
    Assert(File.GetAttributes(origin).HasFlag(FileAttributes.ReadOnly), "the other link is still read-only");
    File.SetAttributes(origin, FileAttributes.Normal);
}, when: windows, onWindows: true);

Test("windows_a_root_ending_in_a_dot_stays_itself", () =>
{
    var root = Empty();
    var dotted = Path.Join(root, "junk.");
    Directory.CreateDirectory(Path.Join(root, "junk"));
    Directory.CreateDirectory(Space.Literal(dotted));
    var absolute = Space.Absolute(dotted);
    Equal(dotted, absolute, "absolute");
    Assert(Space.RealPath(absolute) is { } real && Space.PathEquals(real, dotted), $"real: {Space.RealPath(absolute)}");
    Assert(Path.IsPathFullyQualified(Space.Absolute("rel")) && Space.Absolute("rel").EndsWith("rel", StringComparison.Ordinal), "a relative path still resolves");
    var saved = Environment.CurrentDirectory;
    Environment.CurrentDirectory = root;
    try
    {
        Assert(Space.PathEquals(Space.Absolute("junk."), dotted), $"relative: {Space.Absolute("junk.")}");
        Assert(Space.PathEquals(Space.Absolute(@".\junk."), dotted), $"relative: {Space.Absolute(@".\junk.")}");
        Assert(Space.PathEquals(Space.Absolute(root[..2] + "junk."), dotted), "drive-relative");
    }
    finally
    {
        Environment.CurrentDirectory = saved;
    }
}, when: windows, onWindows: true);

Test("windows_a_time_past_year_9999_is_read_as_unknown", () =>
{
    var root = Empty();
    var file = Path.Join(root, "future.bin");
    File.WriteAllBytes(file, new byte[10]);
    using (var handle = File.OpenHandle(file, FileMode.Open, FileAccess.Write))
    {
        var future = 0x3000000000000000L;
        Assert(SetFileTime(handle, 0, 0, ref future), "set the time");
    }
    Equal(10UL, Scanner.Scan(root, new ScanOptions { ApparentSize = true }).Bytes, "scanned");
    Equal(0, FileFacts.Of(file, follow: false, out _), "stated");

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    static extern bool SetFileTime(Microsoft.Win32.SafeHandles.SafeFileHandle file, nint created, nint accessed, ref long written);
}, when: windows, onWindows: true);

Test("windows_a_tree_6500_levels_deep_scans_and_goes", () =>
{
    var root = Empty();
    var top = Path.Join(root, "d");
    var deep = string.Join('\\', [top, .. Enumerable.Repeat("d", 6499)]);
    Directory.CreateDirectory(Space.Literal(deep));
    // Big enough for Insights to walk all the way down; sparse, so instant.
    using (var big = new FileStream(Space.Literal(Path.Join(deep, "f.bin")), FileMode.CreateNew))
    {
        big.SetLength(128L << 20);
    }
    var tree = Scanner.Scan(root, new ScanOptions { ApparentSize = true });
    Equal(1UL, (ulong)tree.Files, "scanned and classified");
    Equal(1UL, (ulong)Tree.Resorted(tree, Metric.Files).Files, "aggregated again");
    Equal(0, Filter.Run(tree, [], "zzz")!.Count, "filtered");
    Insights.WorthALook(tree, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), 5);
    Removal.RemovePermanently(top, root);
    Assert(!Directory.Exists(top), "removed");
}, when: windows, onWindows: true);

Test("a_name_that_was_not_utf8_is_refused", () =>
{
    var mounts = new MountTable([], [], []);
    var plan = Removal.PlanAgainst([T("/srv/scan/a\uFFFD", 10), T("/srv/scan/plain", 20)], "/srv/scan", mounts, Removal.HomeDir());
    Equal(1, plan.Blocked.Count);
    Equal("/srv/scan/a\uFFFD", plan.Blocked[0].Path);
    Equal(1, plan.Targets.Count, "the plain name still goes");
});

Test("the_root_and_home_are_refused", () =>
{
    var root = Fixture();
    var home = Removal.HomeDir();
    var targets = new List<Target> { T("/", 0), T(root, 0) };
    if (home is not null)
    {
        targets.Add(T(home, 0));
    }
    var plan = Removal.Plan(targets, root);
    Assert(plan.IsEmpty, "nothing acted on");
    Equal(targets.Count, plan.Blocked.Count);
    Assert(home is null || plan.Blocked.Any(blocked => blocked.Reason.Contains("home directory")), "home refused as home");
});

Test("the_directories_above_home_are_refused", () =>
{
    var home = Removal.Home.Of("/home/disktree-test-user");
    Assert(Removal.Refuse("/home", "/", home, noMounts, noMounts)?.Contains("home directory") == true, "home is inside it");
    Equal(null, Removal.Refuse("/home/other", "/", home, noMounts, noMounts), "a sibling of home is not above it");
});

Test("paths_outside_the_root_are_refused", () =>
{
    var plan = Removal.Plan([T("/etc/passwd", 1)], Fixture());
    Assert(plan.IsEmpty, "empty");
    Equal("outside the scanned root", plan.Blocked[0].Reason);
});

Test("sibling_prefixes_are_not_treated_as_containment", () =>
{
    var root = Fixture();
    Directory.CreateDirectory(Path.Join(root, "a-real"));
    var plan = Removal.Plan([T(Path.Join(root, "a"), 1), T(Path.Join(root, "a-real"), 1)], root);
    Equal(2, plan.Targets.Count, "a-real is not inside a");
    Equal(0, plan.Covered.Count);
});

Test("normalize_resolves_dots_without_the_filesystem", () =>
{
    Equal("/home/user/src", Removal.Normalize("/home/user/./src/../src/"));
    Equal("b", Removal.Normalize("a/../b"));
    Equal("/", Removal.Normalize("/.."));
    Equal("../x", Removal.Normalize("../x"));
});

Test("mount_points_are_recognized", () =>
{
    Assert(!Removal.IsMountPoint(Fixture()), "a temp dir is not one");
    if (Directory.Exists("/proc/self"))
    {
        Assert(Removal.IsMountPoint("/proc"), "/proc is one");
    }
});

Test("a_directory_holding_the_home_directory_is_refused", () =>
{
    var home = Removal.Home.Of("/Users/user");
    var reason = Removal.Refuse("/Users", "/", home, noMounts, noMounts);
    Assert(reason?.Contains("home directory") == true, reason ?? "not refused");
    Equal(null, Removal.Refuse("/Users/user/src", "/", home, noMounts, noMounts), "inside home is the user's");
});

Test("a_directory_holding_a_system_tree_is_refused", () =>
{
    var home = Removal.Home.Of("/Users/user");
    foreach (var (path, inside) in new[] { ("/opt", "/opt/homebrew"), ("/var", "/var/lib") })
    {
        var reason = Removal.Refuse(path, "/", home, noMounts, noMounts);
        Assert(reason?.Contains(inside) == true, $"{path}: {reason}");
    }
    Equal(null, Removal.Refuse("/opt/local", "/", home, noMounts, noMounts), "a sibling of a system tree is not its parent");
});

Test("a_target_with_a_mount_inside_is_refused", () =>
{
    var root = Fixture();
    var mounts = new List<string> { Removal.GuardKey(Path.Join(root, "a/b")), Removal.GuardKey(Path.Join(root, "other")) };
    Equal(Removal.GuardKey(Path.Join(root, "a/b")), Removal.MountBelow(Removal.GuardKey(Path.Join(root, "a")), mounts));
    Equal(null, Removal.MountBelow(Removal.GuardKey(Path.Join(root, "a/b")), mounts), "itself");
    var reason = Removal.Refuse(Path.Join(root, "a"), root, null, mounts, noMounts);
    Assert(reason?.Contains("mounted inside") == true, reason ?? "not refused");
    Equal(null, Removal.Refuse(Path.Join(root, "a/one.bin"), root, null, mounts, noMounts));
});

Test("nothing_is_mounted_below_a_temp_dir", () =>
    Equal(null, Removal.MountBelow(Removal.GuardKey(Fixture()), Removal.ReadMountTable().Points)));

Test("this_macs_mount_table_is_read_without_proc", () =>
{
    var mounts = Removal.ReadMountTable().Points;
    Assert(mounts.Contains(Removal.GuardKey("/")), string.Join(",", mounts));
    // Asked of MountBelow itself: Refuse turns /System/Volumes away as system first.
    Assert(Removal.MountBelow(Removal.GuardKey("/System/Volumes"), mounts) is not null, "the Data volume is mounted there");
}, when: macos);

Test("the_mount_table_is_read_off_the_calling_thread_and_then_kept", () =>
{
    Removal.PrimeMountPoints();
    var mounts = Removal.MountPoints();
    for (var i = 0; i < 500 && mounts.Points.Count == 0; i++)
    {
        Thread.Sleep(10);
        mounts = Removal.MountPoints();
    }
    Assert(mounts.Points.SequenceEqual(Removal.ReadMountTable().Points), "the same table, cached");
});

Test("macos_guard_keys_fold_the_data_volume_and_case", () =>
{
    Equal("/users/user/src", Removal.GuardKeyFor("/System/Volumes/Data/Users/User/src", macos: true));
    Equal("/library/caches", Removal.GuardKeyFor("/Library/./Caches", macos: true));
    Equal("/system/volumes/data", Removal.GuardKeyFor("/System/Volumes/Data", macos: true), "the Data volume itself stays under /System");
    Equal("/Library/X", Removal.GuardKeyFor("/Library/X", macos: false), "Linux paths are compared as spelled");
});

Test("macos_guards_see_through_other_spellings", () =>
{
    var home = Removal.Home.Of("/Users/user");
    var data = Removal.MacosDataVolume;
    Equal(null, Removal.Refuse(data + "/Users/user/src", data, home, noMounts, noMounts), "home under the Data volume's name is still home");
    var reason = Removal.Refuse("/library/caches", "/", home, noMounts, noMounts);
    Assert(reason?.Contains("/Library") == true, reason ?? "not refused");
}, when: macos);

Test("permanent_removal_takes_nested_trees_and_hidden_files", () =>
{
    var root = Fixture();
    var doomed = Path.Join(root, "a");
    Directory.CreateDirectory(Path.Join(doomed, "b/c/d"));
    File.WriteAllText(Path.Join(doomed, "b/c/d/.hidden"), "x");
    File.WriteAllText(Path.Join(doomed, "b/c/f"), "x");
    Removal.RemovePermanently(doomed, root);
    Assert(!Directory.Exists(doomed), "gone");
    Assert(Directory.Exists(Path.Join(root, "other")), "sibling kept");
});

Test("permanent_removal_unlinks_nested_symlinks_without_following_them", () =>
{
    var root = Fixture();
    var outside = Empty();
    File.WriteAllText(Path.Join(outside, "keep"), "x");
    LinkDir(outside, Path.Join(root, "a/link"));
    Removal.RemovePermanently(Path.Join(root, "a"), root);
    Assert(File.Exists(Path.Join(outside, "keep")), "the link's target survives");
});

Test("permanent_removal_takes_directories_and_leaves_siblings", () =>
{
    var root = Fixture();
    Removal.RemovePermanently(Path.Join(root, "a/b"), root);
    Assert(!Directory.Exists(Path.Join(root, "a/b")), "gone");
    Assert(File.Exists(Path.Join(root, "a/c.bin")), "sibling kept");
});

Test("permanent_removal_unlinks_a_symlink_instead_of_following_it", () =>
{
    var root = Fixture();
    var keep = Empty();
    File.WriteAllText(Path.Join(keep, "precious.bin"), "data");
    var link = Path.Join(root, "link");
    LinkDir(keep, link);
    Removal.RemovePermanently(link, root);
    Assert(new FileInfo(link).LinkTarget is null && !Directory.Exists(link), "the link is gone");
    Assert(File.Exists(Path.Join(keep, "precious.bin")), "its target survives");
});

Test("permanent_removal_does_not_follow_a_link_put_on_the_way_down", () =>
{
    var root = Fixture();
    var keep = Empty();
    Directory.CreateDirectory(Path.Join(keep, "x"));
    File.WriteAllText(Path.Join(keep, "x/precious.bin"), "data");
    var marked = Path.Join(root, "a/b/x");
    Directory.Delete(Path.Join(root, "a/b"), recursive: true);
    LinkDir(keep, Path.Join(root, "a/b"));
    var error = Expect<IOException>(() => Removal.RemovePermanently(marked, root));
    Assert(error.Contains("changed since"), error);
    Assert(File.Exists(Path.Join(keep, "x/precious.bin")), "the other x survives");
});

Test("a_target_reached_through_a_link_is_refused", () =>
{
    var root = Fixture();
    var keep = Empty();
    File.WriteAllText(Path.Join(keep, "precious.bin"), "data");
    LinkDir(keep, Path.Join(root, "link"));
    var plan = Removal.Plan([T(Path.Join(root, "link/precious.bin"), 4)], root);
    Assert(plan.IsEmpty, "empty");
    Assert(plan.Blocked[0].Reason.Contains("through a link"), plan.Blocked[0].Reason);
});

Test("a_link_put_on_the_way_down_after_planning_stops_the_worker", () =>
{
    var root = Fixture();
    var keep = Empty();
    Directory.CreateDirectory(Path.Join(keep, "x"));
    File.WriteAllText(Path.Join(keep, "x/precious.bin"), "data");
    Directory.CreateDirectory(Path.Join(root, "a/b/x"));
    var plan = Removal.Plan([T(Path.Join(root, "a/b/x"), 0)], root);
    Equal(1, plan.Targets.Count);
    Directory.Delete(Path.Join(root, "a/b"), recursive: true);
    LinkDir(keep, Path.Join(root, "a/b"));
    var item = Drain(Removal.Spawn(plan, RemovalMode.Permanent)).OfType<RemovalEvent.Item>().Single();
    Assert(item.Error?.Contains("through a link") == true, item.Error ?? "not refused");
    Assert(File.Exists(Path.Join(keep, "x/precious.bin")), "survives");
});

Test("home_is_found_by_its_real_path_too", () =>
{
    var temp = Fixture();
    var real = Path.Join(temp, "data");
    Directory.CreateDirectory(Path.Join(real, "home/user"));
    var spelled = Empty();
    LinkDir(Path.Join(real, "home"), Path.Join(spelled, "home"));
    var home = Removal.Home.Of(Path.Join(spelled, "home/user"));
    var root = Removal.Realpath(temp)!;
    var reason = Removal.Linked(Path.Join(root, "data"), root, root, home);
    Assert(reason?.Contains("home directory") == true, reason ?? "not refused");
    Equal(null, Removal.Linked(Path.Join(root, "other"), root, root, home));
});

Test("names_win32_would_misread_are_recognized", () =>
{
    foreach (var name in new[] { "Users.", "Users ", "a. .", "NUL", "nul.txt", "Com1", "LPT9.log", "con ", "file:stream", "COM¹" })
    {
        Assert(Removal.MisreadByWin32(name), name);
    }
    foreach (var name in new[] { "Users", ".git", "..hidden", "console", "COM0", "LPT10" })
    {
        Assert(!Removal.MisreadByWin32(name), name);
    }
});

Test("permanent_removal_follows_links_above_the_root", () =>
{
    var temp = Fixture();
    var root = Path.Join(Empty(), "root");
    LinkDir(temp, root);
    Removal.RemovePermanently(Path.Join(root, "a/b"), root);
    Assert(!Directory.Exists(Path.Join(temp, "a/b")), "removed through the root's link");
    Assert(File.Exists(Path.Join(temp, "a/one.bin")), "sibling kept");
});

Test("a_mount_below_a_target_is_caught_again_at_removal_time", () =>
{
    var root = Fixture();
    var mounts = new[] { Removal.GuardKey(Path.Join(root, "a/b")) };
    var reason = Removal.Mounted(Path.Join(root, "a"), mounts);
    Assert(reason?.Contains("mounted inside") == true, reason ?? "not caught");
    Equal(null, Removal.Mounted(Path.Join(root, "other"), mounts));
});

// The Volume rule on Linux is the mount table's: a directory the table lists
// stops the walk.
Test("a_mount_point_inside_stops_the_walk_and_nothing_on_it_is_touched", () =>
{
    var root = Fixture();
    File.WriteAllText(Path.Join(root, "a/b/keep"), "x");
    var mounts = new MountTable([Removal.GuardKey(Path.Join(root, "a/b"))], [], null);
    var error = Expect<IOException>(() => Removal.RemovePermanently(Path.Join(root, "a"), root, mounts));
    Assert(error.Contains("another filesystem is mounted there"), error);
    Assert(File.Exists(Path.Join(root, "a/b/keep")), "nothing below the mount point was touched");
}, when: linux);

Test("read_only_files_are_removed_too", () =>
{
    var root = Fixture();
    var objects = Path.Join(root, "a/b/objects");
    Directory.CreateDirectory(objects);
    var packed = Path.Join(objects, "pack.idx");
    File.WriteAllText(packed, "x");
    File.SetAttributes(packed, FileAttributes.ReadOnly);
    var lone = Path.Join(root, "a/lone.idx");
    File.WriteAllText(lone, "x");
    File.SetAttributes(lone, FileAttributes.ReadOnly);
    Removal.RemovePermanently(lone, root);
    Removal.RemovePermanently(Path.Join(root, "a/b"), root);
    Assert(!File.Exists(lone) && !Directory.Exists(Path.Join(root, "a/b")), "both gone");
    Assert(File.Exists(Path.Join(root, "a/c.bin")), "sibling kept");
});

Test("a_missing_target_reports_not_found", () =>
{
    var root = Fixture();
    Expect<FileNotFoundException>(() => Removal.RemovePermanently(Path.Join(root, "absent"), root));
});

Test("percent_encoding_escapes_what_the_spec_requires", () =>
{
    Equal("/home/user/a%20b", Removal.PercentEncode("/home/user/a b"));
    Equal("/a/b-c.d_e~f", Removal.PercentEncode("/a/b-c.d_e~f"));
    Equal("/a/n%C3%A9w", Removal.PercentEncode("/a/néw"));
    Equal("/a%0Ab", Removal.PercentEncode("/a\nb"));
});

Test("trash_names_avoid_collisions", () =>
{
    var dir = Empty();
    var (first, firstName) = Removal.UniqueName(dir, "notes");
    Equal("notes", firstName);
    File.WriteAllText(first, "x");
    var (second, secondName) = Removal.UniqueName(dir, "notes");
    Equal("notes.1", secondName);
    Assert(first != second, "different paths");
});

Test("the_xdg_trash_moves_a_file_and_records_where_it_came_from", () =>
{
    // A private trash directory keeps this out of the real trash.
    var trash = Empty();
    var file = Path.Join(Empty(), "doomed.bin");
    File.WriteAllText(file, "payload");
    Removal.TrashInto(file, trash);
    Assert(!File.Exists(file), "moved away");
    var moved = Path.Join(trash, "files/doomed.bin");
    Equal("payload", File.ReadAllText(moved));
    var info = File.ReadAllText(Path.Join(trash, "info/doomed.bin.trashinfo"));
    Assert(info.StartsWith("[Trash Info]\nPath=") && info.Contains("doomed.bin") && info.Contains("DeletionDate="), info);
}, when: !OperatingSystem.IsWindows());

Test("a_trash_info_path_is_restorable", () =>
{
    var trash = Empty();
    var file = Path.Join(Empty(), "with space.bin");
    File.WriteAllText(file, "x");
    Removal.TrashInto(file, trash);
    var recorded = File.ReadAllLines(Path.Join(trash, "info/with space.bin.trashinfo"))
        .First(line => line.StartsWith("Path=", StringComparison.Ordinal))["Path=".Length..];
    Equal(Removal.PercentEncode(file), recorded);
    Assert(recorded.EndsWith("with%20space.bin"), recorded);
}, when: !OperatingSystem.IsWindows());

// A link goes into the XDG trash as itself, its target stays.
Test("the_xdg_trash_takes_a_link_and_leaves_its_target", () =>
{
    var trash = Empty();
    var keep = Empty();
    File.WriteAllText(Path.Join(keep, "precious.bin"), "data");
    var link = Path.Join(Empty(), "link");
    LinkDir(keep, link);
    Removal.TrashInto(link, trash);
    Assert(new FileInfo(link).LinkTarget is null && !Directory.Exists(link), "the link went");
    Assert(new FileInfo(Path.Join(trash, "files/link")).LinkTarget is not null, "trashed as a link");
    Assert(File.Exists(Path.Join(keep, "precious.bin")), "not its target");
}, when: !OperatingSystem.IsWindows());

Test("a_removal_run_reports_every_item_and_a_total", () =>
{
    var root = Fixture();
    var plan = Removal.Plan([T(Path.Join(root, "a/b"), 0), T(Path.Join(root, "a/c.bin"), 20), T(Path.Join(root, "absent"), 0)], root);
    Equal(3, plan.Targets.Count);
    var events = Drain(Removal.Spawn(plan, RemovalMode.Permanent));
    Assert(events[0] is RemovalEvent.Start { Total: 3 }, "start");
    Equal(3, events.OfType<RemovalEvent.Item>().Count());
    Assert(events[^1] is RemovalEvent.Done { Removed: 2, Failed: 1, Bytes: 20 }, events[^1].ToString());
    Assert(!Directory.Exists(Path.Join(root, "a/b")) && File.Exists(Path.Join(root, "a/one.bin")), "just the targets");
});

string FakeTool(string name, string body)
{
    var script = Path.Join(Empty(), name);
    File.WriteAllText(script, "#!/bin/sh\n" + body);
    File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    return script;
}

Test("a_trash_tool_is_called_with_the_path_after_a_separator", () =>
{
    var dir = Empty();
    var recording = Path.Join(dir, "argv");
    var script = FakeTool("fake-trash-put", $"printf '%s\\n' \"$@\" > '{recording}'\n");
    // A path that would be an option if the separator were missing.
    var doomed = Path.Join(dir, "-rf");
    File.WriteAllText(doomed, "not this time");
    Removal.RunTool(script, [], doomed);
    Assert(File.ReadAllLines(recording).SequenceEqual(["--", doomed]), File.ReadAllText(recording));
    Assert(File.Exists(doomed), "the real tool would have moved it");
}, when: !OperatingSystem.IsWindows());

Test("a_failing_trash_tool_reports_its_stderr", () =>
{
    var script = FakeTool("angry-trash-put", "echo 'no trash here' >&2\nexit 3\n");
    var error = Expect<IOException>(() => Removal.RunTool(script, [], Empty()));
    Assert(error.Contains("no trash here"), error);
}, when: !OperatingSystem.IsWindows());

Test("detection_prefers_a_tool_this_machine_has", () =>
{
    var backend = Removal.DetectTrashBackend();
    Console.WriteLine($"      backend here: {backend} ({backend.Label()})");
    Assert(backend.IsAvailable(), "some trash");
}, when: linux);

Test("macos_always_uses_the_system_trash", () =>
    Equal(TrashBackend.MacOs, Removal.DetectTrashBackend()), when: macos && File.Exists("/usr/bin/trash"));

Test("system_trees_are_refused_in_a_whole_disk_scan", () =>
{
    var home = Removal.Home.Of("/home/user");
    string? TreeOf(string path, Removal.Home at) =>
        Removal.Refuse(path, "/", at, noMounts, noMounts) is { } reason && reason.StartsWith("part of the system under ")
            ? reason["part of the system under ".Length..reason.IndexOf(':')]
            : null;
    Equal("/usr", TreeOf("/usr/lib/libfoo.so", home));
    Equal("/var/lib", TreeOf("/var/lib/pacman", home));
    Equal(null, TreeOf("/var/cache/pacman/pkg", home));
    Equal(null, TreeOf("/opt/thing", home));
    Equal("/home/linuxbrew/.linuxbrew", TreeOf("/home/linuxbrew/.linuxbrew/Cellar", home));
    Equal("/gnu/store", TreeOf("/gnu/store/abc-hello", home));
    Equal(null, TreeOf("/usrlocal", home), "components, not prefixes");
    Equal(null, TreeOf("/usr/home/user/.cache", Removal.Home.Of("/usr/home/user")));
    Assert(Removal.Refuse("/etc/hosts", "/", home, noMounts, noMounts)?.Contains("/etc") == true, "/etc");
}, when: !OperatingSystem.IsWindows());

Test("macos_system_trees_are_refused_but_apps_are_not", () =>
{
    var home = Removal.Home.Of("/Users/user");
    foreach (var (path, tree) in new[]
    {
        ("/System/Library/Fonts", "/System"), ("/Library/Caches/com.apple.x", "/Library"), ("/private/etc/hosts", "/private"),
        ("/private/var/folders/xy", "/private"), ("/opt/homebrew/Cellar/git", "/opt/homebrew"),
    })
    {
        Assert(Removal.Refuse(path, "/", home, noMounts, noMounts)?.StartsWith($"part of the system under {tree}:") == true, path);
    }
    foreach (var path in new[] { "/Applications/Xcode.app", "/Users/user/Library/Caches" })
    {
        Assert(Removal.Refuse(path, "/", home, noMounts, noMounts)?.StartsWith("part of the system") != true, path);
    }
}, when: !OperatingSystem.IsWindows());

// ── Export ──────────────────────────────────────────────────────────────

static Target Named(string path, ulong bytes, bool dir) => new(path, bytes, dir, false);

Test("the_list_is_one_path_per_line", () =>
    Equal("/home/me/src/old/target\n/home/me/Downloads/big.iso\n",
        Export.DeleteList([Named("/home/me/src/old/target", 5UL << 30, true), Named("/home/me/Downloads/big.iso", 4UL << 30, false)])));

Test("a_name_with_a_newline_cannot_smuggle_in_a_path", () =>
{
    Target[] targets = [Named("/tmp/x\n/home/me", 1, true)];
    var list = Export.DeleteList(targets);
    Equal(1, list.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
    Assert(list.StartsWith("# left out"), list);
    Assert(!list.Split('\n').Contains("/home/me"), list);
    var prompt = Export.AgentPrompt(targets, "/tmp", null);
    Assert(!prompt.Split('\n').Any(line => line.StartsWith("/home/me")), prompt);
    Assert(prompt.Contains("escaped, find by hand: /tmp/x\\n/home/me"), prompt);
});

Test("a_name_that_is_not_unicode_is_flagged_not_listed", () =>
{
    // What the lister makes of the bytes caf\xe9: U+FFFD in place of them.
    Target[] targets = [Named("/tmp/caf" + (char)0xFFFD, 1, false)];
    var list = Export.DeleteList(targets);
    Assert(list.StartsWith("# left out"), list);
    Equal(1, list.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
    Assert(Export.AgentPrompt(targets, "/tmp", null).Contains("- escaped, find by hand: "), "flagged");
    Assert(Export.DeleteList([Named("/tmp/lone" + (char)0xD800, 1, false)]).StartsWith("# left out"), "a lone surrogate");
});

Test("the_prompt_lists_every_path_with_its_size_and_the_rules", () =>
{
    var prompt = Export.AgentPrompt([Named("/home/me/src/old/target", 5UL << 30, true), Named("/home/me/Downloads/big.iso", 4UL << 30, false)],
        "/home/me", new SpaceInfo(500UL << 30, 12UL << 30, 10UL << 30));
    foreach (var part in new[]
    {
        "look through /home/me and picked", "9.0 GiB in all", "10 GiB available of 500 GiB",
        "- /home/me/src/old/target  (5.0 GiB, directory)", "- /home/me/Downloads/big.iso  (4.0 GiB, file)",
        "git status", "Do not delete anything else",
    })
    {
        Assert(prompt.Contains(part), part);
    }
});

// ── Marks ───────────────────────────────────────────────────────────────

static NodeRef MarksTree()
{
    var root = Tree.Of(Draft.Dir("home", Draft.Dir(".cache", Draft.File("blob.bin", 900)), Draft.File("notes.bin", 100)));
    Tree.Aggregate(root, Metric.Bytes);
    return root;
}

Test("toggling_marks_and_unmarks", () =>
{
    var marks = new Marks();
    Assert(marks.Toggle(Named("/home/user/a", 1, false)), "marked");
    Assert(marks.Contains("/home/user/a") && marks.Count == 1, "contains");
    Assert(!marks.Toggle(Named("/home/user/a", 1, false)) && marks.Count == 0, "unmarked");
});

Test("marking_the_same_path_twice_keeps_one_entry", () =>
{
    var marks = new Marks();
    for (var i = 0; i < 3; i++)
    {
        marks.Toggle(Named("/home/user/a", 1, false));
    }
    Equal(1, marks.Count);
});

Test("refresh_re_reads_sizes_from_a_new_tree", () =>
{
    var marks = new Marks();
    marks.Toggle(Named("/home/user/.cache", 0, false));
    marks.Toggle(Named("/home/user/gone", 500, false));
    marks.Refresh("/home/user", MarksTree());
    Equal(900UL, marks.Items[0].Bytes);
    Assert(marks.Items[0].IsDir && marks.Items[0].Hidden, "a hidden directory");
    Equal(0UL, marks.Items[1].Bytes, "a path that no longer exists");
});

Test("find_matches_whole_components_only", () =>
{
    var root = MarksTree();
    Assert(Marks.Find("/home/user", root, "/home/user/.cache") is not null, ".cache");
    Assert(Marks.Find("/home/user", root, "/home/user/.cache/blob.bin") is not null, "blob");
    Assert(Marks.Find("/home/user", root, "/home/user/cache") is null, "cache");
    Assert(Marks.Find("/elsewhere", root, "/home/user/notes.bin") is null, "elsewhere");
    Assert(Marks.CrumbsFor("/home/user", root, "/home/user/.cache/blob.bin") is [0, 0], "crumbs");
});

// The fixture, and only it, goes. Links inside it point inside it, and are
// unlinked, not followed.
Directory.Delete(fixtures, recursive: true);
Console.WriteLine($"{passed} passed, {failed} failed");
return failed == 0 ? 0 : 1;
