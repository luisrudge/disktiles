// Marking, review and removal (feature 5): space or x marks the tile you
// point at (or the keyboard selection after an arrow), ctrl-click and
// middle-click mark without moving anything, and a mark covers everything
// inside it.
//
// Marks are absolute paths (Disktiles.Core.Marks), so they survive a rescan.
// The panel's parts are in MarkedPanel.cs, the review, running and done
// screens in Review.cs.
using Disktiles.Core;

namespace Disktiles;

enum Status { Neutral, Success, Warning, Error }

/// The marks, the removal mode, and the one notice line they talk through.
sealed class Marker
{
    readonly App _app;
    public readonly Marks Marks = new();
    public readonly TrashBackend Backend = Removal.DetectTrashBackend();
    /// The trash when there is one: it commits without asking.
    public RemovalMode Mode { get; private set; }
    public (string Text, Status Status)? Notice { get; private set; }
    Plan? _plan;
    /// What the last walk found inside each target (Removal.CloudInside),
    /// by path: a walk reads the whole target, so it runs when the review
    /// opens, never on every mark. The worker walks again before it acts.
    readonly Dictionary<string, string?> _walked = [];

    /// The marks changed: paint, panel and review follow.
    public event Action? Changed;
    public event Action? ModeChanged;
    public event Action? NoticeChanged;

    public Marker(App app)
    {
        _app = app;
        Mode = Backend.IsAvailable() ? RemovalMode.Trash : RemovalMode.Permanent;
        // Sizes again from every new tree.
        app.State.TreeChanged += () =>
        {
            // A mark from another disk under the same name keeps its own
            // identity (Target.Where): the plan refuses it, and says so.
            _plan = null;
            if (app.State.Tree is { } tree && Marks.Count > 0)
            {
                Marks.Refresh(Root, tree);
                Touch();
            }
        };
    }

    /// The scanned root, absolute and normalized, as marks are keyed.
    /// Never through Path.GetFullPath: on Windows it trims `junk.` to `junk`.
    public string Root => Removal.Normalize(_app.State.Root);

    /// What the review shows and the removal runs, planned once per change.
    public Plan Plan => _plan ??= Removal.Plan(Marks.Items, Root, path => _walked.GetValueOrDefault(path), _app.State.Volume);

    /// Plan again on demand: the mount table may have landed since. With
    /// `walk`, every target's contents are walked again too.
    public void Replan(bool walk = false)
    {
        _plan = null;
        if (walk)
        {
            _walked.Clear();
            _plan = Removal.Plan(Marks.Items, Root, path => _walked[path] = Removal.CloudInside(path), _app.State.Volume);
        }
    }

    public string PathAt(int[] crumbs) => Removal.Normalize(_app.State.PathOf(crumbs));

    public void SetMode(RemovalMode mode)
    {
        if (mode == RemovalMode.Trash && !Backend.IsAvailable())
        {
            return;
        }
        Mode = mode;
        ModeChanged?.Invoke();
    }

    public void Say(string text, Status status)
    {
        Notice = (text, status);
        NoticeChanged?.Invoke();
    }

    public void Quiet()
    {
        if (Notice is not null)
        {
            Notice = null;
            NoticeChanged?.Invoke();
        }
    }

