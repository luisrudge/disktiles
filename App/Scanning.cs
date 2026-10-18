// Scanning, modes and scan targets (feature 3): one scan at a time, its live
// counters over the treemap while it runs, the status line, `r`/F5 to scan
// again and Esc to stop; the Size/Files/Age switch, Hidden and Apparent; the
// volume picker, ctrl+o and `g`.
using System.Diagnostics;
using Disktiles.Core;

namespace Disktiles;

/// What the treemap shows while a scan runs, or after one stopped or failed.
/// A scan has no total to measure against, so the meter shows the work done
/// rather than pretending to know how far along it is.
sealed record ScanModel(bool Visible, bool Scanning, string Title, string Files, string Dirs, string Bytes, string Errors,
    bool ErrorsAccent, double Fraction, string Note, string Error, string Button);

/// One legend entry: a swatch and its label; `Hatch` for "Reclaimable".
sealed record LegendItem(string Label, string Color, bool Hatch);

/// The volume picker: a card over the treemap that owns the keys while open.
sealed record VolumesModel(bool Visible, string Status, List<string> Rows, int Selected);

sealed partial class App
{
    /// Where the scan in flight, or the last one, is rooted: what `r` scans
    /// again. Ahead of State.Root while a new root is being read.
    public string ScanRoot { get; private set; } = "";
    /// The scan in flight, if any: its counters, and the way to stop it.
    public ScanProgress? Running { get; private set; }
    /// Paths the last scan that landed could not read.
    public long Unreadable { get; private set; }
    /// Each scan that lands (true) or fails (false); not one that was stopped or replaced.
    public event Action<bool>? Landed;
    // Bumped per scan and per cancel: only the newest scan's result lands.
    int _epoch;
    ScanModel _scan = new(false, false, "", "0", "0", "0 B", "0", false, 0, "", "", "Cancel");

    void InitScanning()
    {
        State.Options = Cli.Options;
        State.SetMetric(Cli.Options.Metric);
        State.SetLayout(DefaultLayout with { MaxDepth = Cli.Depth });
        Keys.Bind("r F5 ctrl+r", "Scan", "scan again", () => Start(ScanRoot));
        // Below a filter's Escape, above going up.
        Keys.Bind("Escape", "Scan", "stop the scan", CancelScan, priority: 5);

        Keys.Bind("t", "Measure", "size, files or age: what areas and colours say", () => SetMode((ModeIndex + 1) % ModeNames.Length));
        Keys.Bind("d", "Measure", "disk usage or apparent size", ToggleApparent);
        Keys.Bind("i", "Measure", "include or skip hidden entries", ToggleHidden);
        State.LayoutChanged += LogMode;
        State.TreeChanged += LogMode;

        InitTargets();
    }

    public ScanModel Scan => _scan;

    /// Scan `root` from scratch with State.Options, abandoning any walk in
    /// progress. The treemap is covered until the tree lands.
    public void Start(string root, Action<NodeRef?>? landed = null)
    {
        Running?.Cancel();
        var progress = Running = new ScanProgress();
        var epoch = ++_epoch;
        ScanRoot = root;
        // Ranked by the metric on screen, so Files mode stays Files.
        var options = State.Options with { Metric = State.Metric };
        var watch = Stopwatch.StartNew();
        Unhover();
        _scan = new ScanModel(true, true, $"Reading {State.Tilde(root)}", "0", "0", "0 B", "0", false, Estimate(0),
            "Marking, zooming and the free-space meter all work as soon as it lands.", "", "Cancel");
        Touch(Part.Scan | Part.Header);
        Log($"scan {root} ({options})");
        Perf.Mark("scan_start", Perf.F("root", root));
        Ui.Every(110, () =>
        {
            // Stops once this scan lands, fails or is replaced.
            if (epoch != _epoch || Running != progress)
            {
                return false;
            }
            Count(progress);
            SetStatus($"scanning · {Look.HumanCount(progress.Files)} entries · {Look.HumanBytes((ulong)progress.Bytes)}");
            Log($"progress {progress.Files} files, {progress.Dirs} dirs, {progress.Bytes} bytes, {progress.Errors} unreadable");
            return true;
        });
        // A thread of its own: Scan blocks until the walk is done, and on a
        // pool thread it would hold one the listers could use.
        Task.Factory.StartNew(() => Scanner.Scan(root, options, progress),
                CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)
            .ContinueWith(scan => Ui.Post(() => Land(scan, root, options, progress, epoch, watch, landed)), TaskScheduler.Default);
    }

