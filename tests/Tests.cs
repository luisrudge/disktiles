// Tests for DiskTiles: plain C#, no Qt, no window. Run: dotnet run --project tests
//
// The core's own tests come first (FilterTests.cs and RemovalTests.cs, ported
// from the GTK app), then the app: the whole controller driven the way QML
// drives it (keys, the pointer, buttons), on a scratch tree under
// /tmp/claude-disktiles/tests-<pid> that it deletes when done. Removal runs
// only on that tree, and its trash is a private one in the same folder.
global using AppBackend = Backend;
using System.Collections.Concurrent;
using System.Diagnostics;
using Disktiles;
using Disktiles.Core;

var failures = 0;
void Check(string name, bool ok, string detail = "")
{
    Console.WriteLine($"{(ok ? "ok  " : "FAIL")}  {name}{(ok || detail.Length == 0 ? "" : $": {detail}")}");
    if (!ok)
    {
        failures++;
    }
}

FilterTests.Run();
failures += RemovalTests.Run();

var scratch = $"/tmp/claude-disktiles/tests-{Environment.ProcessId}-app";
Directory.CreateDirectory(scratch);
try
{
    AppTests();
}
finally
{
    Directory.Delete(scratch, recursive: true);
}

Console.WriteLine(failures == 0 ? "all passed" : $"{failures} failed");
return failures == 0 ? 0 : 1;

