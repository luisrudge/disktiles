// Scanning, modes and scan targets (feature 3): one scan at a time, its live
// counters over the treemap while it runs, the status line, `r`/F5 to scan
// again and Esc to stop. Modes.cs has the Size/Files/Age, Hidden and Apparent
// controls; Targets.cs the volume picker, ctrl+o and `g`.
using System.Diagnostics;
using Disktiles.Core;
using static Disktiles.Look;

namespace Disktiles;

static class Scanning
{
    /// Where the scan in flight, or the last one, is rooted: what `r` scans
    /// again. Ahead of State.Root while a new root is being read.
    public static string Root { get; private set; } = "";
    /// The scan in flight, if any: its counters, and the way to stop it.
    public static ScanProgress? Running { get; private set; }
    /// Paths the last scan that landed could not read.
    public static long Unreadable { get; private set; }

    static App s_app = null!;
    static ScanPanel s_panel = null!;
    // Bumped per scan and per cancel: only the newest scan's result lands.
    static int s_epoch;

    public static void Install(App app)
    {
        s_app = app;
        var state = app.State;
        var cli = Cli.Current;
        state.Options = cli.Options;
        state.SetMetric(cli.Options.Metric);
        state.SetLayout(state.Layout with { MaxDepth = cli.Depth });
        s_panel = new ScanPanel(app);
        app.Treemap.AddOverlay(s_panel.Widget);
        app.AddCss(ScanPanel.Css);

        app.Keys.Bind("r F5 ctrl+r", "Scan", "scan again", () => Start(Root));
        // Below a filter's Escape, above going up.
        app.Keys.Bind("Escape", "Scan", "stop the scan", Cancel, priority: 5);

        Modes.Install(app);
        Targets.Install(app);
        Start(cli.Root);
    }

    /// Scan `root` from scratch with State.Options, abandoning any walk in
    /// progress. The treemap is covered until the tree lands.
    public static void Start(string root, Action<NodeRef?>? landed = null)
    {
        Running?.Cancel();
        var progress = Running = new ScanProgress();
        var epoch = ++s_epoch;
        Root = root;
        var state = s_app.State;
        // Ranked by the metric on screen, so Files mode stays Files.
        var options = state.Options with { Metric = state.Metric };
        var watch = Stopwatch.StartNew();
        s_app.Treemap.Unhover();
        s_panel.Reading(state.Tilde(root));
        TestHooks.Log($"scan {root} ({options})");
        GLib.Functions.TimeoutAdd(GLib.Constants.PRIORITY_DEFAULT, 110, () =>
        {
            // Stops once this scan lands, fails or is replaced.
            if (epoch != s_epoch || Running != progress)
            {
                return false;
            }
            s_panel.Count(progress);
            s_app.Footer.SetStatus($"scanning · {HumanCount(progress.Files)} entries · {HumanBytes((ulong)progress.Bytes)}");
            TestHooks.Log($"progress {progress.Files} files, {progress.Dirs} dirs, {progress.Bytes} bytes, {progress.Errors} unreadable");
            return true;
        });
        _ = Land(root, options, progress, epoch, watch, landed);
    }

    static async Task Land(string root, ScanOptions options, ScanProgress progress, int epoch, Stopwatch watch, Action<NodeRef?>? landed)
    {
        NodeRef tree;
        Removal.RootId? volume = null;
        try
        {
            // A thread of its own: Scan blocks until the walk is done, and on a
            // pool thread (the pool is capped at one worker per core) it took
            // a lister's place, or all of them on one core.
            // The disk as the scan starts, so one swapped in meanwhile is
            // not taken for the one the tree shows.
            tree = await Task.Factory.StartNew(() =>
            {
                volume = Removal.VolumeOf(root);
                return Scanner.Scan(root, options, progress);
            },
                CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception error)
        {
            if (epoch == s_epoch)
            {
                Running = null;
                s_panel.Failed(progress, error.Message);
                s_app.Footer.SetStatus($"scan failed · {error.Message}");
                TestHooks.Log($"scan failed: {error.Message}");
                landed?.Invoke(null);
            }
            return;
        }
        if (epoch != s_epoch)
        {
            return;
        }
        Running = null;
        s_panel.Widget.Visible = false;
        // The mode switched while it was read: rank it the way it is shown.
        if (s_app.State.Metric != options.Metric)
        {
            Tree.Aggregate(tree, s_app.State.Metric);
        }
        Unreadable = progress.Errors;
        s_app.State.SetTree(root, tree, volume);
        landed?.Invoke(tree);
        s_app.Footer.SetStatus($"scan {HumanCount(progress.Files)} entries · {watch.Elapsed.TotalSeconds:0.0} s");
        TestHooks.Log($"landed {root}: {tree.Files} files, {tree.Dirs} dirs, {tree.Bytes} bytes, first [{string.Join(", ", tree.Children.Take(3).Select(c => $"{c.Name} {c.Bytes}B {c.Files}f"))}] in {watch.ElapsedMilliseconds} ms, rss {Environment.WorkingSet >> 20} MB, heap {GC.GetGCMemoryInfo().HeapSizeBytes >> 20} MB");
    }

    /// Esc while scanning: stop. A partial tree is never shown as if it were
    /// the whole one; the panel says it stopped. False when nothing runs.
    public static bool Cancel()
    {
        if (Running is not { } progress)
        {
            return false;
        }
        progress.Cancel();
        s_epoch++;
        Running = null;
        s_panel.Stopped(progress, s_app.State.Tilde(Root));
        s_app.Footer.SetStatus($"scan cancelled · {HumanCount(progress.Files)} entries · r scans again");
        TestHooks.Log($"cancelled {Root} at {progress.Files} files, {progress.Dirs} dirs, {progress.Bytes} bytes");
        return true;
    }
}

/// What the treemap shows while a scan runs: what is being read and how much
/// so far. A scan has no total to measure against, so the meter shows the
/// work done rather than pretending to know how far along it is.
sealed class ScanPanel
{
    public readonly Gtk.Box Widget;
    readonly Gtk.Spinner _spinner;
    readonly Gtk.Label _title, _files, _dirs, _bytes, _errors, _note, _error;
    readonly Gtk.ProgressBar _meter;
    readonly Gtk.Button _button;
    bool _scanning;

