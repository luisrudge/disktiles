// The treemap: painted once per layout into a texture, with the hover outline
// and the hover card as small widgets above it.
//
// The treemap is painted only when the folder, the size or the layout
// changes: a DrawingArea is rasterized on the CPU and re-uploaded, which on
// an old dual-core laptop cost a frame its 60 fps, where a texture goes to
// the GPU once and stays there. Hover moves small widgets above it instead. Never call
// Render on hover.
//
// Zoom and pan (View.cs) never repaint during the motion either: the GPU
// moves the textures already made, and the view is painted sharp once, when
// the motion settles.
using System.Diagnostics;
using Disktiles.Core;
using static Disktiles.Look;

namespace Disktiles;

sealed class TreemapView
{
    public readonly Gtk.Overlay Widget;
    readonly AppState _state;
    readonly Gtk.Window _window;
    // Draws nothing: it is the treemap's size, its resize signal, and the
    // target for the pointer. The picture above it shows the texture.
    readonly Gtk.DrawingArea _area;
    // The textures, each with its own transform (PlaceTextures):
    // the base, the whole layout at scale 1, and above it the detail, the
    // view as it was when the last motion settled. Zooming out of a detail
    // shows the base around it until the next one is painted.
    readonly Gtk.Fixed _layer;
    readonly Gtk.Picture _picture;
    readonly Gtk.Picture _detail;
    // The textures they show, ours to release when replaced.
    Gdk.MemoryTexture? _baseTexture;
    Gdk.MemoryTexture? _detailTexture;
    View _detailView;
    // The logical size both textures were painted at.
    (int W, int H) _painted;
    // Mid-transition: the base texture's own transform, x * K + (X, Y).
    (double K, double X, double Y)? _motion;
    int _transitions;
    long _viewChanged;
    uint _settle;
    // The folder the tiles are for, to tell going in from going out.
    int[] _shown = [];
    readonly Gtk.Box[] _edges = new Gtk.Box[4];
    readonly HoverCard _card = new();
    (int W, int H) _cardSize;
    (double X, double Y) _pointer;


    public List<Tile> Tiles { get; private set; } = [];
    public Tile? Hovered { get; private set; }
    /// Zoom and pan; change it with SetView. Back to identity in a new folder.
    public View View
    {
        get => _view;
        private set
        {
            if (value != _view)
            {
                _view = value;
                ViewChanged?.Invoke();
            }
        }
    }
    View _view = View.Identity;
    /// Zoomed, panned, or back to identity in a new folder.
    public event Action? ViewChanged;
    public int Width => _area.GetWidth();
    public int Height => _area.GetHeight();

    /// A press on the treemap (1 left, 2 middle, 3 right, 8/9 back/forward),
    /// with the tile under it.
    public event Action<Tile?, uint>? Clicked;
    /// A scroll step at (x, y): dx, dy in wheel notches (down is positive),
    /// and the modifiers held.
    public event Action<double, double, double, double, Gdk.ModifierType>? Scrolled;
    /// After the tiles are painted, before the texture is made: draw what
    /// belongs in it (marks, a filter's dimming). In base space: the view's
    /// transform is applied.
    public event Action<Cairo.Context>? Painted;
    /// Each repaint, with what it cost in ms.
    public event Action<double>? Rendered;