// Sparse files with --apparent-size: sizes as large as the rules need
// (Worth a look starts at 64 MiB) without taking any memory.
string MakeTree()
{
    var root = Path.Join(scratch, "home");
    void F(string path, long bytes)
    {
        var full = Path.Join(root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        using var file = new FileStream(full, FileMode.Create);
        file.SetLength(bytes);
    }
    F("projects/web/package.json", 1_000);
    F("projects/web/node_modules/big/lib.so", 90L << 20);
    F("projects/web/node_modules/small/index.js", 4_000);
    F("projects/web/src/app.ts", 60_000);
    F("projects/rusty/Cargo.toml", 1_000);
    F("projects/rusty/target/debug/rusty", 70L << 20);
    F("projects/rusty/src/main.rs", 20_000);
    F(".cache/pip/wheels.bin", 66L << 20);
    F("Pictures/trip/img1.jpg", 30L << 20);
    F("Pictures/trip/img2.jpg", 20L << 20);
    F("Documents/report.pdf", 5L << 20);
    F("Downloads/installer.iso", 12L << 20);
    return root;
}

void AppTests()
{
    var root = MakeTree();
    var trash = Path.Join(scratch, "trash");
    var saved = Path.Join(scratch, "list.txt");
    var prompt = Path.Join(scratch, "prompt.txt");
    var cli = Cli.Parse([root, "--apparent-size", "--test-trash-dir", trash, "--test-save-to", saved,
        "--test-clipboard-to", prompt, "--test-reveal", "dry", "--test-pick", Path.Join(root, "Pictures")], Power.Balanced);
    var ui = new Hand();
    var app = new App(cli, ui);
    var effects = new List<Effect>();
    app.Effected += effects.Add;
    bool Key(string chord)
    {
        var handled = app.Press(QtKeys.Parse(chord));
        ui.Drain();
        return handled;
    }
    string? Name(int[]? crumbs) => crumbs is null ? null : app.State.Resolve(crumbs)?.Name;
    void Land() => ui.Until(() => app.Running is null && app.State.Tree is not null);

    // ── the scan lands ──
    Land();
    app.Resize(800, 600);
    var tree = app.State.Tree!.Value;
    Check("scan: sizes are the sum of the files", tree.Bytes == (ulong)(1_000 + (90L << 20) + 4_000 + 60_000 + 1_000 + (70L << 20) + 20_000 + (66L << 20) + (30L << 20) + (20L << 20) + (5L << 20) + (12L << 20)), $"{tree.Bytes}");
    Check("scan: largest first, and selected", tree.Children[0].Name == "projects" && Name(app.State.Selected) == "projects");
    Check("scan: classified", tree.Children[tree.Children.IndexOf(".cache")].Category == Category.Cache
        && Marks.Find(root, tree, Path.Join(root, "projects/web/node_modules"))?.Reclaim == Reclaim.Reinstallable
        && Marks.Find(root, tree, Path.Join(root, "projects/rusty/target"))?.Reclaim == Reclaim.BuildOutput);
    Check("scan: the panel's model is ready", !app.Scan.Visible && app.Status.StartsWith("scan ") && app.Totals.Bytes.Length > 0);
    Check("tiles: laid out at the treemap's size", app.Tiles.Count > 5 && app.Tiles.All(t => t.Rect.Right <= 800.5f && t.Rect.Bottom <= 600.5f));
    Check("tiles: models carry colours and labels", app.TileModels.Count == app.Tiles.Count
        && app.TileModels.All(t => t.Fill.A > 0 && t.Ink.A > 0) && app.TileModels.Any(t => t.Hatch) && app.TileModels.Any(t => t.Accent is not null));
    var drawing = app.BuildDrawing();
    Check("paint: one drawing with every tile, labels and the ground", drawing.Count > app.Tiles.Count, $"{drawing.Count} ops for {app.Tiles.Count} tiles");
    var worth = app.Worth;
    Check("worth a look: node_modules, target and the cache", worth.Rows.Count == 3 && worth.Rows[0].Title == "web/node_modules"
        && worth.Rows.Any(r => r.Reason == "build output") && worth.Rows.Any(r => r.Reason == "regenerable"), string.Join(", ", worth.Rows.Select(r => r.Title)));
    Check("trail: / tmp … then the root", app.Trail.Last().Label == "home" && app.Trail.Last().Current && app.Trail.Any(c => c.Kind == "above"));

    // ── navigation ──
    Check("Return opens the selection", Key("Return") && Name(app.State.Current is null ? null : app.State.Crumbs) == "projects");
    Key("Tab");
    var first = Name(app.State.Selected);
    Key("Tab");
    Check("Tab walks the siblings largest first", first == "web" && Name(app.State.Selected) == "rusty", $"{first}, {Name(app.State.Selected)}");
    Key("shift+Tab");
    Check("shift+Tab walks back", Name(app.State.Selected) == "web");
    Key("Escape");
    Check("Escape drops the selection first", app.State.Selected is null && app.State.Crumbs.Length == 1);
    Key("Escape");
    Check("then goes up", app.State.Crumbs.Length == 0);
    Key("alt+Left");
    Check("alt+Left goes back", Name(app.State.Crumbs) == "projects");
    Key("alt+Right");
    Check("alt+Right goes forward", app.State.Crumbs.Length == 0 && app.BackButton.Enabled && !app.ForwardButton.Enabled);
    app.State.Select([0]);
    Key("l");
    Check("l moves to the neighbour on the right", app.State.Selected is { Length: 1 } s && s[0] != 0, Name(app.State.Selected) ?? "none");
    Key("h");
    Check("h comes back", Name(app.State.Selected) == "projects");
    Key("BackSpace");
    Check("BackSpace at the root does nothing", app.State.Crumbs.Length == 0);

    // ── the pointer ──
    var webTile = app.Tiles.First(t => Name(t.Crumbs) == "web");
    var (cx, cy) = (webTile.Rect.X + webTile.Rect.W / 2, webTile.Rect.Y + webTile.Rect.H / 2);
    app.PointerMoved(cx, cy);
    ui.Drain();
    Check("hover: the deepest tile under the pointer", app.Hovered is { } hovered && hovered.Crumbs.Length >= 2 && app.HoverRect.Length == 4);
    Check("hover: the card names it", app.Card is { } card && card.Path.Contains("projects/web") && card.Hint.Contains("space mark"), app.Card?.Path ?? "no card");
    ui.Until(() => app.Selection.Path.Contains("projects/web"), 2000);
    Check("hover: the panel follows, throttled", app.Selection.Path.Contains("projects/web"), app.Selection.Path);
    Check("hover: keys act on the tile under the pointer", app.State.ActionTarget is { Length: >= 2 } && app.State.PointerActive);
    app.Click(cx, cy, 1, 2, control: false);
    Check("a double click opens what it hits", Name(app.State.Crumbs) is "web" or "node_modules", Name(app.State.Crumbs) ?? "");
    app.Unhover();
    Key("BackSpace");
    Key("BackSpace");
    Key("BackSpace");
    app.Click(1, 1, 3, 1, control: false);
    Check("right-click reveals (dry)", app.State.Selected is not null);

    // ── zoom and depth ──
    Key("plus");
    Check("+ magnifies", Math.Abs(app.View.Scale - 1.25) < 1e-6 && app.ScaleText == "1.3×", app.ScaleText);
    Key("0");
    Check("0 resets", app.View.IsIdentity);
    var depth = app.State.Layout.MaxDepth;
    Key("bracketright");
    Key("bracketleft");
    Key("bracketleft");
    Check("[ and ] draw fewer or more levels", app.State.Layout.MaxDepth == depth - 1);
    Key("bracketright");
    var projects = app.Tiles.First(t => Name(t.Crumbs) == "projects");
    for (var i = 0; i < 30 && app.State.Crumbs.Length == 0; i++)
    {
        app.Scroll(projects.Rect.X + 30, projects.Rect.Y + 40, 0, -1, shift: false);
    }
    Check("the wheel zooms toward a directory, then goes in", app.State.Crumbs.Length >= 1, string.Join(",", app.State.Crumbs));
    Check("going in eases from where it was on screen (already magnified to fit)", app.Transition is { K: > 0.8 and < 1.25 }, $"{app.Transition}");
    for (var i = 0; i < 30 && app.State.Crumbs.Length > 0; i++)
    {
        app.Scroll(10, 10, 0, 1, shift: false);
    }
    Check("and out goes up", app.State.Crumbs.Length == 0);

    // ── modes ──
    app.State.Select([tree.Children.IndexOf("Pictures")]);
    Key("t");
    Check("t: Files ranks by file count, the selection kept", app.State.Metric == Metric.Files && Name(app.State.Selected) == "Pictures"
        && app.State.Tree!.Value.Children[0].Name == "projects");
    Key("t");
    Check("t: Age colours by last write, with its ramp", app.State.ColorMode == ColorMode.Age && app.State.Metric == Metric.Bytes
        && app.Legend.Any(l => l.Label == "This week"));
    Key("t");
    Check("t: back to Size", app.ModeIndex == 0 && app.Legend.Any(l => l.Label == "Cache"));
    tree = app.State.Tree!.Value;

    // ── the filter ──
    Key("slash");
    foreach (var c in "IMG")
    {
        app.Press(new Key(c.ToString(), c.ToString()));
    }
    ui.Until(() => app.State.Matches is not null);
    Check("filter: typing dims what does not match", app.State.Matches is { Count: 2 } && app.FilterBar.Summary.StartsWith("2 matches") && !app.State.FilterApplied,
        app.FilterBar.Summary);
    Key("Return");
    Check("filter: Enter shows only the matches, the largest selected", app.State.FilterApplied && Name(app.State.Selected) == "img1.jpg"
        && app.Tiles.Where(t => t.Kind == TileKind.Node).All(t => app.State.Matches!.KeepOf(t.Crumbs) is not null), Name(app.State.Selected) ?? "");
    Key("Escape");
    Check("filter: Escape clears it", app.State.Matches is null && !app.FilterBar.Visible);

    // ── the trail and its sibling menu ──
    app.State.GoTo([tree.Children.IndexOf("projects")]);
    var crumb = app.Trail.Last();
    app.CrumbMenu(crumb.Index);
    Check("siblings: listed largest first", app.Siblings.Visible && app.Siblings.Rows[0].Name == "projects" && app.Siblings.Rows[0].Current);
    Key("Down");
    Key("Return");
    Check("siblings: Enter goes there", !app.Siblings.Visible && Name(app.State.Crumbs) == ".cache", Name(app.State.Crumbs) ?? "");
    Check("crumbs: the root goes home", app.Crumb(app.Trail.First(c => c.Label == "home").Index) == "pressed" && app.State.Crumbs.Length == 0);

    // ── overlays ──
    Key("question");
    Check("?: the overlay takes the keys", app.HelpShown && Key("j") && app.State.Crumbs.Length == 0);
    Key("Escape");
    Check("?: Escape closes it", !app.HelpShown);
    Key("v");
    ui.Until(() => app.Volumes.Status != "Looking for volumes…");
    Check("v: the volume picker lists volumes", app.Volumes.Visible && app.Volumes.Rows.Count > 0, app.Volumes.Status);
    Key("Escape");
    Check("v: Escape closes it", !app.Volumes.Visible);
    Key("p");
    Check("p hides the panel", !app.PanelShown);
    Key("p");
    Key("ctrl+equal");
    Check("ctrl+= zooms the interface", app.UiZoom > 1 && app.State.Layout.Header > App.DefaultLayout.Header);
    Key("ctrl+0");
    Check("ctrl+0 resets it", app.UiZoom == 1);
    Check("q asks the window to quit", Key("q") && effects.Any(e => e.Kind == "quit"));

    // ── marking ──
    var cache = new[] { tree.Children.IndexOf(".cache") };
    app.State.Select(cache);
    Key("space");
    Check("space marks the selection", app.Marks.Count == 1 && app.Marked.Total == "66 MiB" && app.TileModels.Any(t => t.Mark == 1));
    app.ToggleMark([cache[0], 0]);
    Check("inside a marked directory nothing is marked on its own", app.Marks.Count == 1 && app.Notice?.Tone == Tone.Warning);
    app.State.Select([tree.Children.IndexOf("projects")]);
    var web = Marks.CrumbsFor(root, tree, Path.Join(root, "projects/web/node_modules"))!;
    app.ToggleMark(web);
    app.ToggleMark([web[0]]);
    Check("marking a directory absorbs the marks inside it", app.Marks.Count == 2 && app.Notice?.Text.Contains("covers 1 mark") == true, app.Notice?.Text ?? "");
    app.ToggleMark([web[0]]);
    Check("unmarking", app.Marks.Count == 1);
    app.ToggleMark(web);
    app.ToggleMark([]);
    Check("the scanned root cannot be marked", app.Marks.Count == 2 && app.Notice?.Text.Contains("scanned root") == true);
    Check("the projection counts what comes back", app.Projection.Visible && app.Projection.Review.Contains("Review 2 marked"));

    // ── review, export, removal ──
    Key("c");
    Check("c opens the review", app.State.Screen == Screen.Review && app.Review.Rows.Count == 2 && app.Review.CanCommit
        && app.Review.Mode == "Trash", app.Review.Mode);
    Key("s");
    Check("s saves the list", File.ReadAllLines(saved).Order().SequenceEqual(new[] { Path.Join(root, ".cache"), Path.Join(root, "projects/web/node_modules") }.Order()));
    Key("a");
    Check("a copies a prompt for an agent", File.ReadAllText(prompt).Contains(Path.Join(root, ".cache")) && app.Notice?.Tone == Tone.Success);
    Key("p");
    Key("Return");
    Check("permanent: Enter asks first", app.Dialog.Visible && app.Dialog.Title == "Delete 2 items permanently?", app.Dialog.Title);
    Key("Escape");
    Check("permanent: Escape cancels", !app.Dialog.Visible && app.State.Screen == Screen.Review && Directory.Exists(Path.Join(root, ".cache")));
    Key("m");
    Key("Return");
    Check("trash: Enter runs at once", app.State.Screen is Screen.Running or Screen.Done);
    ui.Until(() => app.State.Screen == Screen.Done);
    Check("trash: both moved into the (private) trash", !Directory.Exists(Path.Join(root, ".cache")) && !Directory.Exists(Path.Join(root, "projects/web/node_modules"))
        && Directory.Exists(Path.Join(trash, "files/.cache")) && File.Exists(Path.Join(trash, "info/node_modules.trashinfo")));
    Check("done: what went, and the marks are gone", app.DoneScreen.Stats[0].Value == "2 items" && app.Marks.Count == 0);
    Land();
    Check("done: the tree is scanned again", app.State.Tree!.Value.Children.IndexOf(".cache") < 0 && !app.DoneScreen.Rescanning);
    Key("Return");
    Check("done: Enter goes back", app.State.Screen == Screen.Explore);

    tree = app.State.Tree!.Value;
    app.ToggleMark([tree.Children.IndexOf("Downloads")]);
    Key("c");
    Key("p");
    Key("Return");
    Key("Return");
    ui.Until(() => app.State.Screen == Screen.Done);
    Check("permanent: Enter in the dialog deletes", !Directory.Exists(Path.Join(root, "Downloads")) && app.DoneScreen.Stats[0].Value == "1 items");
    Land();
    Key("Return");

    // ── scan targets ──
    Key("ctrl+o");
    Land();
    Check("ctrl+o scans the folder picked", app.State.Root == Path.Join(root, "Pictures"), app.State.Root);
    Check("the trail's crumb above widens the scan", app.Crumb(app.Trail.Last(c => c.Kind == "above").Index) == "pressed");
    Land();
    Check("and the folder it came from is selected", app.State.Root == root && Name(app.State.Selected) == "Pictures", app.State.Root);
    Key("i");
    Land();
    Check("i rescans without hidden entries", !app.State.Options.IncludeHidden && app.State.Tree!.Value.Children.IndexOf(".cache") < 0);
    Key("r");
    Check("Escape stops a scan", app.Running is not null && Key("Escape") && app.Running is null && app.Scan.Title.StartsWith("Stopped"));

    // ── what QML reads: every view model encodes ──
    var json = Omasharp.Json.Encode(new object?[] { app.Selection, app.Review, app.Trail, app.Scan, app.Card, app.Palette });
    Check("view models encode as JSON objects", json.Contains("\"name\":") && json.Contains("\"commitLabel\":"), json[..Math.Min(200, json.Length)]);

    // ── keys as Qt names them ──
    Check("keys: Return, shift+Tab, ctrl+o, ?",
        QtKeys.Describe(0x01000004, 0, "\r") == new Key("Return", null)
        && QtKeys.Describe(0x01000002, QtKeys.Shift, "") == new Key("shift+Tab", null)
        && QtKeys.Describe('O', QtKeys.Control, "\u000f") == new Key("ctrl+o", null)
        && QtKeys.Describe('?', QtKeys.Shift, "?") == new Key("question", "?")
        && QtKeys.Describe('A', QtKeys.Shift, "A") == new Key("A", "A")
        && QtKeys.Describe('[', 0, "[") == new Key("bracketleft", "[")
        && QtKeys.Describe(0x01000012, QtKeys.Alt, "") == new Key("alt+Left", null)
        && QtKeys.Describe('=', QtKeys.Control, "") == new Key("ctrl+equal", null)
        && QtKeys.Describe('+', QtKeys.Keypad, "+") == new Key("KP_Add", "+")
        && QtKeys.Describe(0x01000020, QtKeys.Shift, "") is null);

    // ── the pointer's other buttons ──
    Land();
    tree = app.State.Tree!.Value;
    app.State.GoTo([tree.Children.IndexOf("projects")]);
    app.State.GoTo([]);
    app.Click(5, 5, 8, 1, control: false);
    Check("the mouse's back button steps back", Name(app.State.Crumbs) == "projects");
    app.Click(5, 5, 9, 1, control: false);
    Check("and forward", app.State.Crumbs.Length == 0);
    var middle = app.Tiles.First(t => t.Kind == TileKind.Node && t.Crumbs.Length == 1);
    app.Click(middle.Rect.X + 4, middle.Rect.Y + 4, 2, 1, control: false);
    Check("the middle button marks without moving the selection", app.Marks.Count == 1);
    app.UnmarkAll();
    Key("d");
    Land();
    Check("d switches to disk usage (allocated blocks: sparse files weigh nothing)", !app.State.Options.ApparentSize && app.State.Tree!.Value.Bytes < (1UL << 20),
        $"{app.State.Tree!.Value.Bytes}");
    Key("d");
    Land();

    // ── the command line ──
    var parsed = Cli.Parse(["--power-efficiency", "miser", "--metric", "files", "-d", "5", "-H", "-X", "-l", root], Power.Balanced);
    Check("cli: presets, metric, depth, hidden, cross-filesystem, links", parsed.Options is { Threads: <= 2, Metric: Metric.Files, IncludeHidden: false, OneFilesystem: false, FollowLinks: true }
        && parsed.Depth == 5 && parsed.Power == Power.Miser);
    Check("cli: --disk is the disk home is on", Cli.Parse(["--disk"], Power.Balanced).Root == (Space.VolumeRootFor(Removal.HomeDir()!) ?? "/"));
    Check("cli: errors say what is wrong", Throws(() => Cli.Parse(["--depth", "9"], Power.Balanced)) == "--depth must be 1 to 6"
        && Throws(() => Cli.Parse(["--disk", root], Power.Balanced)) == "--disk and a PATH cannot be combined");
    Check("g: the whole disk under a scratch folder is its mount", Space.VolumeRootFor(root) == "/tmp", Space.VolumeRootFor(root) ?? "null");
    var linked = Path.Join(scratch, "links");
    Directory.CreateDirectory(linked);
    Directory.CreateSymbolicLink(Path.Join(linked, "pics"), Path.Join(root, "Pictures"));
    Check("follow links: a link to a directory is walked", Scanner.Scan(linked, new ScanOptions { ApparentSize = true, FollowLinks = true }).Bytes == 50UL << 20
        && Scanner.Scan(linked, new ScanOptions { ApparentSize = true }).Bytes < 1024);

    // ── the treemap layout ──
    var flat = Tree.Of(Draft.Dir("r", Draft.File("a", 600), Draft.File("b", 300), Draft.File("c", 100)));
    Tree.Aggregate(flat, Metric.Bytes);
    var tiles = Treemap.Layout(flat, [], new Rect(0, 0, 100, 100), Metric.Bytes, new LayoutOptions { Padding = 0, PaddingOuter = 0, MinTile = 0 });
    var areas = tiles.Select(t => t.Rect.W * t.Rect.H).ToArray();
    Check("layout: areas proportional to size", tiles.Count == 3 && Math.Abs(areas[0] - 6000) < 1 && Math.Abs(areas[1] - 3000) < 1 && Math.Abs(areas[2] - 1000) < 1,
        string.Join(",", areas));
    Check("layout: squarified (no sliver)", tiles.All(t => Math.Max(t.Rect.W / t.Rect.H, t.Rect.H / t.Rect.W) < 3));
    var nested = Tree.Of(Draft.Dir("r", Draft.Dir("d", Draft.File("x", 50), Draft.File("y", 50)), Draft.File("z", 100)));
    Tree.Aggregate(nested, Metric.Bytes);
    var deep = Treemap.Layout(nested, [], new Rect(0, 0, 400, 400), Metric.Bytes, new LayoutOptions());
    var dir = deep.First(t => t.Crumbs is [0]);
    Check("layout: a directory gets a name band, its children below it", dir.Header is { } band
        && deep.Where(t => t.Crumbs.Length == 2).All(t => t.Rect.Y >= band.Bottom && t.Rect.X >= dir.Rect.X && t.Rect.Right <= dir.Rect.Right));
    Check("layout: padding keeps tiles apart", deep.Where(t => t.Crumbs.Length == 1).All(t => t.Rect.X >= 3 && t.Rect.Y >= 3));

    // ── the theme ──
    var themed = OmarchyTheme.FromColorsToml("Test", "background = \"#000000\"\nforeground = \"#ffffff\"\naccent = \"#ff0000\"\nred = \"#ff0000\"\nyellow = \"#ffff00\"\ngreen = \"#00ff00\"\n");
    Check("theme: colors.toml, roles derived", themed is { Dark: true } && themed.Surface.Qml == "#0d0d0d" && themed.Danger.Qml == "#ff0000", themed?.Surface.Qml ?? "null");
    Check("theme: a partial theme is refused", OmarchyTheme.FromColorsToml("Bad", "background = \"#000000\"\n") is null);
    Check("colour: QML's #aarrggbb", new Rgba(1, 0, 0, 0.5).Qml == "#80ff0000");
}

static string Throws(Action body)
{
    try
    {
        body();
        return "nothing thrown";
    }
    catch (ArgumentException error)
    {
        return error.Message;
    }
}

/// The UI thread, by hand: posts queue until drained, timers tick when asked.
sealed class Hand : IDispatcher
{
    readonly ConcurrentQueue<Action> _posts = new();
    readonly List<Func<bool>> _timers = [];

    public void Post(Action action) => _posts.Enqueue(action);

    public void Every(int ms, Func<bool> tick)
    {
        lock (_timers)
        {
            _timers.Add(tick);
        }
    }

    public void Drain()
    {
        while (_posts.TryDequeue(out var action))
        {
            action();
        }
    }

    void Tick()
    {
        List<Func<bool>> due;
        lock (_timers)
        {
            due = [.. _timers];
            _timers.Clear();
        }
        foreach (var tick in due)
        {
            if (tick())
            {
                lock (_timers)
                {
                    _timers.Add(tick);
                }
            }
        }
    }

    /// Drain and tick until `done`, or `ms` pass.
    public void Until(Func<bool> done, int ms = 20000)
    {
        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < ms)
        {
            Drain();
            if (done())
            {
                return;
            }
            Tick();
            Thread.Sleep(5);
        }
    }
}