    /// Mark or unmark the node at `crumbs`. Marking a directory absorbs the
    /// marks inside it; inside a marked directory nothing is marked or kept
    /// on its own: the notice says which mark it goes with.
    public void Toggle(int[] crumbs)
    {
        if (crumbs.Length == 0)
        {
            Say("the scanned root cannot be removed; open a directory first", Status.Warning);
            return;
        }
        if (_app.State.Resolve(crumbs) is not { } node)
        {
            return;
        }
        var path = PathAt(crumbs);
        Quiet();
        if (!Marks.Contains(path) && MarkedAncestor(path) is { } ancestor)
        {
            Say($"{_app.State.Tilde(path)} goes with the marked {_app.State.Tilde(ancestor)}; unmark that to keep it", Status.Warning);
            return;
        }
        // The tree shows the disk scanned: marked once another took its name,
        // the mark would carry that one's identity, and pass for it.
        // Unmarking always works.
        if (!Marks.Contains(path) && _app.State.Volume is { } scanned && Removal.VolumeOf(Root) != scanned)
        {
            Say($"{_app.State.Tilde(Root)} is another disk or folder now than the one scanned: scan it again", Status.Warning);
            return;
        }
        var where = _app.State.Volume is null ? null : Removal.VolumeOf(path) ?? Removal.RootId.Unknown;
        if (Marks.Toggle(new Target(path, node.Bytes, node.IsDir, Marks.IsHidden(path), where)))
        {
            var inside = Marks.Items.Where(item => item.Path != path && Space.PathStartsWith(item.Path, path))
                .Select(item => item.Path).ToList();
            foreach (var inner in inside)
            {
                Marks.Remove(inner);
            }
            if (inside.Count > 0)
            {
                Say($"{_app.State.Tilde(path)} now covers {inside.Count} mark{(inside.Count == 1 ? "" : "s")} inside it", Status.Neutral);
            }
        }
        Touch();
    }

    /// The outermost marked directory `path` is inside; never `path` itself.
    public string? MarkedAncestor(string path) =>
        Marks.Items.Where(item => item.Path != path && Space.PathStartsWith(path, item.Path))
            .Select(item => item.Path).MinBy(ancestor => ancestor.Length);

    public void Unmark(string path)
    {
        Marks.Remove(path);
        Touch();
    }

    public void Clear()
    {
        Marks.Clear();
        Quiet();
        Touch();
    }

    void Touch()
    {
        _plan = null;
        Changed?.Invoke();
    }
}

static class Marking
{
    // The marked fill: the inset mixed toward the theme's
    // danger. Read at paint time, so a theme switch follows.
    static Rgba MarkedFill => Look.Theme.Inset.Mix(Look.Danger, 0.16);

    static Marker? s_marker;

    public static void Install(App app)
    {
        // Read the mount table now, so it is there by the time anything is marked.
        Removal.PrimeMountPoints();
        var marker = s_marker = new Marker(app);
        var state = app.State;

        app.Keys.Bind("space x", "Mark", "mark or unmark", () =>
        {
            if (state.Tree is not null && state.ActionTarget is { } target)
            {
                marker.Toggle(target);
            }
        });
        app.Keys.Bind("c", "Mark", "review the marked list", () =>
        {
            if (marker.Marks.Count == 0)
            {
                marker.Say("mark something first: space marks the selected tile", Status.Warning);
                return;
            }
            state.SetScreen(Screen.Review);
        });

        // Ctrl-click (⌘ on macOS) and the middle button mark without going
        // in or moving the selection. Claimed in the capture phase, so the
        // treemap's own click (go in, go up) never sees them.
        var press = Gtk.GestureClick.New();
        press.Button = 0;
        press.PropagationPhase = Gtk.PropagationPhase.Capture;
        press.OnPressed += (gesture, e) =>
        {
            var button = gesture.GetCurrentButton();
            var modifiers = gesture.GetCurrentEventState();
            var command = modifiers.HasFlag(Gdk.ModifierType.ControlMask)
                || (OperatingSystem.IsMacOS() && modifiers.HasFlag(Gdk.ModifierType.MetaMask));
            if (button == 2 || (button == 1 && command))
            {
                gesture.SetState(Gtk.EventSequenceState.Claimed);
                Click(app, e.X, e.Y);
            }
        };
        app.Treemap.Widget.AddController(press);

        // Marks are painted into the texture, so it is drawn again only when
        // they change, never on hover.
        app.Treemap.Painted += cr => Paint(app, marker, cr);
        marker.Changed += app.Treemap.Render;
        marker.Changed += app.Treemap.RefreshCard;

        if (TestHooks.Trace)
        {
            marker.Changed += () => Console.Error.WriteLine($"trace: marks [{string.Join(", ", marker.Marks.Items.Select(item => $"{item.Path} {item.Bytes}"))}], plan {marker.Plan.Targets.Count} targets, {marker.Plan.Blocked.Count} blocked ({string.Join("; ", marker.Plan.Blocked.Select(b => b.Reason))}), reclaim {marker.Plan.Reclaim}");
            marker.NoticeChanged += () => Console.Error.WriteLine($"trace: notice {marker.Notice}");
            marker.ModeChanged += () => Console.Error.WriteLine($"trace: mode {marker.Mode}");
            state.ScreenChanged += () => Console.Error.WriteLine($"trace: screen {state.Screen}");
        }
        MarkedPanel.Install(app, marker);
        Review.Install(app, marker);
    }