    void Land(Task<NodeRef> scan, string root, ScanOptions options, ScanProgress progress, int epoch, Stopwatch watch, Action<NodeRef?>? landed)
    {
        if (scan.Exception?.InnerException is { } error and not OperationCanceledException)
        {
            if (epoch == _epoch)
            {
                Running = null;
                Count(progress, done: true);
                _scan = _scan with { Error = error.Message };
                Touch(Part.Scan);
                SetStatus($"scan failed · {error.Message}");
                Log($"scan failed: {error.Message}");
                landed?.Invoke(null);
                Landed?.Invoke(false);
            }
            return;
        }
        if (!scan.IsCompletedSuccessfully || epoch != _epoch)
        {
            return;
        }
        var tree = scan.Result;
        Perf.Mark("scan_end", Perf.F("ms", watch.Elapsed.TotalMilliseconds) + "," + Perf.F("entries", progress.Files + progress.Dirs)
            + "," + Perf.F("files", (double)tree.Files));
        Running = null;
        _scan = _scan with { Visible = false, Scanning = false };
        // The mode switched while it was read: rank it the way it is shown.
        if (State.Metric != options.Metric)
        {
            Tree.Aggregate(tree, State.Metric);
        }
        Unreadable = progress.Errors;
        State.SetTree(root, tree);
        landed?.Invoke(tree);
        Landed?.Invoke(true);
        Touch(Part.All);
        SetStatus($"scan {Look.HumanCount(progress.Files)} entries · {watch.Elapsed.TotalSeconds:0.0} s");
        Log($"landed {root}: {tree.Files} files, {tree.Dirs} dirs, {tree.Bytes} bytes, first [{string.Join(", ", tree.Children.Take(3).Select(c => $"{c.Name} {c.Bytes}B {c.Files}f"))}] in {watch.ElapsedMilliseconds} ms, rss {Environment.WorkingSet >> 20} MB, heap {GC.GetGCMemoryInfo().HeapSizeBytes >> 20} MB");
    }

    /// Esc while scanning: stop. A partial tree is never shown as if it were
    /// the whole one; the panel says it stopped. False when nothing runs.
    public bool CancelScan()
    {
        if (Running is not { } progress)
        {
            return false;
        }
        progress.Cancel();
        _epoch++;
        Running = null;
        Count(progress, done: true);
        _scan = _scan with { Title = $"Stopped reading {State.Tilde(ScanRoot)}", Note = "Nothing is shown from a scan that did not finish." };
        Touch(Part.Scan);
        SetStatus($"scan cancelled · {Look.HumanCount(progress.Files)} entries · r scans again");
        Log($"cancelled {ScanRoot} at {progress.Files} files, {progress.Dirs} dirs, {progress.Bytes} bytes");
        return true;
    }

    /// The scan panel's button: Cancel while it runs, Scan again after.
    public void ScanButton()
    {
        if (Running is not null)
        {
            CancelScan();
        }
        else
        {
            Start(ScanRoot);
        }
    }

    void Count(ScanProgress progress, bool done = false)
    {
        var (files, dirs, bytes, errors) = (progress.Files, progress.Dirs, progress.Bytes, progress.Errors);
        _scan = _scan with
        {
            Visible = true,
            Scanning = !done,
            Files = Look.HumanCount(files),
            Dirs = Look.HumanCount(dirs),
            Bytes = Look.HumanBytes((ulong)Math.Max(bytes, 0)),
            Errors = Look.HumanCount(errors),
            ErrorsAccent = errors > 0,
            Fraction = Estimate(files),
            // A failed or stopped scan has nothing left to cancel.
            Button = done ? "Scan again" : "Cancel",
        };
        Touch(Part.Scan);
    }

    // Asymptotic, so the bar keeps moving while the walk continues.
    static double Estimate(long files) =>
        files == 0 ? 0.06 : Math.Min(0.1 + files / (files + 20_000.0) * 0.85, 0.99);

