// The side panel: sections in a fixed order, each its own class
// (SelectionSection, WorthSection, DiskSection, ...), added by whoever owns
// it. Everything scrolls but the disk, pinned below.

namespace Disktiles;

/// Where a section goes, top to bottom.
enum Slot { Selection, Worth, Marked, Notice, Disk }

sealed class Sidebar
{
    public readonly Gtk.Box Widget;
    readonly Dictionary<Slot, Gtk.Box> _slots = [];

    public Sidebar()
    {
        var panel = Gtk.Box.New(Gtk.Orientation.Vertical, 6);
        panel.AddCssClass("panel");
        var bottom = Gtk.Box.New(Gtk.Orientation.Vertical, 6);
        bottom.AddCssClass("panel-bottom");
        foreach (var slot in Enum.GetValues<Slot>())
        {
            // Hidden while empty, so an unused slot takes no spacing.
            var box = Gtk.Box.New(Gtk.Orientation.Vertical, 6);
            box.Visible = false;
            _slots[slot] = box;
            (slot == Slot.Disk ? bottom : panel).Append(box);
        }

        var scroll = Gtk.ScrolledWindow.New();
        scroll.SetPolicy(Gtk.PolicyType.Never, Gtk.PolicyType.Automatic);
        scroll.Vexpand = true;
        scroll.Child = panel;
        Widget = Gtk.Box.New(Gtk.Orientation.Vertical, 0);
        Widget.SetSizeRequest(340, -1);
        Widget.Append(scroll);
        Widget.Append(Gtk.Separator.New(Gtk.Orientation.Horizontal));
        Widget.Append(bottom);
    }

    /// A section's widget, in its slot, after what is there already.
    public void Add(Slot slot, Gtk.Widget section)
    {
        _slots[slot].Append(section);
        _slots[slot].Visible = true;
    }

    // Building blocks the sections share.

    public static (string Number, string Unit) Split(string size)
    {
        var space = size.IndexOf(' ');
        return space < 0 ? (size, "") : (size[..space], size[(space + 1)..]);
    }

    public static Gtk.Label Caps(string text) => Text(text.ToUpperInvariant(), "caps");

    public static Gtk.Label Text(string text, string css, Pango.EllipsizeMode ellipsize = Pango.EllipsizeMode.None)
    {
        var label = Gtk.Label.New(text);
        label.Xalign = 0;
        label.Ellipsize = ellipsize;
        if (ellipsize != Pango.EllipsizeMode.None)
        {
            // An ellipsized label still asks for its whole text's width,
            // and the divider follows the panel's ask: every long name would
            // widen the panel and re-lay the treemap. Ask for nothing; the
            // label fills what the panel's fixed width gives it.
            label.MaxWidthChars = 1;
            label.Hexpand = true;
        }
        label.AddCssClass(css);
        return label;
    }

    public static Gtk.Separator Separator()
    {
        var line = Gtk.Separator.New(Gtk.Orientation.Horizontal);
        line.MarginTop = line.MarginBottom = 10;
        return line;
    }

    /// The panel's styles. Colours are the theme's named @dt_* colours, and
    /// the per-category bars are the theme's too (Omarchy.cs), so a theme
    /// switch restyles them.
    public static string Css() => """
            .panel { padding: 18px; }
            .panel-bottom { padding: 12px 18px 18px 18px; }
            .caps { font-size: 0.78em; letter-spacing: 1px; opacity: 0.65; }
            .title { font-size: 1.45em; font-weight: bold; }
            .big { font-size: 2.6em; font-weight: bold; }
            .unit { font-size: 1.2em; opacity: 0.8; }
            .dim { opacity: 0.65; }
            .value { font-size: 1.15em; font-weight: bold; }
            .accent { color: @dt_highlight; font-weight: bold; }
            .worth { background: transparent; }
            .worth row { padding: 6px 4px; }
            .row-title { font-weight: bold; }
            progressbar > trough, progressbar > trough > progress { min-height: 6px; }
            progressbar.accent > trough > progress { background-color: @dt_highlight; }
            progressbar.disk > trough { background-color: alpha(@dt_foreground, 0.08); }
            progressbar.disk > trough > progress { background-color: alpha(@dt_foreground, 0.22); }
            """;
}
