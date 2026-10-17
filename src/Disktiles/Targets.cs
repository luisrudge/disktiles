// What to scan instead: `v` lists every mounted volume with its free space
// (fullest first) and scans the one picked; ctrl+o asks for a directory
// through Gtk.FileDialog, which on Omarchy is the xdg-desktop-portal's
// chooser; `g` scans the whole disk the scanned root lives on.
using Disktiles.Core;
using static Disktiles.Look;

namespace Disktiles;

static class Targets
{
    public static void Install(App app)
    {
        var picker = new VolumePicker(app);
        app.Treemap.AddOverlay(picker.Widget);
        app.AddCss(VolumePicker.Css);
        app.Keys.Bind("v", "Scan", "scan another mounted volume", picker.Open);
        // The disk header doubles as the way to another volume.
        var volumes = Gtk.Button.NewWithLabel("Volumes");
        volumes.FocusOnClick = false;
        volumes.OnClicked += (_, _) => picker.Open();
        app.Disk.Header.Append(volumes);
        app.Keys.Bind("ctrl+o", "Scan", "choose another directory to scan", () => _ = ChooseFolder(app));
        app.Keys.Bind("g", "Scan", "the whole disk", () => WholeDisk(app));
    }

    /// The disk of what is on screen, following the root: from ~ that is `/`
    /// on Omarchy. Already there: go to its top.
    static void WholeDisk(App app)
    {
        if (Space.VolumeRootFor(Scanning.Root) is not { } disk)
        {
            return;
        }
        if (Space.PathEquals(disk, app.State.Root) && Scanning.Running is null)
        {
            app.State.GoTo([]);
        }
        else
        {
            // ponytail: a fresh walk; the tree already scanned is not reused
            // as a memoized subtree of the wider one.
            Scanning.Start(disk);
        }
    }

    static async Task ChooseFolder(App app)
    {
        string? path;
        if (TestHooks.PickFolder is { } picked)
        {
            path = picked;
        }
        else
        {
            var dialog = Gtk.FileDialog.New();
            dialog.Title = "Choose a directory to scan";
            dialog.AcceptLabel = "Scan";
            try
            {
                path = (await dialog.SelectFolderAsync(app.Window))?.GetPath();
            }
            catch (GLib.GException)
            {
                // Dismissed.
                return;
            }
        }
        // The dialog does not block the window: the review list or a
        // removal may have started while it was open.
        if (path is null || app.State.Screen != Screen.Explore)
        {
            return;
        }
        TestHooks.Log($"chose folder {path}");
        Scanning.Start(Space.RealPath(path) ?? path);
    }
}

/// The volume picker: a card over the treemap that owns the keys
/// while open. Arrows move, Enter scans, Escape (or a click outside) closes.
sealed class VolumePicker
{
    public readonly Gtk.Box Widget;
    readonly App _app;
    readonly Gtk.ListBox _rows;
    readonly Gtk.Label _status;
    readonly Func<Key, bool> _keys;
    List<Volume> _volumes = [];
    bool _loading;
    int _epoch;

    public VolumePicker(App app)
    {
        _app = app;
        // A backdrop the size of the treemap: a click on it closes.
        Widget = Gtk.Box.New(Gtk.Orientation.Vertical, 0);
        Widget.AddCssClass("volumes-backdrop");
        Widget.Visible = false;
        var outside = Gtk.GestureClick.New();
        outside.OnPressed += (gesture, e) =>
        {
            if (Widget.Pick(e.X, e.Y, Gtk.PickFlags.Default) == Widget)
            {
                Close();
            }
        };
        Widget.AddController(outside);

        var card = Gtk.Box.New(Gtk.Orientation.Vertical, 10);
        card.AddCssClass("volumes");
        card.Halign = Gtk.Align.Center;
        card.Valign = Gtk.Align.Center;
        card.Vexpand = true;
        card.SetSizeRequest(460, -1);
        var title = Gtk.Label.New("Scan a volume");
        title.AddCssClass("volumes-title");
        title.Xalign = 0;
        card.Append(title);
        var help = Gtk.Label.New("Up and down moves, Enter scans it, Escape stays here.");
        help.AddCssClass("dim");
        help.Xalign = 0;
        card.Append(help);
        _status = Gtk.Label.New("");
        _status.Xalign = 0;
        card.Append(_status);
        _rows = Gtk.ListBox.New();
        _rows.SelectionMode = Gtk.SelectionMode.Browse;
        _rows.AddCssClass("volume-rows");
        _rows.OnRowActivated += (_, e) => Choose(e.Row.GetIndex());
        card.Append(_rows);
        Widget.Append(card);

        _keys = key =>
        {
            switch (key.Name)
            {
                case "Escape": Close(); break;
                case "Return": Choose(_rows.GetSelectedRow()?.GetIndex() ?? -1); break;
                case "Up" or "k": Move(-1); break;
                case "Down" or "j": Move(1); break;
            }
            // Nothing behind the picker acts while it is open.
            return true;
        };
    }

