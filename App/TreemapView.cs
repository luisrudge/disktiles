// The treemap: laid out here in base space (the unzoomed treemap's pixels),
// painted as a Drawing (Paint.cs) that the host shows natively in QML's
// DrawView, with the hover outline, the selection ring and the hover card as
// small QML items above it, so pointing never repaints the tiles.
//
// Layout runs only when the folder, the size or the layout changes, and only
// once per turn of the UI loop however many of those happened. Zoom and pan
// (View.cs) change only the view: DrawView moves the picture at once, and a
// sharp one follows 150 ms after the motion settles.
using Disktiles.Core;

namespace Disktiles;

/// One tile as QML paints it. Base-space rect; `Band` is the height of a
/// directory's name band (0: the label owns the whole tile). `Mark`: 0 none,
/// 1 marked (a ring), 2 inside a marked directory.
sealed record TileModel(double X, double Y, double W, double H, double Band, Rgba Fill, Rgba? Accent, bool Hatch,
    string Name, string Size, Rgba Ink, bool Bold, int Mark, int Depth);

/// The hover card: what the tile is, where, how big against its folder.
sealed record CardModel(string Name, string Path, string Size, double Share, string Percent, string Color,
    string Facts, string Badge, string Hint);

/// Going into a directory or out of one: the new picture starts at scale K
/// and offset (X, Y) and eases home in `Duration` ms. `Seq` tells them apart.
sealed record TransitionModel(int Seq, double K, double X, double Y, double Duration);

sealed partial class App
{
    /// The tiles of the folder on screen, laid out on first use after a change.
    public List<Tile> Tiles
    {
        get
        {
            if (_layoutDirty)
            {
                Layout();
            }
            return _tiles;
        }
    }
    List<Tile> _tiles = [];
    bool _layoutDirty;
    public double Width { get; private set; }
    public double Height { get; private set; }
    /// Zoom and pan; change it with SetView. Back to identity in a new folder.
    public View View { get; private set; } = View.Identity;
    public Tile? Hovered { get; private set; }
    public TransitionModel? Transition { get; private set; }
    (double X, double Y) _pointer = (double.NaN, double.NaN);
    // The folder the tiles are for, to tell going in from going out.
    int[] _shown = [];
    int _transitions;
    List<TileModel>? _tileModels;

    void InitTreemap()
    {
        // A new or re-sorted tree lands where it is, without a transition.
        State.TreeChanged += () => _shown = State.Crumbs;
        State.FolderChanged += Arrive;
        State.LayoutChanged += Render;
        State.FilterChanged += Render;
        State.SelectionChanged += () => Touch(Part.View);
    }

    /// The treemap's size in logical pixels, from QML.
    public void Resize(double width, double height)
    {
        if (width == Width && height == Height)
        {
            return;
        }
        (Width, Height) = (width, height);
        Perf.Mark("resize", Perf.F("w", width) + "," + Perf.F("h", height));
        Render();
    }

    /// The folder, the size or the layout changed: lay out again, once, when
    /// the tiles are next needed (at the latest when Backend paints them).
    public void Render()
    {
        _layoutDirty = true;
        _shown = State.Crumbs;
        // A resize keeps the view, inside the new size.
        View = View.Clamped(Width, Height);
        Repaint();
        Hovered = null;
        Hover(_pointer.X, _pointer.Y);
    }

    void Layout()
    {
        _layoutDirty = false;
        if (State.Current is not { } current || Width < 1 || Height < 1)
        {
            _tiles = [];
            return;
        }
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        _tiles = Treemap.Layout(current, State.Crumbs, new Rect(0, 0, (float)Width, (float)Height), State.Metric, State.Layout,
            State.FilterApplied ? State.Matches : null);
        Perf.Mark("layout", Perf.F("ms", Perf.Ms(started)) + "," + Perf.F("tiles", _tiles.Count));
    }

