// Colours, labels, number formatting and the hatch, shared by the treemap,
// the legend and the panel: the palette's one entry point. The colours are
// derived from the current theme (Theme.cs), so they follow the Omarchy
// theme.
using Disktiles.Core;

namespace Disktiles;

static class Look
{
    public static readonly Category[] Legend =
    [
        Category.Code, Category.AgentScratch, Category.Toolchain, Category.Synced,
        Category.Git, Category.Media, Category.Documents, Category.Cache,
    ];

    /// The theme everything is drawn from; Omarchy.cs follows the desktop's.
    public static OmarchyTheme Theme { get; set; } = OmarchyTheme.TokyoNight;

    // Every hue at the same muted saturation and lightness, so no block
    // stands out by accident; lightness, saturation and the surface each fill
    // is pulled toward come from the theme, light or dark.

    /// The hue a category is drawn in, and how much colour it carries.
    static (double Hue, double Chroma) Hue(Category category) => category switch
    {
        Category.Code => (0.605, 1.0),
        Category.AgentScratch => (0.065, 1.0),
        Category.Toolchain => (0.415, 1.0),
        Category.Synced => (0.535, 1.0),
        Category.Git => (0.955, 1.0),
        Category.Media => (0.745, 1.0),
        Category.Cache => (0.125, 0.95),
        Category.Documents => (0.6, 0.18),
        _ => (0.6, 0.08),
    };

    /// A tile of `category`, `depth` levels into the view: deeper tiles lift
    /// away from the background, so nesting reads without borders.
    public static Rgba Fill(Category category, uint depth)
    {
        var (h, chroma) = Hue(category);
        var step = Math.Min(depth, 4u);
        var (s, l) = Theme.Dark ? (0.26 * chroma, 0.215 + step * 0.028) : (0.30 * chroma, 0.84 - step * 0.03);
        return Rgba.Hsl(h, s, l).Mix(Theme.Inset, 0.12);
    }

    /// The saturated version of a category's hue: the strip over a top-level
    /// directory, the legend swatch, the share bars.
    public static Rgba Accent(Category category)
    {
        var (h, chroma) = Hue(category);
        return Theme.Dark ? Rgba.Hsl(h, 0.42 * chroma, 0.52) : Rgba.Hsl(h, 0.45 * chroma, 0.46);
    }

    /// The age ramp, newest first.
    public static readonly (long Days, string Label)[] AgeBuckets =
        [(7, "This week"), (30, "This month"), (182, "Six months"), (365, "This year"), (long.MaxValue, "Older")];

    public static int AgeBucket(long days)
    {
        var i = Array.FindIndex(AgeBuckets, b => days <= b.Days);
        return i < 0 ? AgeBuckets.Length - 1 : i;
    }

    /// Age mode: recent writes carry the theme accent, and colour drains out
    /// of a tile as it goes untouched.
    public static Rgba AgeFill(int bucket, uint depth)
    {
        var fade = Math.Min(bucket, AgeBuckets.Length - 1) / 4.0;
        var step = Math.Min(depth, 4u);
        var (s, l) = Theme.Dark
            ? ((1 - fade) * 0.34 + 0.03, step * 0.028 + 0.29 - fade * 0.09)
            : ((1 - fade) * 0.36 + 0.04, step * -0.03 + 0.74 + fade * 0.1);
        return Rgba.Hsl(Theme.Accent.Hue, s, l).Mix(Theme.Inset, 0.1);
    }

    public static Rgba AgeAccent(int bucket)
    {
        var fade = Math.Min(bucket, AgeBuckets.Length - 1) / 4.0;
        return Rgba.Hsl(Theme.Accent.Hue, (1 - fade) * 0.45 + 0.04, Theme.Dark ? 0.55 - fade * 0.25 : 0.45 + fade * 0.25);
    }

    /// The one strong colour, the theme's warning amber: the selection, the
    /// main action, what can be had back, and nothing else.
    public static Rgba Highlight => Theme.Warning;

    /// Text on a filled highlight.
    public static Rgba OnHighlight => Theme.Dark ? Theme.Background : Theme.Bright;

