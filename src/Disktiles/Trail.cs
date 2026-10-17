// Where you are (feature 4): the trail from `/`, where a crumb in the tree
// goes there and its ▾ lists its siblings, largest first, to jump sideways;
// above the scanned root a crumb is dimmer, and clicking it widens the scan
// to there. Under it the scan totals and the filter (FilterBar); in the
// panel, git (GitStatus).
using Disktiles.Core;
using static Disktiles.Look;

namespace Disktiles;

sealed class Trail
{
    /// Steps a trail shows before it folds its middle into an ellipsis.
    const int Steps = 7;
    /// Rows a sibling menu lists; the rest are counted.
    const int SiblingRows = 24;

    readonly App _app;
    readonly AppState _state;
    readonly Gtk.Box _row;
    readonly Gtk.Label _bytes, _counts;
    // What each shown crumb does, and its ▾ if it has one, for DISKTILES_CRUMB.
    readonly List<(Action Go, Gtk.MenuButton? Menu)> _shown = [];
    // The directory a widening scan is reading, and which widening is newest.
    string? _widening;
    static Trail? s_instance;

    public static void Install(App app)
    {
        s_instance = new Trail(app);
        _ = new FilterBar(app);
        GitStatus.Install(app);
    }

    Trail(App app)
    {
        _app = app;
        _state = app.State;
        // Packed to the end: short of room, the trail gives up its start,
        // never the crumb you are in.
        _row = Gtk.Box.New(Gtk.Orientation.Horizontal, 2);
        _row.Halign = Gtk.Align.Start;
        var clip = Gtk.ScrolledWindow.New();
        clip.SetPolicy(Gtk.PolicyType.External, Gtk.PolicyType.Never);
        clip.Hexpand = true;
        clip.Child = _row;
        clip.MarginStart = 6;
        clip.MarginTop = 4;
        clip.Hadjustment!.OnChanged += (adjustment, _) => adjustment.Value = adjustment.Upper - adjustment.PageSize;
        app.Header.Start.Append(clip);

        // What the whole scan found, as one quiet line; unreadable paths in
        // the warning colour when there are any.
        _bytes = Gtk.Label.New("");
        _counts = Gtk.Label.New("");
        _counts.AddCssClass("totals");
        var unreadable = Gtk.Label.New("");
        unreadable.AddCssClass("unreadable");
        var totals = Gtk.Box.New(Gtk.Orientation.Horizontal, 6);
        totals.Append(_bytes);
        totals.Append(_counts);
        totals.Append(unreadable);
        app.Header.Under.Append(totals);

        app.AddCss("""
            .crumb { padding: 2px 6px; min-height: 0; min-width: 0; opacity: 0.75; }
            .crumb.current { opacity: 1; font-weight: 600; }
            .crumb.above { opacity: 0.5; }
            .crumb.pending { opacity: 1; color: @dt_highlight; }
            .crumb-chevron > button { padding: 2px 4px; min-height: 0; min-width: 0; opacity: 0.6; font-size: 0.85em; }
            .crumb-sep { opacity: 0.4; }
            .totals { opacity: 0.65; font-size: 0.9em; }
            .unreadable { color: @dt_warning; font-size: 0.9em; }
            .siblings { padding: 4px; }
            .siblings row { padding: 4px 8px; }
            .sibling-file { opacity: 0.8; }
            .sibling-current { font-weight: bold; }
            .sibling-value { opacity: 0.7; }
            """);

        _state.FolderChanged += Rebuild;
        _state.TreeChanged += () =>
        {
            var tree = _state.Tree!.Value;
            _bytes.SetText(HumanBytes(tree.Bytes));
            _counts.SetText($"· {HumanCount(tree.Files)} files · {HumanCount(tree.Dirs)} dirs");
            unreadable.Visible = Scanning.Unreadable > 0;
            unreadable.SetText($"· {HumanCount(Scanning.Unreadable)} unreadable");
            if (TestHooks.Trace)
            {
                Console.Error.WriteLine($"trace: totals {_bytes.GetText()} {_counts.GetText()}{(unreadable.Visible ? $" {unreadable.GetText()}" : "")}");
            }
        };
        Rebuild();
    }