    public ScanPanel(App app)
    {
        Widget = Gtk.Box.New(Gtk.Orientation.Vertical, 18);
        Widget.AddCssClass("scanning");
        Widget.Valign = Gtk.Align.Fill;
        Widget.Halign = Gtk.Align.Fill;
        var content = Gtk.Box.New(Gtk.Orientation.Vertical, 18);
        content.Valign = content.Halign = Gtk.Align.Center;
        content.Vexpand = true;
        Widget.Append(content);

        _spinner = Gtk.Spinner.New();
        _spinner.SetSizeRequest(32, 32);
        content.Append(_spinner);
        _title = Label("", "scan-title");
        content.Append(_title);
        var stats = Gtk.Box.New(Gtk.Orientation.Horizontal, 36);
        stats.Halign = Gtk.Align.Center;
        content.Append(stats);
        _files = Stat(stats, "files");
        _dirs = Stat(stats, "directories");
        _bytes = Stat(stats, "measured");
        _errors = Stat(stats, "unreadable");
        _meter = Gtk.ProgressBar.New();
        _meter.SetSizeRequest(360, -1);
        _meter.Halign = Gtk.Align.Center;
        _meter.AddCssClass("accent");
        content.Append(_meter);
        _note = Label("", "dim");
        content.Append(_note);
        // Focusable off: Space and Return stay the keymap's.
        _button = Gtk.Button.NewWithLabel("Cancel");
        _button.Halign = Gtk.Align.Center;
        _button.FocusOnClick = false;
        _button.OnClicked += (_, _) =>
        {
            if (_scanning)
            {
                Scanning.Cancel();
            }
            else
            {
                Scanning.Start(Scanning.Root);
            }
        };
        content.Append(_button);
        _error = Label("", "scan-error");
        _error.Wrap = true;
        _error.MaxWidthChars = 60;
        content.Append(_error);
        Widget.Visible = false;
    }

    public void Reading(string root)
    {
        _scanning = true;
        _title.SetText($"Reading {root}");
        _note.SetText("Marking, zooming and the free-space meter all work as soon as it lands.");
        _button.Label = "Cancel";
        _spinner.Visible = _meter.Visible = true;
        _spinner.Start();
        _error.Visible = false;
        Count(null);
        Widget.Visible = true;
    }

    public void Stopped(ScanProgress progress, string root)
    {
        Done(progress);
        _title.SetText($"Stopped reading {root}");
        _note.SetText("Nothing is shown from a scan that did not finish.");
    }

    public void Failed(ScanProgress progress, string error)
    {
        Done(progress);
        _error.SetText(error);
        _error.Visible = true;
    }

    void Done(ScanProgress progress)
    {
        _scanning = false;
        Count(progress);
        _spinner.Stop();
        _spinner.Visible = _meter.Visible = false;
        // A failed scan has nothing left to cancel either.
        _button.Label = "Scan again";
        Widget.Visible = true;
    }

    public void Count(ScanProgress? progress)
    {
        var (files, dirs, bytes, errors) = progress is null ? (0L, 0L, 0L, 0L) : (progress.Files, progress.Dirs, progress.Bytes, progress.Errors);
        _files.SetText(HumanCount(files));
        _dirs.SetText(HumanCount(dirs));
        _bytes.SetText(HumanBytes((ulong)bytes));
        _errors.SetText(HumanCount(errors));
        if (errors > 0)
        {
            _errors.AddCssClass("accent");
        }
        else
        {
            _errors.RemoveCssClass("accent");
        }
        _meter.Fraction = Estimate(files);
    }

    // Asymptotic, so the bar keeps moving
    // while the walk continues.
    static double Estimate(long files) =>
        files == 0 ? 0.06 : Math.Min(0.1 + files / (files + 20_000.0) * 0.85, 0.99);

    static Gtk.Label Label(string text, string css)
    {
        var label = Gtk.Label.New(text);
        label.AddCssClass(css);
        return label;
    }

    // A dim label above a number.
    static Gtk.Label Stat(Gtk.Box row, string caption)
    {
        var box = Gtk.Box.New(Gtk.Orientation.Vertical, 2);
        var label = Label(caption, "stat-caption");
        label.Xalign = 0;
        var value = Label("0", "stat-value");
        value.Xalign = 0;
        box.Append(label);
        box.Append(value);
        row.Append(box);
        return value;
    }

    public const string Css = """
        .scanning { background-color: @dt_inset; }
        .scan-title { font-size: 1.6em; font-weight: bold; }
        .stat-caption { font-size: 0.85em; opacity: 0.6; }
        .stat-value { font-size: 1.6em; font-weight: bold; }
        .scan-error { color: @dt_danger; border: 1px solid @dt_danger; padding: 10px; }
        """;
}