    public void Open()
    {
        if (Widget.Visible)
        {
            return;
        }
        var epoch = ++_epoch;
        var root = Scanning.Root;
        _volumes = [];
        Fill();
        _loading = true;
        _status.SetText("Looking for volumes…");
        _status.Visible = true;
        Widget.Visible = true;
        _app.Keys.Grab(_keys);
        // Removable media and network volumes can take seconds to answer.
        _ = Load(root, epoch);
    }

    async Task Load(string root, int epoch)
    {
        var volumes = await Task.Run(() =>
        {
            var volumes = Space.Volumes();
            if (Space.VolumeRootFor(root) is { } disk && !volumes.Any(volume => volume.Point == disk))
            {
                volumes.Add(new Volume(disk, null, SpaceInfo.Of(disk)));
            }
            volumes.RemoveAll(volume => Space.PathEquals(volume.Point, root));
            return volumes;
        });
        if (epoch != _epoch || !Widget.Visible)
        {
            return;
        }
        _loading = false;
        _volumes = volumes;
        TestHooks.Log($"volumes: {string.Join(" | ", volumes.Select(v => $"{v.Point} {v.Device} {v.Space?.Available}"))}");
        _status.SetText("No other volume could be read.");
        _status.Visible = volumes.Count == 0;
        Fill();
    }

    void Fill()
    {
        _rows.RemoveAll();
        foreach (var volume in _volumes)
        {
            var free = volume.Space is { } space ? $"{HumanBytes(space.Available)} free" : "unknown free";
            var label = Gtk.Label.New(volume.Device is { } device
                ? $"{volume.Point}  ·  {device}  ·  {free}"
                : $"{volume.Point}  ·  {free}");
            label.Xalign = 0;
            label.Ellipsize = Pango.EllipsizeMode.Middle;
            label.MaxWidthChars = 1;
            label.Hexpand = true;
            _rows.Append(label);
        }
        _rows.Visible = _volumes.Count > 0;
        if (_rows.GetRowAtIndex(0) is { } first)
        {
            _rows.SelectRow(first);
        }
    }

    // Wrapping at the ends.
    void Move(int step)
    {
        if (_volumes.Count == 0)
        {
            return;
        }
        var at = _rows.GetSelectedRow()?.GetIndex() ?? 0;
        _rows.SelectRow(_rows.GetRowAtIndex((at + step + _volumes.Count) % _volumes.Count));
    }

    void Choose(int index)
    {
        if (_loading)
        {
            return;
        }
        var point = index >= 0 && index < _volumes.Count ? _volumes[index].Point : null;
        Close();
        if (point is not null)
        {
            Scanning.Start(point);
        }
    }

    void Close()
    {
        Widget.Visible = false;
        _app.Keys.Release(_keys);
    }

    public const string Css = """
        .volumes-backdrop { background-color: alpha(@dt_background, 0.5); }
        .volumes { background-color: @dt_background; border: 1px solid @dt_border; padding: 18px; }
        .volumes-title { font-size: 1.25em; font-weight: bold; }
        .volume-rows { background: transparent; }
        .volume-rows row { padding: 8px 10px; }
        """;
}