    void Rebuild()
    {
        while (_row.GetFirstChild() is { } child)
        {
            _row.Remove(child);
        }
        _shown.Clear();

        // The directories above the scanned root, then the root and the path into the tree.
        var steps = new List<(string Label, string? Above, int[] Crumbs)>();
        for (var path = Path.GetDirectoryName(_state.Root); path is not null; path = Path.GetDirectoryName(path))
        {
            steps.Insert(0, (Label(path), path, []));
        }
        steps.Add((Label(_state.Root), null, []));
        if (_state.Tree is { } node)
        {
            for (var depth = 0; depth < _state.Crumbs.Length; depth++)
            {
                var index = _state.Crumbs[depth];
                if (index >= node.Children.Length)
                {
                    break;
                }
                node = node.Children[index];
                steps.Add((node.Name, null, _state.Crumbs[..(depth + 1)]));
            }
        }

        // A deep trail keeps its first two steps and its last four.
        var (hideFrom, hideTo) = steps.Count > Steps ? (2, steps.Count - (Steps - 3)) : (0, 0);
        var last = steps.Count - 1;
        for (var i = 0; i < steps.Count; i++)
        {
            if (i >= hideFrom && i < hideTo)
            {
                if (i == hideFrom)
                {
                    _row.Append(Glyph(Path.DirectorySeparatorChar.ToString()));
                    _row.Append(Glyph("…"));
                }
                continue;
            }
            // The root is its own separator: "/" then "home", not "/ / home".
            if (i > 1)
            {
                _row.Append(Glyph(Path.DirectorySeparatorChar.ToString()));
            }
            var (label, above, crumbs) = steps[i];
            if (above is not null)
            {
                var button = Crumb(label, "above");
                if (_widening == above && Scanning.Running is not null)
                {
                    button.AddCssClass("pending");
                }
                button.TooltipText = "Scan from here";
                Action go = () => Widen(above);
                button.OnClicked += (_, _) => go();
                _row.Append(button);
                _shown.Add((go, null));
            }
            else
            {
                TreeCrumb(label, crumbs, i == last);
            }
        }
        if (TestHooks.Trace)
        {
            var text = new List<string>();
            for (var child = _row.GetFirstChild(); child is not null; child = child.GetNextSibling())
            {
                text.Add(child switch
                {
                    Gtk.Label glyph => glyph.GetText(),
                    Gtk.Button button => (button.HasCssClass("above") ? "(" : "[") + button.GetLabel() + (button.HasCssClass("pending") ? "*" : "") + (button.HasCssClass("above") ? ")" : "]"),
                    _ => "[" + ((Gtk.Button)child.GetFirstChild()!).GetLabel() + (child.GetFirstChild()!.HasCssClass("current") ? " current" : "") + " ▾]",
                });
            }
            Console.Error.WriteLine($"trace: trail {string.Join(' ', text)}");
        }
    }

    // A crumb in the tree. Its label goes there (the current one opens the
    // menu instead, being there already); its ▾ opens the sibling menu.
    void TreeCrumb(string label, int[] crumbs, bool current)
    {
        var button = Crumb(label, current ? "current" : null);
        if (crumbs.Length == 0)
        {
            // The scanned root: no siblings in the tree to offer.
            Action home = () => _state.GoTo([]);
            button.OnClicked += (_, _) => home();
            _row.Append(button);
            _shown.Add((home, null));
            return;
        }
        var chevron = Gtk.MenuButton.New();
        chevron.Label = "▾";
        chevron.AddCssClass("crumb-chevron");
        chevron.HasFrame = false;
        var popover = Gtk.Popover.New();
        chevron.Popover = popover;
        // Filled when it opens: the siblings are the parent's children now,
        // by the metric now, and a closed menu costs nothing.
        Func<Key, bool>? keys = null;
        popover.OnShow += (_, _) => keys = FillMenu(popover, crumbs[..^1], crumbs[^1]);
        popover.OnClosed += (_, _) =>
        {
            if (keys is not null)
            {
                _app.Keys.Release(keys);
            }
        };
        Action go = current ? () => chevron.Popup() : () => _state.GoTo(crumbs);
        button.OnClicked += (_, _) => go();
        var chip = Gtk.Box.New(Gtk.Orientation.Horizontal, 0);
        chip.Append(button);
        chip.Append(chevron);
        _row.Append(chip);
        _shown.Add((go, chevron));
    }

