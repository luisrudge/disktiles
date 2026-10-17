// Zoom, pan and depth (feature 2). The wheel magnifies toward the pointer
// until the directory under it fills the view, and the next notch goes into
// it; the other way comes back out, and at scale 1 goes up. Shift-scroll
// pans. + and - magnify without going in, 0 resets. [ and ], or the header's
// Depth control, draw fewer or more levels. The view itself, and why zooming
// never repaints: View.cs and TreemapView.cs.

namespace Disktiles;

static class Zoom
{
    const double Notch = 1.15;
    const double KeyStep = 1.25;
    // Screen pixels a pan notch moves.
    const double PanStep = 40;
    const int MinDepth = 1, MaxDepth = 6;

    public static void Install(App app)
    {
        var (state, treemap) = (app.State, app.Treemap);
        treemap.Scrolled += (x, y, dx, dy, modifiers) =>
        {
            if (state.Tree is null)
            {
                return;
            }
            if (modifiers.HasFlag(Gdk.ModifierType.ShiftMask))
            {
                // Pan, for looking around a magnified view. Some systems turn
                // shift-wheel into a sideways scroll, so both axes pan.
                var view = treemap.View;
                treemap.SetView(view with
                {
                    OriginX = view.OriginX + dx * PanStep / view.Scale,
                    OriginY = view.OriginY + dy * PanStep / view.Scale,
                });
                return;
            }
            if (Math.Abs(dy) < 1e-6)
            {
                return;
            }
            // Up magnifies. A wheel notch is one step, a touchpad's smaller
            // moves a fraction of one.
            ZoomAt(app, x, y, Math.Pow(Notch, -Math.Clamp(dy, -1, 1)), descend: true);
        };

        double CentreX() => treemap.Width / 2.0;
        double CentreY() => treemap.Height / 2.0;
        app.Keys.Bind("plus equal KP_Add", "View", "magnify", () => ZoomAt(app, CentreX(), CentreY(), KeyStep, descend: false));
        app.Keys.Bind("minus KP_Subtract", "View", "shrink", () => ZoomAt(app, CentreX(), CentreY(), 1 / KeyStep, descend: false));
        app.Keys.Bind("0 KP_0", "View", "reset the view", () => treemap.SetView(View.Identity));
        app.Keys.Bind("bracketleft", "View", "fewer levels", () => StepDepth(state, -1));
        app.Keys.Bind("bracketright", "View", "more levels", () => StepDepth(state, 1));

        app.Header.End.Append(DepthControl(state));
        // How far in, while magnified, in the key bar.
        var scale = Gtk.Label.New("");
        scale.AddCssClass("hint");
        scale.Visible = false;
        app.Footer.End.Append(scale);
        treemap.ViewChanged += () =>
        {
            var s = treemap.View.Scale;
            scale.Visible = Math.Abs(s - 1) > 0.01;
            scale.SetText($"{s:0.0}×");
        };
        app.AddCss(Css);
    }

    /// Zoom by `factor` toward (x, y). With `descend`, the wheel stops
    /// magnifying where the directory under the pointer exactly fits the
    /// view, and the next step goes inside it: its contents are already as
    /// large as they will be, so going in makes them grow, never shrink.
    /// Zooming out at scale 1 goes up.
    public static void ZoomAt(App app, double x, double y, double factor, bool descend)
    {
        var (state, treemap) = (app.State, app.Treemap);
        var view = treemap.View;
        if (factor < 1 && view.Scale <= View.MinScale + 1e-6)
        {
            if (descend)
            {
                state.Up();
            }
            return;
        }
        // One directory decides how far the wheel magnifies and where it then goes.
        var target = descend ? Target(app, x, y) : null;
        var body = target is null ? null : treemap.Body(target);
        var ceiling = Math.Clamp(body is { } b ? View.FitScale(b, treemap.Width, treemap.Height) : View.MaxScale,
            View.MinScale, View.MaxScale);
        if (factor > 1 && view.Scale >= ceiling - 1e-6 && target is not null)
        {
            state.GoTo(target);
            state.Select(target);
            return;
        }
        treemap.SetView(view.ZoomedAt(x, y, factor, ceiling));
    }

    // The deepest drawn directory under (x, y) with something inside: what
    // zooming there is zooming into.
    static int[]? Target(App app, double x, double y)
    {
        if (app.Treemap.Hit(x, y) is not { } tile)
        {
            return null;
        }
        for (var length = tile.Crumbs.Length; length > app.State.Crumbs.Length; length--)
        {
            var crumbs = tile.Crumbs[..length];
            if (app.State.Resolve(crumbs) is { IsDir: true, Children.Length: > 0 } && app.Treemap.Body(crumbs) is not null)
            {
                return crumbs;
            }
        }
        return null;
    }

    // The other meaning of zoom in a treemap: seeing further in without
    // changing what is on screen.
    static void StepDepth(AppState state, int step)
    {
        var depth = (uint)Math.Clamp((int)state.Layout.MaxDepth + step, MinDepth, MaxDepth);
        if (depth != state.Layout.MaxDepth)
        {
            state.SetLayout(state.Layout with { MaxDepth = depth });
        }
    }

    // The depth control: "Depth N", then − and +, framed together.
    static Gtk.Box DepthControl(AppState state)
    {
        var label = Gtk.Label.New("");
        var less = Stepper("−", -1);
        var more = Stepper("+", 1);
        var box = Gtk.Box.New(Gtk.Orientation.Horizontal, 0);
        box.AddCssClass("depth");
        box.TooltipText = "Levels drawn at once · [ and ]";
        box.Valign = Gtk.Align.Center;
        box.Append(label);
        box.Append(less);
        box.Append(more);
        state.LayoutChanged += Update;
        Update();
        return box;

        void Update()
        {
            var depth = state.Layout.MaxDepth;
            label.SetText($"Depth {depth}");
            less.Sensitive = depth > MinDepth;
            more.Sensitive = depth < MaxDepth;
        }

        Gtk.Button Stepper(string text, int step)
        {
            var button = Gtk.Button.NewWithLabel(text);
            // The keys stay with the treemap.
            button.FocusOnClick = false;
            button.Focusable = false;
            button.OnClicked += (_, _) => StepDepth(state, step);
            return button;
        }
    }

    const string Css = """
        .depth { border: 1px solid @dt_border; margin: 4px 10px 4px 0; }
        .depth label { padding: 0 8px; }
        .depth button { border-radius: 0; min-height: 0; min-width: 0; padding: 2px 8px; }
        """;
}
