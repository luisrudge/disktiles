// The hover card, built from widgets rather than painted on the canvas: GTK
// renders it as its own small nodes, so following the pointer redraws only
// the card, never the treemap under it.
using Disktiles.Core;

namespace Disktiles;

sealed class HoverCard
{
    public readonly Gtk.Box Widget;
    readonly Gtk.Label _name, _path, _size, _percent, _facts, _kind, _hint;
    readonly Gtk.ProgressBar _share;
    string? _shareClass;

    public HoverCard()
    {
        Widget = Gtk.Box.New(Gtk.Orientation.Vertical, 4);
        Widget.AddCssClass("card");
        Widget.Halign = Gtk.Align.Start;
        Widget.Valign = Gtk.Align.Start;
        // Never under the pointer as far as GTK is concerned: events go to
        // the treemap beneath.
        Widget.CanTarget = false;
        Widget.Visible = false;

        _name = Text("card-name", Pango.EllipsizeMode.Middle);
        _name.MaxWidthChars = 56;
        _path = Text("card-dim", Pango.EllipsizeMode.Middle);
        _path.MaxWidthChars = 64;
        _size = Text("card-size");
        _share = Gtk.ProgressBar.New();
        _share.SetSizeRequest(90, -1);
        _share.Valign = Gtk.Align.Center;
        _percent = Text("card-dim");
        var size = Gtk.Box.New(Gtk.Orientation.Horizontal, 10);
        size.Append(_size);
        size.Append(_share);
        size.Append(_percent);
        _facts = Text("card-text");
        _kind = Text("card-dim");
        _hint = Text("card-dim");

        Widget.Append(_name);
        Widget.Append(_path);
        Widget.Append(size);
        Widget.Append(_facts);
        Widget.Append(_kind);
        Widget.Append(_hint);
    }

    /// Set what the card says; nulls hide their line.
    public void Show(string name, string? path, string size, double share, Category category,
        string? facts, string? kind, string? hint)
    {
        _name.SetText(name);
        Line(_path, path);
        _size.SetText(size);
        _share.Fraction = share;
        var shareClass = $"cat-{category}";
        if (_shareClass != shareClass)
        {
            if (_shareClass is not null)
            {
                _share.RemoveCssClass(_shareClass);
            }
            _share.AddCssClass(shareClass);
            _shareClass = shareClass;
        }
        _percent.SetText($"{share * 100:0}%");
        Line(_facts, facts);
        Line(_kind, kind);
        Line(_hint, hint);
    }

    static void Line(Gtk.Label label, string? text)
    {
        label.Visible = text is not null;
        label.SetText(text ?? "");
    }

    static Gtk.Label Text(string css, Pango.EllipsizeMode ellipsize = Pango.EllipsizeMode.None)
    {
        var label = Gtk.Label.New("");
        label.Xalign = 0;
        label.Ellipsize = ellipsize;
        label.AddCssClass(css);
        return label;
    }

    /// The card, the hover outline and the frame-rate overlay.
    public static string Css() => """
        .card { background-color: alpha(@dt_background, 0.93); color: @dt_foreground;
                border: 1px solid alpha(@dt_foreground, 0.4); padding: 12px; }
        .card-name { font-weight: bold; font-size: 1.05em; }
        .card-size { font-weight: bold; font-size: 1.5em; }
        .card-text { font-size: 0.9em; }
        .card-dim { font-size: 0.9em; opacity: 0.6; }
        .outline { background-color: alpha(@dt_bright, 0.55); }
        .hud { background-color: rgba(0, 0, 0, 0.7); color: #8cf28c; font-family: monospace;
               font-weight: bold; padding: 4px 8px; border-radius: 5px; margin: 8px; }
        """;
}