    public TreemapView(AppState state, Gtk.Window window)
    {
        _state = state;
        _window = window;
        _area = Gtk.DrawingArea.New();
        _area.Hexpand = _area.Vexpand = true;
        _area.Focusable = true;
        _picture = Gtk.Picture.New();
        _picture.CanShrink = true;
        _picture.ContentFit = Gtk.ContentFit.Fill;
        _picture.CanTarget = false;
        _detail = Gtk.Picture.New();
        _detail.CanShrink = true;
        _detail.ContentFit = Gtk.ContentFit.Fill;
        _detail.Visible = false;
        // A Fixed is allocated at least as large as its moved children, and
        // draws past its edges: the overlay above clips it to the treemap.
        _layer = Gtk.Fixed.New();
        _layer.CanTarget = false;
        _layer.Put(_picture, 0, 0);
        _layer.Put(_detail, 0, 0);

        // Above the treemap, none of them a target for the pointer: the hover
        // outline, the hover card. The outline is four thin strips, not one
        // bordered box: GTK re-renders the bounds of what changed, and a box
        // the size of a big tile would make that the tile.
        for (var i = 0; i < _edges.Length; i++)
        {
            _edges[i] = Gtk.Box.New(Gtk.Orientation.Vertical, 0);
            _edges[i].AddCssClass("outline");
            _edges[i].Halign = _edges[i].Valign = Gtk.Align.Start;
            _edges[i].CanTarget = false;
            _edges[i].Visible = false;
        }
        Widget = Gtk.Overlay.New();
        // The card never paints outside the treemap, over the panel.
        Widget.Overflow = Gtk.Overflow.Hidden;
        Widget.Child = _area;
        Widget.AddOverlay(_layer);
        foreach (var edge in _edges)
        {
            Widget.AddOverlay(edge);
        }
        Widget.AddOverlay(_card.Widget);

        _area.OnResize += (_, _) => Render();
        // A new or re-sorted tree lands where it is, without a transition.
        state.TreeChanged += () => _shown = state.Crumbs;
        state.FolderChanged += Arrive;
        state.LayoutChanged += Render;
        state.FilterChanged += Render;

        var click = Gtk.GestureClick.New();
        click.Button = 0;  // every button
        click.OnPressed += (gesture, e) => Click(e.X, e.Y, gesture.GetCurrentButton(), e.NPress);
        _area.AddController(click);

        var scroll = Gtk.EventControllerScroll.New(Gtk.EventControllerScrollFlags.BothAxes);
        scroll.OnScroll += (controller, e) =>
        {
            // A touchpad's pixels count 24 to a notch.
            var notch = controller.GetUnit() == Gdk.ScrollUnit.Surface ? 1 / 24.0 : 1;
            Scroll(_pointer.X, _pointer.Y, e.Dx * notch, e.Dy * notch, controller.GetCurrentEventState());
            return Scrolled is not null;
        };
        _area.AddController(scroll);

        var motion = Gtk.EventControllerMotion.New();
        motion.OnMotion += (_, e) =>
        {
            if (TestHooks.Trace)
            {
                Console.Error.WriteLine($"trace: motion {e.X:0.0},{e.Y:0.0}");
            }
            Hover(e.X, e.Y);
        };
        motion.OnEnter += (_, e) =>
        {
            if (TestHooks.Trace)
            {
                Console.Error.WriteLine($"trace: enter {e.X:0.0},{e.Y:0.0}");
            }
        };
        motion.OnLeave += (_, _) =>
        {
            if (TestHooks.Trace)
            {
                Console.Error.WriteLine("trace: leave");
            }
            Unhover();
        };
        _area.AddController(motion);
    }

    /// Above the treemap, in the order added; set CanTarget = false so the
    /// pointer still reaches the treemap.
    public void AddOverlay(Gtk.Widget widget) => Widget.AddOverlay(widget);

    /// The press count of the click being delivered: 2 for a double click.
    public int Presses { get; private set; } = 1;

    public void Click(double x, double y, uint button, int presses = 1)
    {
        _area.GrabFocus();
        Presses = presses;
        Clicked?.Invoke(Hit(x, y), button);
    }

    public void Scroll(double x, double y, double dx, double dy, Gdk.ModifierType modifiers) =>
        Scrolled?.Invoke(x, y, dx, dy, modifiers);

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