    // ── modes: Size | Files | Age, Hidden, Apparent ─────────────────────

    public static readonly string[] ModeNames = ["Size", "Files", "Age"];

    public int ModeIndex => State.ColorMode == ColorMode.Age ? 2 : State.Metric == Metric.Files ? 1 : 0;

    public bool IncludeHidden => State.Options.IncludeHidden;
    public bool ApparentSize => State.Options.ApparentSize;

    public void ToggleHidden() => Rescan(State.Options with { IncludeHidden = !State.Options.IncludeHidden });
    public void ToggleApparent() => Rescan(State.Options with { ApparentSize = !State.Options.ApparentSize });

    void Rescan(ScanOptions options)
    {
        State.Options = options;
        Start(ScanRoot);
    }

    void LogMode() =>
        Log($"mode {ModeNames[ModeIndex]}, hidden {IncludeHidden}, apparent {ApparentSize}, age legend {State.ColorMode == ColorMode.Age}, first [{string.Join(", ", State.Current?.Children.Take(3).Select(c => $"{c.Name} {c.Bytes}B {c.Files}f") ?? [])}]");

    /// Size, Files (areas and order by file count) or Age.
    public void SetMode(int index)
    {
        State.SetColorMode(index == 2 ? ColorMode.Age : ColorMode.Kind);
        var metric = index == 1 ? Metric.Files : Metric.Bytes;
        if (metric != State.Metric)
        {
            Resort(metric);
        }
        Touch(Part.Header | Part.Panel | Part.Selection);
    }

    /// Children are ordered by the metric, so every crumb moves. Keep the
    /// names and find them again in the re-sorted tree.
    void Resort(Metric metric)
    {
        if (State.Tree is not { } tree)
        {
            State.SetMetric(metric);
            return;
        }
        var folder = NamesOf(tree, State.Crumbs);
        var selected = State.Selected is { } at ? NamesOf(tree, at) : null;
        // A re-sorted copy, not a sort in place: a filter still reading
        // the old tree reads it whole. What dedup charged once stays so.
        tree = Tree.Resorted(tree, metric);
        State.Resort(tree, metric, CrumbsOf(tree, folder) ?? [], selected is null ? null : CrumbsOf(tree, selected));
    }

    static string[] NamesOf(NodeRef tree, int[] crumbs)
    {
        var names = new string[crumbs.Length];
        for (var i = 0; i < crumbs.Length; i++)
        {
            tree = tree.Children[crumbs[i]];
            names[i] = tree.Name;
        }
        return names;
    }

    static int[]? CrumbsOf(NodeRef tree, string[] names)
    {
        var crumbs = new int[names.Length];
        for (var i = 0; i < names.Length; i++)
        {
            var index = tree.Children.IndexOf(names[i]);
            if (index < 0)
            {
                return null;
            }
            crumbs[i] = index;
            tree = tree.Children[index];
        }
        return crumbs;
    }

    /// The key to the kind colours, or in Age mode the ramp newest first;
    /// the hatch first in both.
    public List<LegendItem> Legend
    {
        get
        {
            var items = new List<LegendItem> { new("Reclaimable", Look.Fill(Category.Other, 0).Qml, true) };
            if (State.ColorMode == ColorMode.Age)
            {
                for (var bucket = 0; bucket < Look.AgeBuckets.Length; bucket++)
                {
                    items.Add(new(Look.AgeBuckets[bucket].Label, Look.AgeAccent(bucket).Qml, false));
                }
            }
            else
            {
                items.AddRange(Look.Legend.Select(category => new LegendItem(Look.CategoryLabel(category), Look.Accent(category).Qml, false)));
            }
            return items;
        }
    }

    // ── targets: v, ctrl+o, g ───────────────────────────────────────────

    List<Volume> _volumes = [];
    bool _volumesOpen, _volumesLoading;
    int _volumesEpoch, _volumeSelected;
    string _volumesStatus = "";

    void InitTargets()
    {
        Keys.Bind("v", "Scan", "scan another mounted volume", OpenVolumes);
        Keys.Bind("ctrl+o", "Scan", "choose another directory to scan", ChooseFolder);
        Keys.Bind("g", "Scan", "the whole disk", WholeDisk);
    }

