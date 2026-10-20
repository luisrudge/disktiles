// What QML sees as `backend`: the app's view models (App/*.cs), and the
// events QML hands back (keys, the pointer, buttons). Everything here is a
// thin pass-through; the logic is in App/ and Core/, which tests run without Qt.
//
//     disktiles [OPTIONS] [FOLDER]        (default: $HOME; --help lists the options)
using Disktiles;
using Disktiles.Core;
using Omasharp;

sealed class Backend : BackendBase
{
    readonly App _app;
    Part _pending;
    bool _flushPosted;

    /// The first managed code to run (the AOT library's, or dev mode's, module initializer).
#pragma warning disable CA2255 // measuring: the earliest point managed code runs in the AOT library
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void Loaded() => Perf.Mark("main");
#pragma warning restore CA2255

    public Backend()
    {
        Perf.Mark("toolkit");
        var args = Bridge.Args;
        if (args.Contains("--help") || args.Contains("-h"))
        {
            Console.WriteLine(Cli.Usage);
            Environment.Exit(0);
        }
        if (args.Contains("--version") || args.Contains("-V"))
        {
            Console.WriteLine($"disktiles {Cli.Version}");
            Environment.Exit(0);
        }
        Cli cli;
        try
        {
            cli = Cli.Parse(args, PowerPresets.Load());
        }
        catch (ArgumentException error)
        {
            Console.Error.WriteLine($"Error: {error.Message}");
            Environment.Exit(1);
            throw;
        }
        _app = new App(cli, _dispatcher);
        // What only the window can do (quit, copy, a dialog, a snapshot): QML's onRaised.
        _app.Effected += effect => Raise(effect.Kind, effect);
        Perf.Mark("backend_ready");
        if (Perf.On)
        {
            // What the host's Describe() costs: every property read once, as it does next.
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            foreach (var p in typeof(Backend).GetProperties())
            {
                p.GetValue(this);
            }
            Perf.Mark("getters", Perf.F("ms", Perf.Ms(started)));
        }
        _app.Changed += parts =>
        {
            _pending |= parts;
            // Inside a call from QML or a posted action: flushed when it returns (Act). Elsewhere
            // (a background thread): posted.
            if (_depth == 0 && !_flushPosted)
            {
                _flushPosted = true;
                Ui.Post(Flush);
            }
        };
        _dispatcher.Wrap = action => Act(action);
    }

    readonly BridgeDispatcher _dispatcher = new();
    int _depth;

    // Run a call from QML (or a timer's tick, or a posted continuation), then tell QML what
    // changed, at once. Posting the flush instead would wait behind every frame of a running
    // animation: Qt's threaded render loop keeps the GUI thread blocked in sync for most of each
    // frame, and a queued call can wait several frames for its turn.
    void Act(Action action)
    {
        _depth++;
        try
        {
            action();
        }
        finally
        {
            if (--_depth == 0 && _pending != Part.None)
            {
                Flush();
            }
        }
    }

    T Act<T>(Func<T> action)
    {
        T result = default!;
        Act(() => { result = action(); });
        return result;
    }

    // One notify per view model that changed, once per call or turn of the UI loop.
    void Flush()
    {
        _flushPosted = false;
        if (_pending == Part.None)
        {
            return;
        }
        var parts = _pending;
        _pending = Part.None;
        var names = new List<string>();
        void When(Part part, params string[] properties)
        {
            if ((parts & part) != 0)
            {
                names.AddRange(properties);
            }
        }
        When(Part.View, nameof(View), nameof(SelectRect), nameof(HoverRect), nameof(Transition));
        When(Part.Hover, nameof(HoverRect), nameof(Card), nameof(CardAt));
        When(Part.Pointer, nameof(CardAt));
        When(Part.Selection, nameof(Selection));
        When(Part.Panel, nameof(Worth), nameof(Marked), nameof(Notice), nameof(Disk), nameof(Projection), nameof(PanelShown), nameof(FilterBar));
        When(Part.Header, nameof(Trail), nameof(Totals), nameof(FilterBar), nameof(Legend), nameof(Mode), nameof(IncludeHidden),
            nameof(ApparentSize), nameof(Depth), nameof(BackButton), nameof(ForwardButton), nameof(Title), nameof(Disk));
        When(Part.Footer, nameof(Hints), nameof(Status), nameof(ScaleText));
        When(Part.Scan, nameof(Scan));
        When(Part.Screen, nameof(Screen));
        When(Part.Review, nameof(Review), nameof(Dialog), nameof(RunningScreen), nameof(DoneScreen), nameof(Projection), nameof(Marked));
        When(Part.Overlay, nameof(HelpShown), nameof(Volumes), nameof(Siblings), nameof(FpsShown), nameof(DriveTicking));
        When(Part.Palette, nameof(Palette), nameof(UiZoom));
        if ((parts & Part.Tiles) != 0)
        {
            // The treemap: a native drawing, straight to the host's DrawView (no JSON, no JS).
            var drawing = _app.BuildDrawing();
            var showing = System.Diagnostics.Stopwatch.GetTimestamp();
            Bridge.Show("treemap", drawing);
            // The host has the drawing now (Show is synchronous on the UI thread): ms is its
            // parse of the buffer and the text layouts it had no cached copy of.
            PerfMark("shown", Perf.Ms(showing), _app.TileModels.Count);
        }
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Notify([.. names.Distinct()]);
        Perf.Mark("notify", Perf.F("ms", watch.Elapsed.TotalMilliseconds) + "," + Perf.F("props", names.Count));
    }