    /// Point at (x, y): the outline and the card follow, and the panel shows
    /// the tile, unless `panel` is false (the sweep's measuring mode).
    public void Hover(double x, double y, bool panel = true)
    {
        var tile = Hit(x, y);
        // GTK repeats the pointer's last position after every relayout, and
        // moving the card is a relayout: answering the repeat moved the card
        // again, every frame, which was the flicker. A still pointer over
        // the same tile has nothing to change.
        if (x == _pointer.X && y == _pointer.Y && tile == Hovered && (tile is null || _card.Widget.Visible))
        {
            return;
        }
        _pointer = (x, y);
        if (tile != Hovered)
        {
            Hovered = tile;
            // Nothing, or the merged tail: no node for a key to act on.
            if (panel)
            {
                _state.Hover(tile is { Kind: TileKind.Node } ? tile.Crumbs : null);
            }
            if (tile is null)
            {
                Outline(null);
                _card.Widget.Visible = false;
                return;
            }
            Outline(View.Project(tile.Rect));
            FillCard(tile);
            MeasureCard();
        }
        if (tile is null)
        {
            return;
        }
        // Beside the pointer, flipped to stay inside the treemap, at the size
        // measured when its text was set (never re-measured per motion: the
        // card's own position must not feed back into its size).
        var (width, height) = (Width, Height);
        var (w, h) = _cardSize;
        var cx = x + 18 + w > width ? x - 18 - w : x + 18;
        var cy = y + 18 + h > height ? y - 18 - h : y + 18;
        _card.Widget.MarginStart = (int)Math.Clamp(cx, 4, Math.Max(4, width - w - 4));
        _card.Widget.MarginTop = (int)Math.Clamp(cy, 4, Math.Max(4, height - h - 4));
        if (TestHooks.Trace)
        {
            Console.Error.WriteLine($"trace: hover {x:0.0},{y:0.0} in {width}x{height} -> {Describe(tile)}, card {w}x{h} at {_card.Widget.MarginStart},{_card.Widget.MarginTop}");
        }
    }

    public void Unhover()
    {
        Hovered = null;
        Outline(null);
        _card.Widget.Visible = false;
    }

