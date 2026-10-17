// Below the treemap: key hints that a narrow window clips from the end,
// then the right-aligned extras ("? all keys") and the scan's status line.
namespace Disktiles;

sealed class Footer
{
    public readonly Gtk.Box Widget;
    /// Key hints, appended in order, most useful first.
    public readonly Gtk.Box Hints;
    /// After the hints, before the status: "? all keys".
    public readonly Gtk.Box End;
    readonly Gtk.Label _status;

    public Footer()
    {
        Hints = Gtk.Box.New(Gtk.Orientation.Horizontal, 16);
        // A box never shrinks below its children; a scroller with an
        // external policy does, and clips the hints instead of widening
        // the window.
        var lane = Gtk.ScrolledWindow.New();
        lane.SetPolicy(Gtk.PolicyType.External, Gtk.PolicyType.Never);
        lane.Hexpand = true;
        lane.Child = Hints;
        End = Gtk.Box.New(Gtk.Orientation.Horizontal, 16);
        _status = Gtk.Label.New("");
        _status.Xalign = 1;
        _status.AddCssClass("footer-status");
        Widget = Gtk.Box.New(Gtk.Orientation.Horizontal, 16);
        Widget.AddCssClass("footer");
        Widget.Append(lane);
        Widget.Append(End);
        Widget.Append(_status);
    }

    public void SetStatus(string text) => _status.SetText(text);

    public void AddHint(Gtk.Widget hint) => Hints.Append(hint);

    /// A hint: a keycap, then what it does.
    public static Gtk.Box Hint(string keys, string label)
    {
        var cap = Gtk.Label.New(keys);
        cap.AddCssClass("keycap");
        var text = Gtk.Label.New(label);
        text.AddCssClass("hint");
        var hint = Gtk.Box.New(Gtk.Orientation.Horizontal, 4);
        hint.Append(cap);
        hint.Append(text);
        return hint;
    }
}
