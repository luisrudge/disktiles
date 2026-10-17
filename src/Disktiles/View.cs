// The treemap's view transform:
// screen = (base - origin) * scale. Layout runs once in base space (the
// unzoomed treemap's pixels) and stays cached; zoom and pan change only this.
using Disktiles.Core;

namespace Disktiles;

readonly record struct View(double Scale, double OriginX, double OriginY)
{
    public static readonly View Identity = new(1, 0, 0);
    public const double MinScale = 1;
    /// Past this, zooming again goes into whatever is under the pointer.
    public const double MaxScale = 5;

    public bool IsIdentity => this == Identity;

    public Rect Project(Rect r) => new(
        (float)((r.X - OriginX) * Scale), (float)((r.Y - OriginY) * Scale), (float)(r.W * Scale), (float)(r.H * Scale));

    /// Viewport point to base-space point.
    public (double X, double Y) Unproject(double x, double y) => (x / Scale + OriginX, y / Scale + OriginY);

    /// Zoom by `factor` toward (x, y), no further than `ceiling`: the base
    /// point under (x, y) stays put, so the zoom points at the tile.
    public View ZoomedAt(double x, double y, double factor, double ceiling)
    {
        var scale = Math.Clamp(Scale * factor, MinScale, ceiling);
        var (bx, by) = Unproject(x, y);
        return new(scale, bx - x / scale, by - y / scale);
    }

    /// The scale at which `r` exactly fits a viewport of w × h.
    public static double FitScale(Rect r, double w, double h) =>
        r.W <= 0 || r.H <= 0 ? MinScale : Math.Min(Math.Max(w, 1) / r.W, Math.Max(h, 1) / r.H);

    /// Keep the viewport inside the layout: no empty margins, ever.
    public View Clamped(double w, double h) => this with
    {
        OriginX = Math.Clamp(OriginX, 0, Math.Max(0, w - w / Scale)),
        OriginY = Math.Clamp(OriginY, 0, Math.Max(0, h - h / Scale)),
    };
}