    // The siblings of a crumb, largest first, with a share bar in each one's
    // colour and its size. The menu owns the arrows, Enter and Escape while
    // open; any other key closes it and goes on to its binding.
    Func<Key, bool> FillMenu(Gtk.Popover popover, int[] parent, int current)
    {
        var node = _state.Resolve(parent)!.Value;
        var rows = node.Children.Select((child, index) => (Child: child, Index: index, Value: child.Value(_state.Metric)))
            .OrderByDescending(row => row.Value).ToList();
        var more = Math.Max(0, rows.Count - SiblingRows);
        rows = rows.Take(SiblingRows).ToList();
        var largest = Math.Max(1, rows.Count > 0 ? rows[0].Value : 1);

        var panel = Gtk.Box.New(Gtk.Orientation.Vertical, 4);
        panel.SetSizeRequest(320, -1);
        panel.Append(Sidebar.Caps($"Siblings in {(parent.Length == 0 ? _state.Tilde(_state.Root) : node.Name)}"));
        var list = Gtk.ListBox.New();
        list.AddCssClass("siblings");
        foreach (var (child, index, value) in rows)
        {
            var name = Sidebar.Text(child.Name, child.IsDir ? "sibling-dir" : "sibling-file", Pango.EllipsizeMode.End);
            if (index == current)
            {
                name.AddCssClass("sibling-current");
            }
            var bar = Gtk.ProgressBar.New();
            bar.Fraction = (double)value / largest;
            bar.SetSizeRequest(60, -1);
            bar.Valign = Gtk.Align.Center;
            bar.AddCssClass($"cat-{child.Category}");
            var size = Gtk.Label.New(_state.Metric == Metric.Bytes ? HumanBytes(value) : HumanCount(value));
            size.Xalign = 1;
            size.SetSizeRequest(76, -1);
            size.AddCssClass("sibling-value");
            var line = Gtk.Box.New(Gtk.Orientation.Horizontal, 12);
            line.Append(name);
            line.Append(bar);
            line.Append(size);
            list.Append(line);
        }
        var scroll = Gtk.ScrolledWindow.New();
        scroll.SetPolicy(Gtk.PolicyType.Never, Gtk.PolicyType.Automatic);
        scroll.PropagateNaturalHeight = true;
        scroll.MaxContentHeight = 440;
        scroll.Child = list;
        panel.Append(scroll);
        if (more > 0)
        {
            panel.Append(Sidebar.Text($"+{more} smaller", "dim"));
        }
        popover.Child = panel;

        var highlighted = Math.Max(0, rows.FindIndex(row => row.Index == current));
        list.SelectRow(list.GetRowAtIndex(highlighted));
        list.OnRowActivated += (_, e) => Choose(popover, parent, rows[e.Row.GetIndex()].Index);
        bool Keys(Key key)
        {
            switch (key.Name)
            {
                case "Down" or "j":
                    highlighted = Math.Min(highlighted + 1, rows.Count - 1);
                    break;
                case "Up" or "k":
                    highlighted = Math.Max(highlighted - 1, 0);
                    break;
                case "Home":
                    highlighted = 0;
                    break;
                case "End":
                    highlighted = rows.Count - 1;
                    break;
                case "Return" or "space" or "Right" or "l":
                    if (rows.Count > 0)
                    {
                        Choose(popover, parent, rows[highlighted].Index);
                    }
                    return true;
                case "Escape" or "Left" or "h":
                    popover.Popdown();
                    return true;
                default:
                    popover.Popdown();
                    return false;
            }
            list.SelectRow(list.GetRowAtIndex(highlighted));
            if (TestHooks.Trace)
            {
                Console.Error.WriteLine($"trace: siblings highlight {highlighted} {rows[highlighted].Child.Name}");
            }
            return true;
        }
        _app.Keys.Grab(Keys);
        if (TestHooks.Trace)
        {
            Console.Error.WriteLine($"trace: siblings of [{string.Join(',', parent)}]: {string.Join(", ", rows.Take(5).Select(row => $"{row.Child.Name} {row.Value}"))}{(rows.Count > 5 ? ", …" : "")}; +{more} smaller; highlighted {highlighted}");
        }
        return Keys;
    }

