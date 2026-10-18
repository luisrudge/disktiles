// The treemap as a Drawing for the host's DrawView: the inset ground, each
// tile's fill, the hatch over reclaimable space, the accent strip over a
// top-level directory, the labels, and the marked rings last. The same rules
// the GTK app (and the QML Canvas before this) painted by: labels are chosen
// on screen, so a magnified view makes room for more of them.
using Disktiles.Core;
using Omasharp;

namespace Disktiles;

sealed partial class App
{
    /// The view the last drawing was made for; DrawView moves it to the current one.
    public View DrawnView { get; private set; } = View.Identity;

    /// The picture for the current view.
    public Drawing BuildDrawing()
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var models = TileModels;
        var view = View;
        var d = new Drawing(view.Scale, view.OriginX, view.OriginY);
        var (width, height) = (Width, Height);
        var z = UiZoom;
        var dim = ToColor(Look.LabelDim);
        var hatch = ToColor(Look.HatchColour);
        d.Rect(0, 0, width, height, ToColor(Look.Theme.Inset));
        var rings = new List<Rect>();
        var labels = 0;
        foreach (var t in models)
        {
            var r = view.Project(new Rect((float)t.X, (float)t.Y, (float)t.W, (float)t.H));
            // Every tile is in the picture, so a pan or a zoom out shows them before the sharp one
            // arrives; labels only where they are on screen.
            d.Rect(r.X, r.Y, r.W, r.H, ToColor(t.Fill));
            if (t.Hatch)
            {
                d.Hatch(r.X, r.Y, r.W, r.H, 6, hatch);
            }
            if (t.Accent is { } accent)
            {
                d.Rect(r.X, r.Y, r.W, Math.Min(2, r.H), ToColor(accent));
            }
            if (r.Right > 0 && r.Bottom > 0 && r.X < width && r.Y < height)
            {
                labels += Label(d, t, r, t.Band > 0 ? t.Band * view.Scale : r.H, z, dim) ? 1 : 0;
            }
            if (t.Mark == 1)
            {
                rings.Add(r);
            }
        }
        // The marked ring goes last, so no fill covers it.
        var danger = ToColor(Look.Danger);
        foreach (var r in rings)
        {
            d.Frame(r.X, r.Y, r.W, r.H, 2, danger);
        }
        DrawnView = view;
        Perf.Mark("paint", Perf.F("ms", Perf.Ms(started)) + "," + Perf.F("tiles", models.Count) + "," + Perf.F("labels", labels)
            + "," + Perf.F("ops", d.Count));
        return d;
    }

    /// A tile's labels: the name in the region the tile owns (its band, or
    /// the tile), bold for a first-level band; the size dim, at the band's far
    /// end for the first level, after the name deeper, or under it in a tile
    /// tall enough. Cut at the tile's right edge. `owned` is the band's (or the
    /// tile's) height on screen.
    static bool Label(Drawing d, TileModel t, Rect r, double owned, double z, Omasharp.Color dim)
    {
        var nameSize = 12 * z;
        var sizeSize = 11 * z;
        if (r.W < 54 * z || owned < 15 * z)
        {
            return false;
        }
        var lineHeight = nameSize * 1.35;
        var padding = nameSize * 0.42;
        var inset = nameSize * 0.25;
        var band = t.Band > 0;
        var x = r.X + padding;
        var top = r.Y + inset;
        var mid = top + lineHeight / 2;
        var right = r.X + r.W;
        d.Text(x, mid, t.Name, nameSize, ToColor(t.Ink), t.Bold, right - x);
        if (t.Size.Length == 0)
        {
            return true;
        }
        var nameWidth = t.Name.Length * Drawing.MonoAdvance * nameSize;
        var sizeWidth = t.Size.Length * Drawing.MonoAdvance * sizeSize;
        if (!band && owned >= lineHeight * 2 + inset)
        {
            d.Text(x, top + lineHeight * 0.92 + lineHeight / 2, t.Size, sizeSize, dim, false, right - x);
            return true;
        }
        var sx = band && t.Depth == 0 ? r.X + r.W - sizeWidth - padding
            : r.W - padding * 2 - nameWidth < sizeSize * 3 ? double.NaN : x + nameWidth + nameSize * 0.67;
        if (sx > x + nameWidth + padding)
        {
            d.Text(sx, mid, t.Size, sizeSize, dim, false, right - sx);
        }
        return true;
    }

    static Omasharp.Color ToColor(Rgba c)
    {
        static uint Byte(double v) => (uint)Math.Round(Math.Clamp(v, 0, 1) * 255);
        return new Omasharp.Color((Byte(c.A) << 24) | (Byte(c.R) << 16) | (Byte(c.G) << 8) | Byte(c.B));
    }
}
