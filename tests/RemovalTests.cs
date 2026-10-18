// The tests of Removal, Export and Marks, ported from the GTK app's
// tests/RemovalTests (its Windows and macOS cases dropped), against fixture
// trees this program makes under /tmp/claude-disktiles/tests-<pid> and
// removes when done. Nothing outside that fixture is ever removed, trashed or
// moved: plans against real paths (/, home, /etc) only plan, and the trash
// tools are fake scripts or a private XDG trash.
using System.Diagnostics;
using Disktiles.Core;

static class RemovalTests
{
    /// The failures.
    public static int Run()
    {
        // The tests of Removal, Export and Marks, against fixture
        // trees this program makes in the temp folder and removes when done. Nothing outside
        // that fixture is ever removed, trashed or moved: plans against real paths
        // (/, home, /etc) only plan, and the trash tools are fake scripts.
        var fixtures = $"/tmp/claude-disktiles/tests-{Environment.ProcessId}";
        Directory.CreateDirectory(fixtures);
        var (passed, failed) = (0, 0);

        // there, only the tests marked `windows` run.
        void Test(string name, Action body, bool when = true)
        {
            if (!when)
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

        // already holds (Application Data, My Documents).
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

        // The scan bounds its own workers; the thread pool belongs to the process.
        // CI runs this with DOTNET_PROCESSOR_COUNT=1 too, where a pool capped at one
        // thread hung a scan started on it.
        Test("scan_from_a_pool_thread_leaves_the_pool_alone", () =>
        {
            var root = Empty();
            Directory.CreateDirectory(Path.Join(root, "d", "e"));
            File.WriteAllBytes(Path.Join(root, "d", "e", "f.bin"), new byte[100]);
            ThreadPool.GetMaxThreads(out var workers, out var io);
            var scan = Task.Run(() => Scanner.Scan(root, new ScanOptions { ApparentSize = true }));
            Assert(scan.Wait(TimeSpan.FromSeconds(30)), "the scan finished");
            Equal(100UL, scan.Result.Bytes);
            ThreadPool.GetMaxThreads(out var workersAfter, out var ioAfter);
            Equal((workers, io), (workersAfter, ioAfter), "the pool's limits");
        });

        // The real OneDrive of this machine, if it has one: only planned, never removed.
        var oneDrive = Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "OneDrive");
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
            foreach (var (path, inside) in new[] { ("/home/linuxbrew", "/home/linuxbrew/.linuxbrew"), ("/var", "/var/lib") })
            {
                var reason = Removal.Refuse(path, "/", home, noMounts, noMounts);
                Assert(reason?.Contains(inside) == true, $"{path}: {reason}");
            }
            Equal(null, Removal.Refuse("/home/linuxbrew-not", "/", home, noMounts, noMounts), "a sibling of a system tree is not its parent");
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
        });

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
        });

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
        });

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
        });

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
        });

        Test("a_failing_trash_tool_reports_its_stderr", () =>
        {
            var script = FakeTool("angry-trash-put", "echo 'no trash here' >&2\nexit 3\n");
            var error = Expect<IOException>(() => Removal.RunTool(script, [], Empty()));
            Assert(error.Contains("no trash here"), error);
        });

        Test("detection_prefers_a_tool_this_machine_has", () =>
        {
            var backend = Removal.DetectTrashBackend();
            Console.WriteLine($"      backend here: {backend} ({backend.Label()})");
            Assert(backend.IsAvailable(), "some trash");
        });

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
        });

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
        return failed;

    }
}