    /// The tiles' colours or labels changed (marks, the theme): paint again,
    /// with the same layout.
    public void Repaint()
    {
        _tileModels = null;
        Touch(Part.Tiles | Part.View | Part.Hover);
    }

    // Children are painted after, and inside, their parent: the last hit is the deepest.
    public Tile? Hit(double x, double y)
    {
        (x, y) = View.Unproject(x, y);
        for (var i = Tiles.Count - 1; i >= 0; i--)
        {
            var r = Tiles[i].Rect;
            if (x >= r.X && x < r.Right && y >= r.Y && y < r.Bottom)
            {
                return Tiles[i];
            }
        }
        return null;
    }

    /// The pointer is at (x, y) in the treemap: the outline, the card and the
    /// panel follow. An identical motion over the same tile does nothing.
    public void Hover(double x, double y)
    {
        var tile = double.IsNaN(x) ? null : Hit(x, y);
        if (x.Equals(_pointer.X) && y.Equals(_pointer.Y) && tile == Hovered)
        {
            return;
        }
        _pointer = (x, y);
        if (tile != Hovered)
        {
            Hovered = tile;
            // Nothing, or the merged tail: no node for a key to act on.
            State.Hover(tile is { Kind: TileKind.Node } ? tile.Crumbs : null);
            Touch(Part.Hover);
            return;
        }
        // The same tile: only the card moves with the pointer.
        Touch(Part.Pointer);
    }

    /// Where the card follows the pointer: [x, y] in the treemap, or empty.
    public double[] CardAt => Hovered is null || double.IsNaN(_pointer.X) ? [] : [_pointer.X, _pointer.Y];

    /// The pointer moved for real (not a relayout's repeat): keys act on
    /// the tile under it until the keyboard moves the selection again.
    public void PointerMoved(double x, double y)
    {
        if (!x.Equals(_pointer.X) || !y.Equals(_pointer.Y))
        {
            State.PointerActive = true;
        }
        Hover(x, y);
    }

    public void Unhover()
    {
        State.PointerActive = false;
        _pointer = (double.NaN, double.NaN);
        if (Hovered is not null)
        {
            Hovered = null;
            State.Hover(null);
        }
        Touch(Part.Hover);
    }

    /// Zoom or pan: the tiles stay laid out, QML paints them at the new view.
    public void SetView(View view)
    {
        view = view.Clamped(Width, Height);
        if (view == View)
        {
            return;
        }
        View = view;
        EndTransition();
        Touch(Part.View | Part.Footer);
        Hover(_pointer.X, _pointer.Y);
        SettleLater();
    }

    long _viewChangedAt;
    bool _settling;

    // DrawView moves the last picture at once; a sharp one for the new view
    // follows once the wheel has been still for 150 ms (as the GTK app did).
    void SettleLater()
    {
        _viewChangedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        if (_settling)
        {
            return;
        }
        _settling = true;
        Ui.Every(50, () =>
        {
            if (Perf.Ms(_viewChangedAt) < 150)
            {
                return true;
            }
            _settling = false;
            if (DrawnView != View)
            {
                Touch(Part.Tiles);
            }
            return false;
        });
    }

    /// Where a drawn directory's contents sit: its tile below the name band.
    /// Null when it is not drawn.
    public Rect? Body(int[] crumbs) => Body(crumbs, Tiles);

    static Rect? Body(int[] crumbs, List<Tile> tiles)
    {
        foreach (var tile in tiles)
        {
            if (tile.Kind == TileKind.Node && tile.Crumbs.AsSpan().SequenceEqual(crumbs))
            {
                return tile.Header is { } band ? new Rect(tile.Rect.X, band.Bottom, tile.Rect.W, tile.Rect.Bottom - band.Bottom) : tile.Rect;
            }
        }
        return null;
    }