    /// Lay out and paint the folder at the treemap's size, at device pixels,
    /// into the base texture, and the view into the detail when zoomed.
    /// Every tile is new: point again.
    public void Render()
    {
        // Render runs from GTK's own layout (resize, a scale change): an
        // exception escaping into GTK aborts the process, which the zoom work
        // hit once. Keep the last picture and say what went wrong instead.
        try
        {
            RenderNow();
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"disktiles: repaint failed, keeping the last picture: {error}");
        }
    }

    void RenderNow()
    {
        var (width, height) = (Width, Height);
        if (_state.Current is not { } current || width < 1 || height < 1)
        {
            return;
        }
        var started = Stopwatch.GetTimestamp();
        Tiles = Treemap.Layout(current, _state.Crumbs, new Rect(0, 0, width, height), _state.Metric, _state.Layout,
            _state.FilterApplied ? _state.Matches : null);
        _shown = _state.Crumbs;
        _painted = (width, height);
        _picture.SetSizeRequest(width, height);
        _detail.SetSizeRequest(width, height);
        Show(_picture, ref _baseTexture, Texture(View.Identity, started));
        // A resize keeps the view, inside the new size.
        View = View.Clamped(width, height);
        if (View.IsIdentity)
        {
            _detail.Visible = false;
        }
        else
        {
            PaintDetail();
        }
        PlaceTextures();
        Unhover();
        Hover(_pointer.X, _pointer.Y);
    }

    /// Zoom or pan: the textures move at once, the outline follows, and the
    /// view is painted sharp when it has been still for a moment.
    public void SetView(View view)
    {
        view = view.Clamped(Width, Height);
        if (view == View)
        {
            return;
        }
        View = view;
        EndTransition();
        _viewChanged = Stopwatch.GetTimestamp();
        if (view.IsIdentity)
        {
            // The base is exact at scale 1.
            _detail.Visible = false;
        }
        else if (_settle == 0)
        {
            _settle = GLib.Functions.TimeoutAdd(GLib.Constants.PRIORITY_DEFAULT, 50, Settle);
        }
        PlaceTextures();
        // The pointer stands still while the tiles move under it.
        if (Hit(_pointer.X, _pointer.Y) is { } tile && tile == Hovered)
        {
            Outline(View.Project(tile.Rect));
        }
        else
        {
            Hover(_pointer.X, _pointer.Y);
        }
    }

    // A wheel sends a notch every few frames: paint once it has stopped.
    bool Settle()
    {
        if (Stopwatch.GetElapsedTime(_viewChanged).TotalMilliseconds < 150)
        {
            return true;
        }
        _settle = 0;
        if (!View.IsIdentity && (!_detail.Visible || _detailView != View))
        {
            PaintDetail();
            PlaceTextures();
        }
        return false;
    }

    void PaintDetail()
    {
        Show(_detail, ref _detailTexture, Texture(View, Stopwatch.GetTimestamp()));
        _detailView = View;
        _detail.Visible = true;
    }

    /// Where a drawn directory's contents sit: its tile below the name band.
    /// Null when it is not drawn.
    public Rect? Body(int[] crumbs)
    {
        foreach (var tile in Tiles)
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
        var (from, to) = (_shown, _state.Crumbs);
        var full = new Rect(0, 0, Width, Height);
        Rect? into = to.Length > from.Length && to.AsSpan().StartsWith(from) && Body(to) is { } body ? View.Project(body) : null;
        // Where the folder being left is on screen now: all of it, unzoomed.
        var leaving = View.Project(full);
        View = View.Identity;
        EndTransition();
        Render();
        if (into is { } src)
        {
            Transition(src, full);
        }
        else if (to.Length < from.Length && from.AsSpan().StartsWith(to) && Body(from) is { } child)
        {
            Transition(leaving, child);
        }
    }

    // Draw the new layout's `dst` where `src` is, then ease it home. One
    // region maps linearly, so moving every tile is moving the texture.
    void Transition(Rect src, Rect dst)
    {
        if (src.W <= 1 || src.H <= 1 || dst.W <= 1 || dst.H <= 1)
        {
            return;
        }
        var k = src.W / dst.W;
        // Longer pulls take a little longer, never enough to feel slow.
        var duration = 130 + 45 * Math.Clamp(Math.Log2(Math.Max(k, 1)), 0, 4);
        var generation = ++_transitions;
        _motion = (k, src.X - dst.X * k, src.Y - dst.Y * k);
        PlaceTextures();
        long started = 0;
        var frames = 0;
        _layer.AddTickCallback((_, clock) =>
        {
            if (generation != _transitions)
            {
                return false;
            }
            var now = clock.GetFrameTime();
            // The clock starts a frame late: the first frame uploads the new
            // texture, which on a slow laptop takes most of the transition.
            if (started <= 0)
            {
                started = started == 0 ? -1 : now;
                return true;
            }
            var t = (now - started) / 1000.0 / duration;
            frames++;
            if (t >= 1)
            {
                if (TestHooks.Trace)
                {
                    Console.Error.WriteLine($"trace: transition x{k:0.00} from {src.X:0},{src.Y:0}: {frames} frames in {(now - started) / 1000.0:0} ms (planned {duration:0})");
                }
                EndTransition();
                return false;
            }
            var eased = 1 - Math.Pow(1 - t, 3);
            _motion = (k + (1 - k) * eased, (src.X - dst.X * k) * (1 - eased), (src.Y - dst.Y * k) * (1 - eased));
            PlaceTextures();
            return true;
        });
    }

    void EndTransition()
    {
        if (_motion is not null)
        {
            _motion = null;
            _transitions++;
            PlaceTextures();
        }
    }

    // screen = (base - origin) * scale for the base; the detail was painted
    // at its own view, so it moves by the difference.
    void PlaceTextures()
    {
        var (s, ox, oy) = (View.Scale, View.OriginX, View.OriginY);
        var (k, x, y) = _motion ?? (s, -ox * s, -oy * s);
        Place(_picture, k, x, y);
        if (_detail.Visible)
        {
            var d = _detailView;
            Place(_detail, s / d.Scale, (d.OriginX - ox) * s, (d.OriginY - oy) * s);
        }

        void Place(Gtk.Picture picture, double k, double x, double y)
        {
            using var identity = Gsk.Transform.New();
            using var moved = identity!.Translate(new Graphene.Point { X = (float)x, Y = (float)y });
            using var transform = moved!.Scale((float)k, (float)k);
            _layer.SetChildTransform(picture, transform);
        }
    }

    // GTK keeps its own reference while it shows a texture. Dropping ours at
    // once frees the replaced full-window pixels (tens of MB at 2x) now, not
    // when the GC gets round to the finalizer, which a small heap rarely does.
    static void Show(Gtk.Picture picture, ref Gdk.MemoryTexture? held, Gdk.MemoryTexture next)
    {
        picture.SetPaintable(next);
        held?.Dispose();
        held = next;
    }

    // The tiles as `view` shows them, at device pixels.
    Gdk.MemoryTexture Texture(View view, long started)
    {
        var (width, height) = _painted;
        var scale = _window.GetSurface()?.GetScale() ?? 1.0;
        var (pw, ph) = ((int)Math.Ceiling(width * scale), (int)Math.Ceiling(height * scale));
        using var surface = new Cairo.ImageSurface(Cairo.Format.Argb32, pw, ph);
        using (var cr = new Cairo.Context(surface))
        {
            cr.Scale(scale, scale);
            Paint(cr, view, width, height);
            cr.Scale(view.Scale, view.Scale);
            cr.Translate(-view.OriginX, -view.OriginY);
            Painted?.Invoke(cr);
        }
        surface.Flush();
        // Cairo's ARGB32 is premultiplied BGRA in memory on little-endian.
        using var bytes = GLib.Bytes.New(surface.GetData());
        var texture = Gdk.MemoryTexture.New(pw, ph, Gdk.MemoryFormat.B8g8r8a8Premultiplied, bytes, (nuint)surface.Stride);
        Rendered?.Invoke(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        return texture;
    }

    // The card's natural size for its current text. Visible first: a hidden
    // widget measures as nothing. GTK counts a widget's margins in its
    // measured size, and the margins are where the card is: left in, a card
    // placed low measured tall, and was then placed somewhere else.
    void MeasureCard()
    {
        var widget = _card.Widget;
        widget.Visible = true;
        widget.Measure(Gtk.Orientation.Horizontal, -1, out int _, out var w, out int _, out int _);
        widget.Measure(Gtk.Orientation.Vertical, w, out int _, out var h, out int _, out int _);
        _cardSize = (w - widget.MarginStart - widget.MarginEnd, h - widget.MarginTop - widget.MarginBottom);
    }

    // The hover card: what the tile is, where, how big against its folder.
    void FillCard(Tile tile)
    {
        var hit = tile.Kind == TileKind.Node ? _state.Resolve(tile.Crumbs) : null;
        var parent = _state.Resolve(tile.Kind == TileKind.Node ? tile.Crumbs[..^1] : tile.Crumbs);
        var bytes = hit?.Bytes ?? tile.TailBytes;
        var share = parent is { Bytes: > 0 and var parentBytes } ? (double)bytes / parentBytes : 0;
        if (hit is not { } node)
        {
            _card.Show($"{tile.Count:N0} smaller items", null, HumanBytes(bytes), share, Category.Other, null, null, null);
            return;
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
        var facts = $"{HumanCount(node.Files)} files · {HumanCount(node.Dirs - (node.IsDir ? 1u : 0u))} dirs · {HumanBytes(direct)} direct";
        var path = _state.PathOf(tile.Crumbs);
        string?[] badges = [node.Name.StartsWith('.') ? "Hidden" : null, Marking.Badge(path)];
        var badge = string.Join(" · ", badges.OfType<string>());
        var hint = node.IsDir ? "space mark · enter open" : "space mark";
        _card.Show((node.IsDir ? "▸ " : "") + node.Name, _state.Tilde(path), HumanBytes(bytes), share,
            node.Category, facts, badge.Length > 0 ? badge : null, hint);
    }

    /// What the card says changed under a still pointer (a mark): say it again.
    public void RefreshCard()
    {
        if (Hovered is { } tile && _card.Widget.Visible)
        {
            FillCard(tile);
            MeasureCard();
        }
    }

    // Top, bottom, left, right: 2 px strips along the tile's edges, on
    // screen. Magnified, a tile runs past the treemap: its edges out there
    // are not drawn, and the rest stop at the treemap's.
    void Outline(Rect? rect) => PlaceEdges(_edges, rect);

    /// Four strips (top, bottom, left, right) around `rect`, a rectangle on
    /// screen (View.Project it first), clipped to the treemap: an edge
    /// outside it hides. The hover outline and the selection ring use it.
    public void PlaceEdges(IReadOnlyList<Gtk.Widget> edges, Rect? rect)
    {
        const int Thick = 2;
        var (width, height) = (Width, Height);
        var (x, y, right, bottom) = rect is { } r
            ? ((int)Math.Max(r.X, 0), (int)Math.Max(r.Y, 0), (int)Math.Min(r.Right, width), (int)Math.Min(r.Bottom, height))
            : (0, 0, 0, 0);
        var (w, h) = (Math.Max(Thick, right - x), Math.Max(Thick, bottom - y));
        var shown = right > x && bottom > y;
        Place(edges[0], x, y, w, Thick, shown && rect!.Value.Y >= 0);
        Place(edges[1], x, y + h - Thick, w, Thick, shown && rect!.Value.Bottom <= height);
        Place(edges[2], x, y, Thick, h, shown && rect!.Value.X >= 0);
        Place(edges[3], x + w - Thick, y, Thick, h, shown && rect!.Value.Right <= width);

        static void Place(Gtk.Widget edge, int x, int y, int w, int h, bool visible)
        {
            edge.Visible = visible;
            if (visible)
            {
                edge.MarginStart = x;
                edge.MarginTop = y;
                edge.SetSizeRequest(w, h);
            }
        }
    }

    // Every tile on screen in `view`: the inset ground showing in the gaps,
    // fill, hatch, the accent strip over a top-level directory, label.
    // Colours from Look (the theme).
    // Labels are chosen on screen, so magnifying makes room for more of them.
    void Paint(Cairo.Context cr, View view, int width, int height)
    {
        cr.SetSource(Look.Theme.Inset);
        cr.Paint();
        foreach (var tile in Tiles)
        {
            var r = view.Project(tile.Rect);
            if (r.Right <= 0 || r.Bottom <= 0 || r.X >= width || r.Y >= height)
            {
                continue;
            }
            var node = tile.Kind == TileKind.Node ? _state.Resolve(tile.Crumbs) : null;
            var category = node?.Category ?? Category.Other;
            // Age mode: the last write, where it is known; the kind otherwise.
            var fill = node is { Modified: > 0 and var modified } && _state.ColorMode == ColorMode.Age
                ? AgeFill(AgeBucket((_state.Now - modified) / 86_400), tile.Depth)
                : Fill(category, tile.Depth);
            // Only what matches the filter keeps its colour; a folder holding
            // matches steps back less, so the way to them stays readable.
            // Stepping back is toward the inset.
            Keep? keep = _state.Matches is { } matches ? matches.KeepOf(tile.Crumbs) : new Keep(true, 0, 0);
            var faded = keep is not { Whole: true };
            cr.SetSource(keep switch
            {
                null => fill.Mix(Look.Theme.Inset, 0.82),
                { Whole: false } => fill.Mix(Look.Theme.Inset, 0.55),
                _ => fill,
            });
            cr.Rectangle(r.X, r.Y, r.W, r.H);
            cr.Fill();

            // Hatched once, at the topmost reclaimable tile.
            if (!faded && node is { Reclaim: not Reclaim.None } && _state.Resolve(tile.Crumbs[..^1]) is { Reclaim: Reclaim.None })
            {
                Hatch(cr, r, spacing: 6, width: 1);
            }
            if (tile.Depth == 0)
            {
                cr.SetSource(Accent(category));
                cr.Rectangle(r.X, r.Y, r.W, Math.Min(2, r.H));
                cr.Fill();
            }

            var zoom = _state.Layout.Header / AppState.DefaultLayout.Header;
            var owned = view.Project(tile.Header ?? tile.Rect);
            if (node is not { } shown)
            {
                PaintLabel(cr, tile, r, owned, $"+{tile.Count} more", "", _state.Matches is null ? Look.Label(tile.Depth) : LabelDim, zoom);
            }
            else
            {
                PaintLabel(cr, tile, r, owned, shown.Name, _state.Metric == Metric.Files ? HumanCount(shown.Files) : Size.HumanBytesShort(shown.Bytes),
                    faded ? LabelDim : Look.Label(tile.Depth), zoom);
            }
        }
    }

    static Rgba LabelDim => Look.Label(1).Opacity(0.5);

    /// A tile's labels, in Cairo's text: the name in the region the tile owns
    /// (its band, or the tile), bold for a first-level band; the size dim, at
    /// the band's far end for the first level, after the name deeper, or under
    /// it in a closed tile tall enough. `owned` is where the band or tile is
    /// drawn, `clip` the whole tile, so a band's descenders (node_modules'
    /// underscore) are not cut off. Marking calls it for marked tiles.
    public static void PaintLabel(Cairo.Context cr, Tile tile, Rect clip, Rect owned, string name, string size, Rgba color, double zoom)
    {
        // 0.75 and 0.6875 rem of 16 px.
        var nameSize = 12 * zoom;
        var sizeSize = 11 * zoom;
        if (owned.W < 54 * zoom || owned.H < 15 * zoom)
        {
            return;
        }
        var lineHeight = nameSize * 1.35;
        var padding = nameSize * 0.42;
        var inset = nameSize * 0.25;
        var band = tile.Header is not null;
        cr.Save();
        cr.Rectangle(clip.X, clip.Y, clip.W, clip.H);
        cr.Clip();
        // A line sits centred in its line height.
        double Baseline(double top)
        {
            cr.FontExtents(out var font);
            return top + lineHeight / 2 + (font.Ascent - font.Descent) / 2;
        }
        cr.SelectFontFace("sans-serif", Cairo.FontSlant.Normal, band && tile.Depth == 0 ? Cairo.FontWeight.Bold : Cairo.FontWeight.Normal);
        cr.SetFontSize(nameSize);
        var (x, top) = (owned.X + padding, owned.Y + inset);
        var nameBaseline = Baseline(top);
        cr.TextExtents(name, out var nameExtents);
        var nameWidth = nameExtents.XAdvance;
        cr.SetSource(color);
        cr.MoveTo(x, nameBaseline);
        cr.ShowText(name);
        if (size.Length > 0)
        {
            cr.SelectFontFace("sans-serif", Cairo.FontSlant.Normal, Cairo.FontWeight.Normal);
            cr.SetFontSize(sizeSize);
            cr.TextExtents(size, out var sizeExtents);
            cr.SetSource(LabelDim);
            if (!band && owned.H >= lineHeight * 2 + inset)
            {
                cr.MoveTo(x, Baseline(top + lineHeight * 0.92));
                cr.ShowText(size);
            }
            else
            {
                // Mixed sizes share the name's baseline.
                var sizeX = band && tile.Depth == 0
                    ? owned.X + owned.W - sizeExtents.XAdvance - padding
                    : owned.W - padding * 2 - nameWidth < sizeSize * 3 ? double.NaN : x + nameWidth + nameSize * 0.67;
                if (sizeX > x + nameWidth + padding)
                {
                    cr.MoveTo(sizeX, nameBaseline);
                    cr.ShowText(size);
                }
            }
        }
        cr.Restore();
    }

    static string Describe(Tile? tile) => tile is null ? "nothing" : $"[{string.Join(',', tile.Crumbs)}]{(tile.Kind == TileKind.Others ? " (others)" : "")}";
}