    // ── what QML shows ──────────────────────────────────────────────────

    public string Title => _app.Title;
    public string Screen => _app.State.Screen.ToString();
    public Dictionary<string, string> Palette => _app.Palette;
    public double UiZoom => _app.UiZoom;
    /// [scale, originX, originY]: screen = (base - origin) * scale.
    public double[] View => [_app.View.Scale, _app.View.OriginX, _app.View.OriginY];
    public double[] HoverRect => _app.HoverRect;
    public double[] SelectRect => _app.SelectRect;
    public TransitionModel? Transition => _app.Transition;
    public CardModel? Card => _app.Card;
    public double[] CardAt => _app.CardAt;
    public ScanModel Scan => _app.Scan;
    public List<CrumbModel> Trail => _app.Trail;
    public TotalsModel Totals => _app.Totals;
    public FilterModel FilterBar => _app.FilterBar;
    public List<LegendItem> Legend => _app.Legend;
    public int Mode => _app.ModeIndex;
    public bool IncludeHidden => _app.IncludeHidden;
    public bool ApparentSize => _app.ApparentSize;
    public int Depth => (int)_app.State.Layout.MaxDepth;
    public int MinDepth => App.MinDepth;
    public int MaxDepth => App.MaxDepth;
    public StepModel BackButton => _app.BackButton;
    public StepModel ForwardButton => _app.ForwardButton;
    public SelectionModel Selection => _app.Selection;
    public WorthModel Worth => _app.Worth;
    public MarkedModel Marked => _app.Marked;
    public NoticeModel? Notice => _app.NoticeLine;
    public DiskModel Disk => _app.Disk;
    public ProjectionModel Projection => _app.Projection;
    public bool PanelShown => _app.PanelShown;
    public List<Hint> Hints => _app.Hints;
    public string Status => _app.Status;
    public string ScaleText => _app.ScaleText;
    public bool HelpShown => _app.HelpShown;
    public bool FpsShown => _app.FpsShown;
    public bool DriveTicking => _app.DriveTicking;
    /// DISKTILES_PERF is set: QML reports its paints and frames.
    public bool PerfOn => Perf.On;
    public HelpRow[] HelpRows => App.HelpRows;
    public string HelpFoot => _app.HelpFoot;
    public VolumesModel Volumes => _app.Volumes;
    public SiblingsModel Siblings => _app.Siblings;
    public ReviewModel Review => _app.Review;
    public DialogModel Dialog => _app.Dialog;
    public RunningModel RunningScreen => _app.RunningScreen;
    public DoneModel DoneScreen => _app.DoneScreen;

    // ── what QML hands back ─────────────────────────────────────────────

    /// A key press, as Qt delivered it; true when something took it.
    public bool Press(int key, int modifiers, string text) => Act(() => QtKeys.Describe(key, modifiers, text) is { } named && _app.Press(named));

    public void Resize(double width, double height) => Act(() => _app.Resize(width, height));
    public void Hover(double x, double y) => Act(() => _app.PointerMoved(x, y));
    public void Leave() => Act(() => _app.Unhover());

    public void Click(double x, double y, int button, int presses, int modifiers) => Act(() => _app.Click(x, y, button, presses, (modifiers & QtKeys.Control) != 0));

    /// dx, dy in wheel notches, down positive.
    public void Scroll(double x, double y, double dx, double dy, int modifiers) => Act(() => _app.Scroll(x, y, dx, dy, (modifiers & QtKeys.Shift) != 0));