    // A new folder lands at scale 1. Into a directory or out of one, it moves
    // as one region changing address: the contents grow from where they were
    // on screen, or the folder left shrinks back into its tile. Other jumps
    // simply land.
    void Arrive()
    {
        var (from, to) = (_shown, State.Crumbs);
        var full = new Rect(0, 0, (float)Width, (float)Height);
        // What is on screen now: the tiles laid out for `from`, not a fresh layout.
        Rect? into = to.Length > from.Length && to.AsSpan().StartsWith(from) && Body(to, _tiles) is { } body ? View.Project(body) : null;
        // Where the folder being left is on screen now: all of it, unzoomed.
        var leaving = View.Project(full);
        View = View.Identity;
        EndTransition();
        Render();
        if (into is { } src)
        {
            StartTransition(src, full);
        }
        else if (to.Length < from.Length && from.AsSpan().StartsWith(to) && Body(from) is { } child)
        {
            StartTransition(leaving, child);
        }
    }

    // Draw the new layout's `dst` where `src` is, then ease it home. One
    // region maps linearly, so moving every tile is moving the picture.
    void StartTransition(Rect src, Rect dst)
    {
        if (src.W <= 1 || src.H <= 1 || dst.W <= 1 || dst.H <= 1)
        {
            return;
        }
        var k = src.W / dst.W;
        // Longer pulls take a little longer, never enough to feel slow.
        var duration = 130 + 45 * Math.Clamp(Math.Log2(Math.Max(k, 1)), 0, 4);
        Transition = new TransitionModel(++_transitions, k, src.X - dst.X * k, src.Y - dst.Y * k, duration);
        Touch(Part.View);
    }

    void EndTransition()
    {
        if (Transition is not null)
        {
            Transition = null;
            Touch(Part.View);
        }
    }

    // ── what QML draws ──────────────────────────────────────────────────

    /// Every tile with its colours and labels; built once per change (Paint.cs draws them).
    public List<TileModel> TileModels => _tileModels ??= BuildTileModels();

