// Navigation and keys (feature 1): the keyboard selection
// and the pointer, going in and out, the panel, quitting. History, the "?"
// overlay, the file manager and interface zoom are in their own files.
//
// Space, X, Enter and the arrows act on the tile under the mouse if the
// mouse moved last, and on the keyboard selection after an arrow or Tab.
using Disktiles.Core;

namespace Disktiles;

static class Navigation
{
    enum Direction { Left, Right, Up, Down }

    /// Below this window width (52 rem) the panel gives way.
    const double PanelShownPx = 52 * 16;

    public static void Install(App app)
    {
        var state = app.State;
        app.AddCss("""
            .selected-ring { background-color: @dt_highlight; }
            """);

        app.Keys.Bind("Return", "Navigate", "Open that directory, at any depth", () =>
        {
            state.AdoptPointer();
            Descend(state);
        });
        app.Keys.Bind("BackSpace u", "Navigate", "Go up one directory", state.Up);
        // The selection goes first, then a level.
        app.Keys.Bind("Escape", "Navigate", "Go up one directory", () =>
        {
            if (state.Selected is null)
            {
                return state.Up();
            }
            state.Select(null);
            return true;
        });
        foreach (var (keys, direction) in new[]
        {
            ("Left h", Direction.Left), ("Right l", Direction.Right), ("Up k", Direction.Up), ("Down j", Direction.Down),
        })
        {
            app.Keys.Bind(keys, "Navigate", "Move between tiles at this level", () =>
            {
                state.AdoptPointer();
                Move(app, direction);
            });
        }
        app.Keys.Bind("Tab", "Navigate", "Next largest sibling", () => { state.AdoptPointer(); Cycle(state, 1); });
        app.Keys.Bind("shift+Tab", "Navigate", "Next largest sibling", () => { state.AdoptPointer(); Cycle(state, -1); });
        app.Keys.Bind("q ctrl+q ctrl+w", "App", "Quit", () => app.Window.GetApplication()?.Quit());

        // A scan lands with its largest entry selected (`select_largest`);
        // a re-sort (Files mode) keeps the selection it found again.
        state.TreeChanged += () =>
        {
            if (state.Selected is null && state.Tree is { Children.Length: > 0 })
            {
                state.Select([0]);
            }
        };

        app.Treemap.Clicked += (tile, button) =>
        {
            var crumbs = tile is { Kind: TileKind.Node } ? tile.Crumbs : null;
            switch (button)
            {
                // A double click opens what it hits; for a file, the folder holding it.
                case 1 when crumbs is not null && app.Treemap.Presses >= 2:
                    state.Select(crumbs);
                    Descend(state);
                    break;
                // Select; a click on the selection opens it, like a file manager.
                case 1 when crumbs is not null && state.Selected is { } selected
                        && selected.AsSpan().SequenceEqual(crumbs) && state.Resolve(crumbs) is { IsDir: true }:
                    Descend(state);
                    break;
                case 1:
                    state.Select(crumbs);
                    break;
                // What `o` does, for the tile under the pointer.
                case 3 when crumbs is not null:
                    state.Select(crumbs);
                    FileManager.Reveal(app, crumbs);
                    break;
            }
        };

        // Only a pointer that really moved takes over from the keyboard:
        // GTK repeats the last position after every relayout.
        var pointer = (X: double.NaN, Y: double.NaN);
        var motion = Gtk.EventControllerMotion.New();
        motion.OnMotion += (_, e) =>
        {
            if ((e.X, e.Y) != pointer)
            {
                pointer = (e.X, e.Y);
                state.PointerActive = true;
            }
        };
        motion.OnLeave += (_, _) => state.PointerActive = false;
        app.Treemap.Widget.AddController(motion);

        // "Worth a look": open the folder holding it, with it selected.
        app.Worth.Jump += at =>
        {
            state.GoTo(at[..^1]);
            state.Select(at);
        };

        InstallRing(app);
        InstallPanel(app);
        History.Install(app);
        FileManager.Install(app);
        InterfaceZoom.Install(app);
        KeysHelp.Install(app);
    }

    /// What is selected below this folder, however deep (a file opens the
    /// directory holding it); else the largest entry.
    static void Descend(AppState state)
    {
        var target = state.Selected is { } selected && selected.Length > state.Crumbs.Length
            ? state.Resolve(selected) is { IsDir: true } ? selected : selected[..^1]
            : state.Current is { Children.Length: > 0 } ? [.. state.Crumbs, 0] : null;
        if (target is not null && target.Length > state.Crumbs.Length
            && target.AsSpan().StartsWith(state.Crumbs) && state.Resolve(target) is { IsDir: true, Children.Length: > 0 })
        {
            state.GoTo(target);
        }
    }

    /// The nearest tile at the same depth that way, falling back to the
    /// parent at the left or top edge.
    static void Move(App app, Direction direction)
    {
        var state = app.State;
        state.PointerActive = false;
        var tiles = app.Treemap.Tiles.Where(t => t.Kind == TileKind.Node).ToList();
        var current = state.Selected is { } s ? tiles.Find(t => t.Crumbs.AsSpan().SequenceEqual(s)) : null;
        if (current is null)
        {
            // The selection is not drawn (the folder itself, after Enter):
            // start from the largest tile.
            if (tiles.Count > 0)
            {
                state.Select(tiles[0].Crumbs);
            }
            return;
        }
        // Same-depth neighbours only: stepping into a child by arrow key
        // would make the depth of the selection impossible to predict.
        var from = current.Rect;
        Tile? best = null;
        var bestScore = float.MaxValue;
        foreach (var tile in tiles)
        {
            if (tile.Crumbs.Length != current.Crumbs.Length || tile == current || Gap(direction, from, tile.Rect) is not { } gap)
            {
                continue;
            }
            var score = Offset(direction, from, tile.Rect) * 2.5f + gap;
            if (score < bestScore)
            {
                (best, bestScore) = (tile, score);
            }
        }
        if (best is not null)
        {
            state.Select(best.Crumbs);
        }
        else if (direction is Direction.Left or Direction.Up && state.Crumbs.Length > 0)
        {
            state.Select(state.Crumbs[..^1]);
        }
    }