    public void Crumb(int index) => Act(() => _app.Crumb(index));
    public void CrumbMenu(int index) => Act(() => _app.CrumbMenu(index));
    public void PickSibling(int row) => Act(() => _app.PickSibling(row));
    public void CloseSiblings() => Act(() => _app.CloseSiblings());
    public void SetMode(int index) => Act(() => _app.SetMode(index));
    public void ToggleHidden() => Act(() => _app.ToggleHidden());
    public void ToggleApparent() => Act(() => _app.ToggleApparent());
    public void StepDepth(int step) => Act(() => _app.StepDepth(step));
    public void Back() => Act(() => _app.Step(back: true));
    public void Forward() => Act(() => _app.Step(back: false));
    public void OpenWorth(int index) => Act(() => _app.OpenWorth(index));
    public void MarkSelection() => Act(() => _app.MarkSelection());
    public void Unmark(string path) => Act(() => _app.Unmark(path));
    public void OpenReview() => Act(() => _app.OpenReview());
    public void Explore() => Act(() => _app.SetScreen(Disktiles.Screen.Explore));
    public void UseTrash(bool trash) => Act(() => _app.SetRemovalMode(trash ? RemovalMode.Trash : RemovalMode.Permanent));
    public void UnmarkAll() => Act(() => _app.UnmarkAll());
    public void SaveList() => Act(() => _app.SaveList());
    public void SaveListTo(string path) => Act(() => _app.SaveListTo(path));
    public void CopyPrompt() => Act(() => _app.CopyPrompt());
    public void Commit() => Act(() => _app.Commit());
    public void Confirm() => Act(() => _app.Confirm());
    public void CloseDialog() => Act(() => _app.CloseDialog());
    public void StopRemoval() => Act(() => _app.StopRemoval());
    public void ScanButton() => Act(() => _app.ScanButton());
    public void OpenVolumes() => Act(() => _app.OpenVolumes());
    public void PickVolume(int index) => Act(() => _app.PickVolume(index));
    public void CloseVolumes() => Act(() => _app.CloseVolumes());
    public void CloseHelp() => Act(() => _app.CloseHelp());
    public void FolderChosen(string path) => Act(() => _app.FolderChosen(path));
    /// The host's theme changed (QML watches it); `dark` is Qt's colour scheme, for off Omarchy.
    public void ReloadTheme(bool dark) => Act(() => _app.LoadTheme(dark));

    public void DriveTick() => Act(() => _app.DriveTick());

    bool _firstFrame = true, _paintPending, _treemapShown;
    double _paintAt;

    /// QML's side of the measurements: "window", "tiles_in", "paint" (ms, tiles).
    public void PerfMark(string what, double ms, int tiles)
    {
        Perf.Mark(what, Perf.F("ms", ms) + "," + Perf.F("tiles", tiles));
        if (what == "shown")
        {
            (_paintPending, _paintAt) = (true, Perf.Now);
            _treemapShown |= tiles > 0;
        }
    }

    /// A frame was presented (QQuickWindow::frameSwapped).
    public void PerfFrame()
    {
        Perf.Frame();
        if (_firstFrame)
        {
            _firstFrame = false;
            Perf.Mark("frame");
        }
        if (_paintPending)
        {
            _paintPending = false;
            Perf.Mark("presented", Perf.F("ms", Perf.Now - _paintAt));
            if (_treemapShown)
            {
                _treemapShown = false;
                if (!_firstTreemap)
                {
                    _firstTreemap = true;
                    Perf.Mark("first_treemap", Perf.F("cpu", Perf.CpuMs()));
                    Perf.MarkThreads("first_treemap_threads");
                    Perf.Flush();
                }
            }
        }
    }

    bool _firstTreemap;

    /// The window showed its first frame: with --test-log (or DISKTILES_LOG), how long that took.
    public void FirstFrame() =>
        _app.Log($"first frame {(DateTime.Now - System.Diagnostics.Process.GetCurrentProcess().StartTime).TotalMilliseconds:0} ms after the process started ({(Bridge.IsNativeAot ? "aot" : "jit")})");
}

/// The app's UI thread is the bridge's: posts go through Ui.Post, timers
/// tick on a pool thread and post their work.
sealed class BridgeDispatcher : IDispatcher
{
    // Held until done: a collected timer stops firing.
    readonly HashSet<Timer> _timers = [];

    /// Runs each posted action and tick (Backend: flush what it changed right after).
    public Action<Action> Wrap { get; set; } = action => action();

    public void Post(Action action) => Ui.Post(() => Wrap(action));

    public void Every(int ms, Func<bool> tick)
    {
        Timer? timer = null;
        timer = new Timer(_ => Ui.Post(() => Wrap(() =>
        {
            if (tick())
            {
                timer!.Change(ms, Timeout.Infinite);
            }
            else
            {
                lock (_timers)
                {
                    _timers.Remove(timer!);
                }
                timer!.Dispose();
            }
        })));
        lock (_timers)
        {
            _timers.Add(timer);
        }
        timer.Change(Math.Max(ms, 1), Timeout.Infinite);
    }
}