    List<TileModel> BuildTileModels()
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            return BuildTileModelsNow();
        }
        finally
        {
            Perf.Mark("vm", Perf.F("ms", Perf.Ms(started)) + "," + Perf.F("tiles", Tiles.Count));
        }
    }

    List<TileModel> BuildTileModelsNow()
    {
        var models = new List<TileModel>(Tiles.Count);
        var marked = MarkedCrumbs();
        foreach (var tile in Tiles)
        {
            var node = tile.Kind == TileKind.Node ? State.Resolve(tile.Crumbs) : null;
            var category = node?.Category ?? Category.Other;
            // Age mode: the last write, where it is known; the kind otherwise.
            var fill = node is { Modified: > 0 and var modified } && State.ColorMode == ColorMode.Age
                ? Look.AgeFill(Look.AgeBucket((State.Now - modified) / 86_400), tile.Depth)
                : Look.Fill(category, tile.Depth);
            // Only what matches the filter keeps its colour; a folder holding
            // matches steps back less, so the way to them stays readable.
            Keep? keep = State.Matches is { } matches ? matches.KeepOf(tile.Crumbs) : new Keep(true, 0, 0);
            var faded = keep is not { Whole: true };
            fill = keep switch
            {
                null => fill.Mix(Look.Theme.Inset, 0.82),
                { Whole: false } => fill.Mix(Look.Theme.Inset, 0.55),
                _ => fill,
            };
            // Hatched once, at the topmost reclaimable tile.
            var hatch = !faded && node is { Reclaim: not Reclaim.None } && State.Resolve(tile.Crumbs[..^1]) is { Reclaim: Reclaim.None };
            Rgba? accent = tile.Depth == 0 ? Look.Accent(category) : null;
            string name, size;
            Rgba ink;
            if (node is not { } shown)
            {
                (name, size, ink) = ($"+{tile.Count} more", "", State.Matches is null ? Look.Label(tile.Depth) : Look.LabelDim);
            }
            else
            {
                (name, size, ink) = (shown.Name, State.Metric == Metric.Files ? Look.HumanCount(shown.Files) : Size.HumanBytesShort(shown.Bytes),
                    faded ? Look.LabelDim : Look.Label(tile.Depth));
            }
            // Marked tiles, and everything inside them, take the danger
            // colour: removing a directory takes its contents.
            var mark = 0;
            if (node is not null && marked.Count > 0)
            {
                var key = string.Join(',', tile.Crumbs);
                mark = marked.Contains(key) ? 1
                    : Enumerable.Range(1, tile.Crumbs.Length - 1).Any(length => marked.Contains(string.Join(',', tile.Crumbs[..length]))) ? 2
                    : 0;
            }
            if (mark != 0)
            {
                (fill, ink, hatch, accent) = (Look.MarkedFill, Look.Danger, false, null);
            }
            var r = tile.Rect;
            models.Add(new TileModel(r.X, r.Y, r.W, r.H, tile.Header?.H ?? 0, fill, accent, hatch, name, size, ink,
                tile.Header is not null && tile.Depth == 0, mark, (int)tile.Depth));
        }
        return models;
    }

    /// The hover outline, on screen: [x, y, w, h], or empty.
    public double[] HoverRect => Hovered is { } tile ? OnScreen(tile.Rect) : [];

    /// The keyboard selection's ring, on screen, or empty when it is not drawn.
    public double[] SelectRect =>
        State.Selected is { } at && Tiles.Find(t => t.Kind == TileKind.Node && t.Crumbs.AsSpan().SequenceEqual(at)) is { } tile
            ? OnScreen(tile.Rect)
            : [];

    double[] OnScreen(Rect rect)
    {
        var r = View.Project(rect);
        return [r.X, r.Y, r.W, r.H];
    }

    /// The card beside the pointer; null when nothing is under it.
    public CardModel? Card
    {
        get
        {
            if (Hovered is not { } tile || double.IsNaN(_pointer.X))
            {
                return null;
            }
            var hit = tile.Kind == TileKind.Node ? State.Resolve(tile.Crumbs) : null;
            var parent = State.Resolve(tile.Kind == TileKind.Node ? tile.Crumbs[..^1] : tile.Crumbs);
            var bytes = hit?.Bytes ?? tile.TailBytes;
            var share = parent is { Bytes: > 0 and var parentBytes } ? (double)bytes / parentBytes : 0;
            var percent = $"{share * 100:0}%";
            if (hit is not { } node)
            {
                return new CardModel($"{Look.Grouped((ulong)tile.Count)} smaller items", "", Look.HumanBytes(bytes), share, percent,
                    Look.Accent(Category.Other).Qml, "", "", "");
            }
            // Counts, then the badges, then the keys.
            ulong direct = node.IsDir ? 0 : node.Bytes;
            foreach (var child in node.Children)
            {
                if (!child.IsDir)
                {
                    direct += child.Bytes;
                }
            }
            var facts = $"{Look.HumanCount(node.Files)} files · {Look.HumanCount(node.Dirs - (node.IsDir ? 1u : 0u))} dirs · {Look.HumanBytes(direct)} direct";
            var path = State.PathOf(tile.Crumbs);
            string?[] badges = [node.Name.StartsWith('.') ? "Hidden" : null, Badge(path)];
            var hint = node.IsDir ? "space mark · enter open" : "space mark";
            return new CardModel((node.IsDir ? "▸ " : "") + node.Name, State.Tilde(path), Look.HumanBytes(bytes), share, percent,
                Look.Accent(node.Category).Qml, facts, string.Join(" · ", badges.OfType<string>()), hint);
        }
    }

    /// "1.6×" while magnified, else empty: in the key bar.
    public string ScaleText => Math.Abs(View.Scale - 1) > 0.01 ? $"{View.Scale:0.0}×" : "";
}