    /// The disk of what is on screen, following the root: from ~ that is `/`
    /// on Omarchy. Already there: go to its top.
    void WholeDisk()
    {
        if (Space.VolumeRootFor(ScanRoot) is not { } disk)
        {
            return;
        }
        if (Space.PathEquals(disk, State.Root) && Running is null)
        {
            State.GoTo([]);
        }
        else
        {
            // ponytail: a fresh walk; the tree already scanned is not reused
            // as a memoized subtree of the wider one.
            Start(disk);
        }
    }

    /// ctrl+o: QML opens its folder dialog (the desktop's chooser), and
    /// answers with FolderChosen. --test-pick answers at once instead: a
    /// chooser in a headless test would open on the real screen.
    void ChooseFolder()
    {
        if (Hook("pick") is { } picked)
        {
            FolderChosen(picked);
            return;
        }
        Emit(new Effect("pickFolder", State.Root));
    }

    public void FolderChosen(string path)
    {
        // The dialog does not block the window: the review list or a
        // removal may have started while it was open.
        if (path.Length == 0 || State.Screen != Screen.Explore)
        {
            return;
        }
        Log($"chose folder {path}");
        Start(Space.RealPath(path) ?? path);
    }

    public VolumesModel Volumes => new(_volumesOpen, _volumesStatus,
        [.. _volumes.Select(volume =>
        {
            var free = volume.Space is { } space ? $"{Look.HumanBytes(space.Available)} free" : "unknown free";
            return volume.Device is { } device ? $"{volume.Point}  ·  {device}  ·  {free}" : $"{volume.Point}  ·  {free}";
        })], _volumeSelected);

    public void OpenVolumes()
    {
        if (_volumesOpen)
        {
            return;
        }
        var epoch = ++_volumesEpoch;
        var root = ScanRoot;
        _volumes = [];
        _volumeSelected = 0;
        (_volumesOpen, _volumesLoading, _volumesStatus) = (true, true, "Looking for volumes…");
        Keys.Grab(VolumeKeys);
        Touch(Part.Overlay);
        // Removable media and network volumes can take seconds to answer.
        Task.Run(() =>
        {
            var volumes = Space.Volumes();
            if (Space.VolumeRootFor(root) is { } disk && !volumes.Any(volume => volume.Point == disk))
            {
                volumes.Add(new Volume(disk, null, SpaceInfo.Of(disk)));
            }
            volumes.RemoveAll(volume => Space.PathEquals(volume.Point, root));
            return volumes;
        }).ContinueWith(task => Ui.Post(() =>
        {
            if (epoch != _volumesEpoch || !_volumesOpen)
            {
                return;
            }
            _volumesLoading = false;
            _volumes = task.IsCompletedSuccessfully ? task.Result : [];
            Log($"volumes: {string.Join(" | ", _volumes.Select(v => $"{v.Point} {v.Device} {v.Space?.Available}"))}");
            _volumesStatus = _volumes.Count == 0 ? "No other volume could be read." : "";
            Touch(Part.Overlay);
        }), TaskScheduler.Default);
    }

    bool VolumeKeys(Key key)
    {
        switch (key.Name)
        {
            case "Escape": CloseVolumes(); break;
            case "Return": PickVolume(_volumeSelected); break;
            case "Up" or "k": MoveVolume(-1); break;
            case "Down" or "j": MoveVolume(1); break;
        }
        // Nothing behind the picker acts while it is open.
        return true;
    }

    // Wrapping at the ends.
    void MoveVolume(int step)
    {
        if (_volumes.Count > 0)
        {
            _volumeSelected = (_volumeSelected + step + _volumes.Count) % _volumes.Count;
            Touch(Part.Overlay);
        }
    }

    public void PickVolume(int index)
    {
        if (_volumesLoading)
        {
            return;
        }
        var point = index >= 0 && index < _volumes.Count ? _volumes[index].Point : null;
        CloseVolumes();
        if (point is not null)
        {
            Start(point);
        }
    }

    public void CloseVolumes()
    {
        _volumesOpen = false;
        Keys.Release(VolumeKeys);
        Touch(Part.Overlay);
    }
}