    // Into a sibling directory, or beside a file, selected. After the menu
    // has closed: going there rebuilds the trail the menu hangs from.
    void Choose(Gtk.Popover popover, int[] parent, int index)
    {
        popover.Popdown();
        GLib.Functions.IdleAdd(GLib.Constants.PRIORITY_DEFAULT, () =>
        {
            int[] crumbs = [.. parent, index];
            if (_state.Resolve(crumbs) is { IsDir: true })
            {
                _state.GoTo(crumbs);
            }
            else
            {
                _state.GoTo(parent);
                _state.Select(crumbs);
            }
            return false;
        });
    }

    /// Scan from `above`, a directory holding the scanned root. The view
    /// stays until the wider tree lands, which opens with the directory you
    /// came from selected.
    // ponytail: a full rescan, not memoized (the scanner has no `known`
    // tree to reuse yet); Scanning should own this once it can.
    // Scanning shows the progress and owns the status line; once the wider
    // tree lands, the folder you came from is selected.
    void Widen(string above)
    {
        var from = _state.Root;
        if (_state.Tree is null || from == above || !from.StartsWith(above, StringComparison.Ordinal))
        {
            return;
        }
        _widening = above;
        // ponytail: a full rescan of `above`, though the subtree under the old
        // root is already measured. Memoize in Scanner when widening feels slow.
        Scanning.Start(above, tree =>
        {
            _widening = null;
            if (tree is null)
            {
                Rebuild();
                return;
            }
            var top = Path.GetRelativePath(above, from).Split(Path.DirectorySeparatorChar)[0];
            var index = tree.Value.Children.IndexOf(top);
            if (index >= 0)
            {
                _state.Select([index]);
            }
            Rebuild();
        });
        Rebuild();
    }

    /// DISKTILES_CRUMB=n[,menu]: press the n-th crumb shown, or its ▾.
    public static string Press(int n, bool menu)
    {
        if (s_instance is not { } trail || n < 0 || n >= trail._shown.Count)
        {
            return "no such crumb";
        }
        var (go, chevron) = trail._shown[n];
        if (menu)
        {
            if (chevron is null)
            {
                return "no menu";
            }
            chevron.Popup();
        }
        else
        {
            go();
        }
        return "pressed";
    }

    static Gtk.Button Crumb(string label, string? css)
    {
        var button = Gtk.Button.NewWithLabel(label);
        button.HasFrame = false;
        button.AddCssClass("crumb");
        if (css is not null)
        {
            button.AddCssClass(css);
        }
        return button;
    }

    static Gtk.Label Glyph(string text)
    {
        var glyph = Gtk.Label.New(text);
        glyph.AddCssClass("crumb-sep");
        return glyph;
    }

    // The last part of a path, or the whole path for a root ("/", "C:\").
    static string Label(string path) => Path.GetFileName(path) is { Length: > 0 } name ? name : path;
}
