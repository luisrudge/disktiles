// The panel's first section: what the keys act on, or what the pointer is
// over, against the whole scan. It shows whichever changed last: the folder,
// the selection, or the hovered tile.
using System.Diagnostics;
using Disktiles.Core;
using static Disktiles.Look;
using static Disktiles.Sidebar;

namespace Disktiles;

sealed class SelectionSection
{
    public readonly Gtk.Box Widget;
    /// Lines of other features, below the facts (git status, mark button).
    public readonly Gtk.Box Extra;
    /// After the section shows a node: its crumbs and the node, for Extra's
    /// owners to follow. Throttled like the section itself.
    public event Action<int[], NodeRef>? Shown;

    readonly AppState _state;
    readonly Gtk.DrawingArea _swatch;
    readonly Gtk.Label _name, _path, _size, _unit, _ofScan, _files, _lastWrite, _kind, _reclaim;
    readonly Gtk.ProgressBar _share;
    Category _category = Category.Other;
    string? _shareClass;

    // The panel follows the pointer at most ten times a second: each update
    // re-measures a dozen labels, which on an old dual-core laptop cost more than a frame.
    // The first change shows at once; a burst collapses into one trailing
    // update with the latest tile.
    const int EveryMs = 100;
    int[]? _wanted;
    uint _timer;
    long _shownAt;

    // Constructed before the treemap view: on a folder change the folder
    // shows first, then the tile the treemap finds under the pointer.
    public SelectionSection(AppState state)
    {
        _state = state;
        var panel = Widget = Gtk.Box.New(Gtk.Orientation.Vertical, 6);
        panel.Append(Caps("Selection"));
        _swatch = Gtk.DrawingArea.New();
        _swatch.SetContentWidth(5);
        _swatch.SetContentHeight(26);
        _swatch.Valign = Gtk.Align.Center;
        _swatch.SetDrawFunc((_, cr, w, h) =>
        {
            var (r, g, b) = Colour(_category);
            cr.SetSourceRgb(r, g, b);
            cr.Rectangle(0, 0, w, h);
            cr.Fill();
        });
        _name = Text("", "title", Pango.EllipsizeMode.End);
        var title = Gtk.Box.New(Gtk.Orientation.Horizontal, 10);
        title.Append(_swatch);
        title.Append(_name);
        panel.Append(title);
        _path = Text("", "dim", Pango.EllipsizeMode.Middle);
        panel.Append(_path);

        _size = Text("", "big");
        _unit = Text("", "unit");
        _unit.Valign = Gtk.Align.End;
        _unit.MarginBottom = 8;
        var size = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
        size.MarginTop = 6;
        size.Append(_size);
        size.Append(_unit);
        panel.Append(size);
        _share = Gtk.ProgressBar.New();
        panel.Append(_share);

        var facts = Gtk.Grid.New();
        facts.ColumnHomogeneous = true;
        facts.RowSpacing = 4;
        facts.MarginTop = 10;
        _ofScan = Text("", "value");
        _files = Text("", "value");
        _lastWrite = Text("", "value");
        _kind = Text("", "value");
        facts.Attach(Caps("Of scan"), 0, 0, 1, 1);
        facts.Attach(Caps("Files"), 1, 0, 1, 1);
        facts.Attach(_ofScan, 0, 1, 1, 1);
        facts.Attach(_files, 1, 1, 1, 1);
        var gap = Caps("Last write");
        gap.MarginTop = 10;
        var kindCaps = Caps("Kind");
        kindCaps.MarginTop = 10;
        facts.Attach(gap, 0, 2, 1, 1);
        facts.Attach(kindCaps, 1, 2, 1, 1);
        facts.Attach(_lastWrite, 0, 3, 1, 1);
        facts.Attach(_kind, 1, 3, 1, 1);
        panel.Append(facts);
        _reclaim = Text("", "dim");
        panel.Append(_reclaim);
        Extra = Gtk.Box.New(Gtk.Orientation.Vertical, 6);
        Extra.Visible = false;
        panel.Append(Extra);

        state.FolderChanged += () => Show(state.Crumbs);
        state.SelectionChanged += () =>
        {
            if (state.Selected is { } at)
            {
                Show(at);
            }
        };
        state.HoverChanged += () =>
        {
            if (state.Hovered is { } at)
            {
                Follow(at);
            }
        };
    }

    void Follow(int[] at)
    {
        _wanted = at;
        if (_timer != 0)
        {
            return;
        }
        var wait = EveryMs - (int)Stopwatch.GetElapsedTime(_shownAt).TotalMilliseconds;
        if (wait <= 0)
        {
            _shownAt = Stopwatch.GetTimestamp();
            Show(at);
            return;
        }
        _timer = GLib.Functions.TimeoutAdd(GLib.Constants.PRIORITY_DEFAULT, (uint)wait, () =>
        {
            _timer = 0;
            _shownAt = Stopwatch.GetTimestamp();
            Show(_wanted!);
            return false;
        });
    }

    /// The node at `at`: its facts against the whole scan.
    void Show(int[] at)
    {
        if (_state.Resolve(at) is not { } node)
        {
            return;
        }
        _category = node.Category;
        _swatch.QueueDraw();
        _name.SetText(node.Name);
        _path.SetText(_state.Tilde(_state.PathOf(at)));
        var (number, unit) = Split(HumanBytes(node.Bytes));
        _size.SetText(number);
        _unit.SetText(unit);
        var scanBytes = _state.Tree!.Value.Bytes;
        var share = scanBytes == 0 ? 0 : (double)node.Bytes / scanBytes;
        _share.Fraction = share;
        var shareClass = $"cat-{node.Category}";
        if (_shareClass != shareClass)
        {
            if (_shareClass is not null)
            {
                _share.RemoveCssClass(_shareClass);
            }
            _share.AddCssClass(shareClass);
            _shareClass = shareClass;
        }
        _ofScan.SetText($"{share * 100:0}%");
        _files.SetText($"{node.Files:N0}");
        _lastWrite.SetText(Ago(_state.Now, node.Modified));
        _kind.SetText(CategoryLabel(node.Category));
        _reclaim.SetText(node.Reclaim == Reclaim.None ? "" : $"Reclaimable: {ReclaimLabel(node.Reclaim)}");
        _reclaim.Visible = node.Reclaim != Reclaim.None;
        Extra.Visible = Extra.GetFirstChild() is not null;
        Shown?.Invoke(at, node);
    }
}