    // Distance from `from` to `rect` along the axis, if `rect` lies that way.
    static float? Gap(Direction direction, Rect from, Rect rect)
    {
        var gap = direction switch
        {
            Direction.Right => rect.X - from.Right,
            Direction.Left => from.X - rect.Right,
            Direction.Down => rect.Y - from.Bottom,
            _ => from.Y - rect.Bottom,
        };
        return gap >= -0.5f ? Math.Max(gap, 0) : null;
    }

    // How far off the axis `rect` sits, so the nearest tile that way wins
    // rather than any tile in that half-plane.
    static float Offset(Direction direction, Rect from, Rect rect)
    {
        static float Overlap(float a0, float a1, float b0, float b1) => Math.Max(Math.Min(a1, b1) - Math.Max(a0, b0), 0);
        return direction is Direction.Left or Direction.Right
            ? Math.Max(Math.Min(from.H, rect.H) - Overlap(from.Y, from.Bottom, rect.Y, rect.Bottom), 0)
            : Math.Max(Math.Min(from.W, rect.W) - Overlap(from.X, from.Right, rect.X, rect.Right), 0);
    }

    /// The next entry by rank, which is the next largest by the metric.
    /// Scanning a directory for space is this walk.
    static void Cycle(AppState state, int step)
    {
        state.PointerActive = false;
        var parent = state.Selected is { } s && s.Length > state.Crumbs.Length ? s[..^1] : state.Crumbs;
        if (state.Resolve(parent) is not { Children.Length: > 0 and var count })
        {
            return;
        }
        var at = state.Selected is { } selected && selected.Length == parent.Length + 1
            && selected.AsSpan().StartsWith(parent) ? selected[^1] : -1;
        var next = at < 0 ? (step >= 0 ? 0 : count - 1) : ((at + step) % count + count) % count;
        state.Select([.. parent, next]);
    }

    // The keyboard selection's amber ring: four strips above the texture,
    // like the hover outline, so moving it never repaints the treemap.
    static void InstallRing(App app)
    {
        var edges = new Gtk.Box[4];
        for (var i = 0; i < edges.Length; i++)
        {
            edges[i] = Gtk.Box.New(Gtk.Orientation.Vertical, 0);
            edges[i].AddCssClass("selected-ring");
            edges[i].Halign = edges[i].Valign = Gtk.Align.Start;
            edges[i].CanTarget = false;
            edges[i].Visible = false;
            app.Treemap.AddOverlay(edges[i]);
            // Above the hover outline, below the hover card.
            for (var child = app.Treemap.Widget.GetFirstChild(); child is not null; child = child.GetNextSibling())
            {
                if (child.HasCssClass("card"))
                {
                    edges[i].InsertBefore(app.Treemap.Widget, child);
                    break;
                }
            }
        }
        // On screen as the hover outline is: projected through the zoom and
        // clipped, and moved with every zoom or pan.
        void Place()
        {
            var tile = app.State.Selected is { } at
                ? app.Treemap.Tiles.Find(t => t.Kind == TileKind.Node && t.Crumbs.AsSpan().SequenceEqual(at))
                : null;
            app.Treemap.PlaceEdges(edges, tile is null ? null : app.Treemap.View.Project(tile.Rect));
        }
        app.State.SelectionChanged += Place;
        app.Treemap.Rendered += _ => Place();
        app.Treemap.ViewChanged += Place;
    }

    // The panel: `p` shows or hides it, a narrow window hides it (the
    // mosaic would be too narrow to read), and a double click on the
    // divider puts it back at its own width.
    static void InstallPanel(App app)
    {
        var panel = app.Sidebar.Widget;
        var split = (Gtk.Paned)panel.GetParent()!;
        var shown = true;
        void Fit()
        {
            var wide = split.GetWidth() >= PanelShownPx * InterfaceZoom.Factor;
            if (panel.Visible != (shown && wide))
            {
                panel.Visible = shown && wide;
            }
        }
        app.Keys.Bind("p", "View", "Show or hide the selection line", () =>
        {
            shown = !shown;
            Fit();
        });
        // Resizes arrive mid-allocation; showing a widget there must wait.
        app.Treemap.Rendered += _ => GLib.Functions.IdleAdd(GLib.Constants.PRIORITY_DEFAULT_IDLE, () => { Fit(); return false; });
        InterfaceZoom.Changed += Fit;

        var click = Gtk.GestureClick.New();
        click.PropagationPhase = Gtk.PropagationPhase.Capture;
        click.OnPressed += (_, e) =>
        {
            // The handle sits between the two children; GTK grabs a few
            // pixels either side of it.
            if (e.NPress == 2 && panel.Visible && Math.Abs(e.X - (split.GetWidth() - panel.GetWidth())) <= 6)
            {
                ResetPanel(app);
            }
        };
        split.AddController(click);
    }

    /// Back to the panel's own width: the split lays out from its children again.
    public static void ResetPanel(App app) => ((Gtk.Paned)app.Sidebar.Widget.GetParent()!).PositionSet = false;
}
