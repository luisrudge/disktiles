// `?`: every key, grouped, with what it does. While open it takes the
// keyboard; ?, /, q and Escape close it.
namespace Disktiles;

static class KeysHelp
{
    // The modifier each platform's users reach for; both work everywhere.
    static readonly string Command = OperatingSystem.IsMacOS() ? "\u2318" : "ctrl";

    static readonly (string Keys, string Does)[] Rows =
    [
        ("space / x", "Mark or unmark the tile you point at"),
        (OperatingSystem.IsMacOS() ? "\u2318-click" : "ctrl-click", "Mark without moving the selection"),
        ("enter", "Open that directory, at any depth"),
        ("\u232b / esc", "Go up one directory"),
        ("alt \u2190 / \u2192", "Back or forward, as do the mouse's side buttons"),
        ("\u2190 \u2191 \u2193 \u2192", "Move between tiles at this level"),
        ("tab", "Next largest sibling"),
        ("scroll", "Zoom toward a directory, then go into it"),
        ("shift-scroll", "Pan the magnified view"),
        ("[ / ]", "Draw fewer or more levels at once"),
        ("- / = / 0", "Magnify, shrink, or reset the view"),
        ($"{Command} = / - / 0", "Interface zoom"),
        ("/", "Filter by name: only matches keep their colour; enter shows only them"),
        ("c", "Review the marked list"),
        ("t", "Size, files or age: what areas and colours say"),
        ("r", "Scan again from the same root"),
        ("esc", "Stop a scan in progress"),
        ("v", "Scan another volume"),
        (OperatingSystem.IsMacOS() ? "\u2318O" : "ctrl-o", "Choose another directory to scan"),
        ("g", "The whole disk; click any directory above to widen"),
        ("d", "Disk usage or apparent size"),
        ("i", "Include or skip hidden entries"),
        ("p", "Show or hide the selection line"),
        ("o", OperatingSystem.IsMacOS() ? "Show it in Finder" : OperatingSystem.IsWindows() ? "Show it in File Explorer" : "Show it in the file manager"),
        ("q", "Quit"),
        ("Review screen", "m trash \u00b7 p permanent \u00b7 ! unmark all \u00b7 s save list \u00b7 a copy as prompt"),
        ("", "enter commits \u00b7 esc goes back"),
        ("", "A permanent deletion always asks first"),
    ];

    public static void Install(App app)
    {
        app.AddCss("""
            .keys-scrim { background-color: alpha(@dt_background, 0.86); }
            .keys-card { background-color: @dt_surface; border: 1px solid @dt_border; padding: 24px; }
            .keys-title { font-size: 1.3em; font-weight: bold; }
            .keys-key { color: @dt_accent; font-size: 0.85em; }
            """);
        // Over the whole window: wrap the window's content.
        var content = app.Window.Child!;
        app.Window.Child = null;
        var overlay = Gtk.Overlay.New();
        overlay.Child = content;
        app.Window.Child = overlay;

        Gtk.Widget? shown = null;
        bool Grab(Key key)
        {
            if (key.Name is "Escape" or "question" or "slash" or "q")
            {
                overlay.RemoveOverlay(shown!);
                shown = null;
                app.Keys.Release(Grab);
            }
            return true;
        }
        app.Keys.Bind("question", "App", "Every key", () =>
        {
            shown = Build(app);
            overlay.AddOverlay(shown);
            app.Keys.Grab(Grab);
        }, on: null);
    }

    static Gtk.Box Build(App app)
    {
        var grid = Gtk.Grid.New();
        grid.RowSpacing = 6;
        grid.ColumnSpacing = 18;
        var row = 0;
        foreach (var (keys, does) in Rows)
        {
            Row(keys, does);
        }

        var title = Gtk.Label.New("Keyboard and mouse");
        title.AddCssClass("keys-title");
        title.Xalign = 0;
        var foot = Gtk.Label.New($"? or esc closes · {app.State.Root} · {(app.State.Metric == Disktiles.Core.Metric.Files ? "files" : "size")}");
        foot.AddCssClass("dim");
        foot.Xalign = 0;
        var card = Gtk.Box.New(Gtk.Orientation.Vertical, 14);
        card.AddCssClass("keys-card");
        // 32.5 rem wide, with a 7 rem key lane.
        card.SetSizeRequest(520, -1);
        card.Append(title);
        card.Append(grid);
        card.Append(foot);
        var scroll = Gtk.ScrolledWindow.New();
        scroll.SetPolicy(Gtk.PolicyType.Never, Gtk.PolicyType.Automatic);
        scroll.PropagateNaturalHeight = scroll.PropagateNaturalWidth = true;
        scroll.Child = card;
        scroll.Halign = scroll.Valign = Gtk.Align.Center;
        var scrim = Gtk.Box.New(Gtk.Orientation.Vertical, 0);
        scrim.AddCssClass("keys-scrim");
        scrim.Append(scroll);
        scroll.Vexpand = true;
        return scrim;

        void Row(string keys, string does)
        {
            var key = Gtk.Label.New(keys);
            key.AddCssClass("keys-key");
            key.Xalign = 0;
            key.SetSizeRequest(112, -1);
            var label = Gtk.Label.New(does);
            label.Xalign = 0;
            label.Wrap = true;
            label.MaxWidthChars = 1;
            label.Hexpand = true;
            grid.Attach(key, 0, row, 1, 1);
            grid.Attach(label, 1, row++, 1, 1);
        }
    }
}