    /// A ctrl-click at (x, y) in the treemap: mark what is there. Also the
    /// DISKTILES_CTRLCLICK test hook.
    /// The hover card's badges for `path`: "Marked for removal", or
    /// "Inside marked X". Null when neither.
    public static string? Badge(string path) => s_marker is not { } marker ? null
        : marker.Marks.Contains(path) ? "Marked for removal"
        : marker.MarkedAncestor(path) is { } ancestor ? $"Inside marked {Path.GetFileName(ancestor)}"
        : null;

    public static void Click(App app, double x, double y)
    {
        if (s_marker is { } marker && app.Treemap.Hit(x, y) is { Kind: TileKind.Node } tile)
        {
            marker.Toggle(tile.Crumbs);
        }
    }

    /// Marked tiles, and everything inside them, take the danger colour:
    /// removing a directory takes its contents. Painted over the tiles, in
    /// their order, so children repaint inside a marked parent; the marked
    /// ring goes last, so no fill covers it.
    static void Paint(App app, Marker marker, Cairo.Context cr)
    {
        if (marker.Marks.Count == 0 || app.State.Tree is not { } tree)
        {
            return;
        }
        var root = marker.Root;
        var marked = new HashSet<string>();
        foreach (var item in marker.Marks.Items)
        {
            if (Marks.CrumbsFor(root, tree, item.Path) is { } crumbs)
            {
                marked.Add(string.Join(',', crumbs));
            }
        }
        var rings = new List<Rect>();
        // Interface zoom scales the header band; TreemapView's labels follow it.
        var zoom = app.State.Layout.Header / AppState.DefaultLayout.Header;
        foreach (var tile in app.Treemap.Tiles)
        {
            if (tile.Kind != TileKind.Node)
            {
                continue;
            }
            var isMarked = marked.Contains(string.Join(',', tile.Crumbs));
            var covered = !isMarked && Enumerable.Range(1, tile.Crumbs.Length - 1)
                .Any(length => marked.Contains(string.Join(',', tile.Crumbs[..length])));
            if (!isMarked && !covered)
            {
                continue;
            }
            var r = tile.Rect;
            cr.SetSource(MarkedFill);
            cr.Rectangle(r.X, r.Y, r.W, r.H);
            cr.Fill();
            if (app.State.Resolve(tile.Crumbs) is { } node)
            {
                // TreemapView's label, in the danger colour.
                TreemapView.PaintLabel(cr, tile, r, tile.Header ?? r, node.Name,
                    app.State.Metric == Metric.Files ? Look.HumanCount(node.Files) : Size.HumanBytesShort(node.Bytes), Look.Danger, zoom);
            }
            if (isMarked)
            {
                rings.Add(r);
            }
        }
        cr.SetSource(Look.Danger);
        cr.LineWidth = 2;
        foreach (var r in rings)
        {
            cr.Rectangle(r.X + 1, r.Y + 1, Math.Max(0, r.W - 2), Math.Max(0, r.H - 2));
        }
        cr.Stroke();
    }
}
