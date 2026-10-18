// Zoom, pan and depth (feature 2). The wheel magnifies toward the pointer
// until the directory under it fills the view, and the next notch goes into
// it; the other way comes back out, and at scale 1 goes up. Shift-scroll
// pans. + and - magnify without going in, 0 resets. [ and ], or the header's
// Depth control, draw fewer or more levels.
namespace Disktiles;

sealed partial class App
{
    const double Notch = 1.15;
    const double KeyStep = 1.25;
    // Screen pixels a pan notch moves.
    const double PanStep = 40;
    public const int MinDepth = 1, MaxDepth = 6;

    void InitZoom()
    {
        Keys.Bind("plus equal KP_Add", "View", "magnify", () => ZoomAt(Width / 2, Height / 2, KeyStep, descend: false));
        Keys.Bind("minus KP_Subtract", "View", "shrink", () => ZoomAt(Width / 2, Height / 2, 1 / KeyStep, descend: false));
        Keys.Bind("0 KP_0", "View", "reset the view", () => SetView(View.Identity));
        Keys.Bind("bracketleft", "View", "fewer levels", () => StepDepth(-1));
        Keys.Bind("bracketright", "View", "more levels", () => StepDepth(1));
        State.LayoutChanged += () => Touch(Part.Header);
    }

    /// A scroll step at (x, y): dx, dy in wheel notches (down is positive);
    /// with shift, a pan.
    public void Scroll(double x, double y, double dx, double dy, bool shift)
    {
        if (State.Tree is null || State.Screen != Screen.Explore)
        {
            return;
        }
        if (shift)
        {
            // Pan, for looking around a magnified view. Some systems turn
            // shift-wheel into a sideways scroll, so both axes pan.
            SetView(View with
            {
                OriginX = View.OriginX + dx * PanStep / View.Scale,
                OriginY = View.OriginY + dy * PanStep / View.Scale,
            });
            return;
        }
        if (Math.Abs(dy) < 1e-6)
        {
            return;
        }
        // Up magnifies. A wheel notch is one step, a touchpad's smaller
        // moves a fraction of one.
        ZoomAt(x, y, Math.Pow(Notch, -Math.Clamp(dy, -1, 1)), descend: true);
    }

    /// Zoom by `factor` toward (x, y). With `descend`, the wheel stops
    /// magnifying where the directory under the pointer exactly fits the
    /// view, and the next step goes inside it: its contents are already as
    /// large as they will be, so going in makes them grow, never shrink.
    /// Zooming out at scale 1 goes up.
    public void ZoomAt(double x, double y, double factor, bool descend)
    {
        var view = View;
        if (factor < 1 && view.Scale <= View.MinScale + 1e-6)
        {
            if (descend)
            {
                State.Up();
            }
            return;
        }
        // One directory decides how far the wheel magnifies and where it then goes.
        var target = descend ? ZoomTarget(x, y) : null;
        var body = target is null ? null : Body(target);
        var ceiling = Math.Clamp(body is { } b ? View.FitScale(b, Width, Height) : View.MaxScale,
            View.MinScale, View.MaxScale);
        if (factor > 1 && view.Scale >= ceiling - 1e-6 && target is not null)
        {
            State.GoTo(target);
            State.Select(target);
            return;
        }
        SetView(view.ZoomedAt(x, y, factor, ceiling));
    }

    // The deepest drawn directory under (x, y) with something inside: what
    // zooming there is zooming into.
    int[]? ZoomTarget(double x, double y)
    {
        if (Hit(x, y) is not { } tile)
        {
            return null;
        }
        for (var length = tile.Crumbs.Length; length > State.Crumbs.Length; length--)
        {
            var crumbs = tile.Crumbs[..length];
            if (State.Resolve(crumbs) is { IsDir: true, Children.Length: > 0 } && Body(crumbs) is not null)
            {
                return crumbs;
            }
        }
        return null;
    }

    // The other meaning of zoom in a treemap: seeing further in without
    // changing what is on screen.
    public void StepDepth(int step)
    {
        var depth = (uint)Math.Clamp((int)State.Layout.MaxDepth + step, MinDepth, MaxDepth);
        if (depth != State.Layout.MaxDepth)
        {
            State.SetLayout(State.Layout with { MaxDepth = depth });
        }
    }
}