    /// Marked for removal, and failures.
    public static Rgba Danger => Theme.Danger;

    /// The hatch over reclaimable space: quiet enough to leave the hue readable.
    public static Rgba HatchColour => Theme.Dark ? Theme.Bright.Opacity(0.16) : Theme.Foreground.Opacity(0.18);

    /// A tile's name on top of its fill.
    public static Rgba Label(uint depth)
    {
        var ink = Theme.Dark ? Theme.Bright : Theme.Foreground;
        return depth == 0 ? ink : ink.Opacity(0.88);
    }

    /// A category's accent as a tuple, for code that predates Rgba.
    public static (double, double, double) Colour(Category category)
    {
        var c = Accent(category);
        return (c.R, c.G, c.B);
    }

    public static void SetSource(this Cairo.Context cr, Rgba c) => cr.SetSourceRgba(c.R, c.G, c.B, c.A);

    // What each category and reclaim kind is called.
    public static string CategoryLabel(Category category) => category switch
    {
        Category.Code => "Code",
        Category.AgentScratch => "Agent scratch",
        Category.Toolchain => "Toolchains",
        Category.Synced => "Synced",
        Category.Git => "Git",
        Category.Media => "Media",
        Category.Documents => "Documents",
        Category.Cache => "Cache",
        _ => "Other",
    };

    public static string ReclaimLabel(Reclaim reclaim) => reclaim switch
    {
        Reclaim.Regenerable => "regenerable",
        Reclaim.SyncHistory => "sync history",
        Reclaim.PackageStore => "package store",
        Reclaim.BuildOutput => "build output",
        Reclaim.Reinstallable => "reinstallable",
        Reclaim.SandboxLayers => "sandbox layers",
        Reclaim.Snapshots => "snapshots",
        Reclaim.Trash => "trash",
        Reclaim.Temporary => "temporary",
        _ => "",
    };

    /// 9999, then 10.0k, 1.0M; each bound sits where one decimal would round
    /// up to 1000.0.
    public static string HumanCount(ulong count) => count switch
    {
        < 10_000 => count.ToString(),
        < 999_950 => $"{count / 1e3:0.0}k",
        < 999_950_000 => $"{count / 1e6:0.0}M",
        _ => $"{count / 1e9:0.0}G",
    };

    /// The scan's progress counters are signed.
    public static string HumanCount(long count) => HumanCount((ulong)Math.Max(count, 0));

    /// `1.4 GiB`, `523 MiB`, `12 B`.
    public static string HumanBytes(ulong bytes) => Size.HumanBytes(bytes);

    /// How long ago, in the unit a person would use.
    public static string Ago(long now, long then)
    {
        if (then <= 0)
        {
            return "unknown";
        }
        var seconds = Math.Max(now - then, 0);
        static string Plural(long count, string unit) => count == 1 ? $"1 {unit} ago" : $"{count} {unit}s ago";
        return seconds switch
        {
            < 60 => "just now",
            < 3_600 => Plural(seconds / 60, "minute"),
            < 86_400 => Plural(seconds / 3_600, "hour"),
            < 5_184_000 => Plural(seconds / 86_400, "day"),
            < 63_072_000 => Plural(seconds / 2_592_000, "month"),
            _ => Plural(seconds / 31_536_000, "year"),
        };
    }

    /// A category's accent as CSS, for widgets styled per category.
    public static string Css(Category category) => Accent(category).Css;

    /// Diagonal stripes over reclaimable space.
    public static void Hatch(Cairo.Context cr, Rect r, double spacing, double width, Rgba? colour = null)
    {
        cr.Save();
        cr.Rectangle(r.X, r.Y, r.W, r.H);
        cr.Clip();
        cr.SetSource(colour ?? HatchColour);
        cr.LineWidth = width;
        for (double x = r.X - r.H; x < r.Right; x += spacing)
        {
            cr.MoveTo(x, r.Bottom);
            cr.LineTo(x + r.H, r.Y);
        }
        cr.Stroke();
        cr.Restore();
    }
}
