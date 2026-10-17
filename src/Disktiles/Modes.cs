// What is measured and how: the Size | Files | Age switch (t), Hidden files
// (i) and Apparent size (d), which scan again. Age keeps areas by size and
// colours by last write; its ramp replaces the kind legend.
using Disktiles.Core;
using static Disktiles.Look;

namespace Disktiles;

static class Modes
{
    static readonly string[] ModeNames = ["Size", "Files", "Age"];

    public static void Install(App app)
    {
        var state = app.State;
        var syncing = false;
        var bar = Gtk.Box.New(Gtk.Orientation.Horizontal, 12);
        bar.MarginEnd = 10;
        bar.MarginTop = 4;

        var switcher = Gtk.Box.New(Gtk.Orientation.Horizontal, 0);
        switcher.AddCssClass("linked");
        var buttons = new Gtk.ToggleButton[ModeNames.Length];
        for (var i = 0; i < ModeNames.Length; i++)
        {
            var index = i;
            var button = buttons[i] = Gtk.ToggleButton.NewWithLabel(ModeNames[i]);
            // Like tab_stop(false): the keyboard stays the treemap's.
            button.FocusOnClick = button.Focusable = false;
            if (i > 0)
            {
                button.SetGroup(buttons[0]);
            }
            button.OnToggled += (_, _) =>
            {
                if (!syncing && button.Active)
                {
                    SetMode(app, index);
                }
            };
            switcher.Append(button);
        }
        bar.Append(switcher);

        var hidden = Check("Hidden files", () => Rescan(app, state.Options with { IncludeHidden = !state.Options.IncludeHidden }));
        var apparent = Check("Apparent size", () => Rescan(app, state.Options with { ApparentSize = !state.Options.ApparentSize }));
        bar.Append(hidden);
        bar.Append(apparent);
        // In order: history, mode, hidden, apparent, depth. History
        // (Navigation) is installed first and Depth (Zoom) before this.
        app.Header.End.InsertChildAfter(bar, app.Header.End.GetFirstChild());

        var ages = AgeLegend();
        ages.Visible = false;
        ages.Hexpand = true;
        // In the legend's place, after the scan totals and the filter.
        ((Gtk.Box)app.Header.Categories.GetParent()!).InsertChildAfter(ages, app.Header.Categories);

        void Sync()
        {
            syncing = true;
            buttons[ModeIndex(state)].Active = true;
            hidden.Active = state.Options.IncludeHidden;
            apparent.Active = state.Options.ApparentSize;
            ages.Visible = state.ColorMode == ColorMode.Age;
            app.Header.Categories.Visible = !ages.Visible;
            syncing = false;
            TestHooks.Log($"mode {ModeNames[ModeIndex(state)]}, hidden {hidden.Active}, apparent {apparent.Active}, age legend {ages.Visible}, first [{string.Join(", ", state.Current?.Children.Take(3).Select(c => $"{c.Name} {c.Bytes}B {c.Files}f") ?? [])}]");
        }
        Sync();
        state.LayoutChanged += Sync;
        state.TreeChanged += Sync;

        app.Keys.Bind("t", "Measure", "size, files or age: what areas and colours say",
            () => SetMode(app, (ModeIndex(state) + 1) % ModeNames.Length));
        app.Keys.Bind("d", "Measure", "disk usage or apparent size",
            () => Rescan(app, state.Options with { ApparentSize = !state.Options.ApparentSize }));
        app.Keys.Bind("i", "Measure", "include or skip hidden entries",
            () => Rescan(app, state.Options with { IncludeHidden = !state.Options.IncludeHidden }));

        Gtk.CheckButton Check(string label, Action toggled)
        {
            var check = Gtk.CheckButton.NewWithLabel(label);
            check.FocusOnClick = check.Focusable = false;
            check.OnToggled += (_, _) =>
            {
                if (!syncing)
                {
                    toggled();
                }
            };
            return check;
        }
    }

    static int ModeIndex(AppState state) =>
        state.ColorMode == ColorMode.Age ? 2 : state.Metric == Metric.Files ? 1 : 0;

    static void Rescan(App app, ScanOptions options)
    {
        app.State.Options = options;
        Scanning.Start(Scanning.Root);
    }

    /// Size, Files (areas and order by file count) or Age.
    static void SetMode(App app, int index)
    {
        var state = app.State;
        state.SetColorMode(index == 2 ? ColorMode.Age : ColorMode.Kind);
        var metric = index == 1 ? Metric.Files : Metric.Bytes;
        if (metric != state.Metric)
        {
            Resort(state, metric);
        }
    }

    /// Children are ordered by the metric, so every crumb
    /// moves. Keep the names and find them again in the re-sorted tree.
    static void Resort(AppState state, Metric metric)
    {
        if (state.Tree is not { } tree)
        {
            state.SetMetric(metric);
            return;
        }
        var folder = NamesOf(tree, state.Crumbs);
        var selected = state.Selected is { } at ? NamesOf(tree, at) : null;
        // A re-sorted copy, not a sort in place: a filter still reading
        // the old tree reads it whole. What dedup charged once stays so.
        tree = Tree.Resorted(tree, metric);
        state.Resort(tree, metric, CrumbsOf(tree, folder) ?? [], selected is null ? null : CrumbsOf(tree, selected));
    }

    static string[] NamesOf(NodeRef tree, int[] crumbs)
    {
        var names = new string[crumbs.Length];
        for (var i = 0; i < crumbs.Length; i++)
        {
            tree = tree.Children[crumbs[i]];
            names[i] = tree.Name;
        }
        return names;
    }

    static int[]? CrumbsOf(NodeRef tree, string[] names)
    {
        var crumbs = new int[names.Length];
        for (var i = 0; i < names.Length; i++)
        {
            var index = tree.Children.IndexOf(names[i]);
            if (index < 0)
            {
                return null;
            }
            crumbs[i] = index;
            tree = tree.Children[index];
        }
        return crumbs;
    }

    // The legend in age mode: the hatch, then the ramp newest first.
    static Gtk.FlowBox AgeLegend()
    {
        var legend = Gtk.FlowBox.New();
        legend.SelectionMode = Gtk.SelectionMode.None;
        legend.MaxChildrenPerLine = 16;
        legend.ColumnSpacing = 14;
        legend.MarginStart = legend.MarginEnd = 10;
        legend.MarginTop = legend.MarginBottom = 4;
        Add("Reclaimable", null);
        for (var bucket = 0; bucket < AgeBuckets.Length; bucket++)
        {
            Add(AgeBuckets[bucket].Label, bucket);
        }
        return legend;

        void Add(string text, int? bucket)
        {
            var swatch = Gtk.DrawingArea.New();
            swatch.SetContentWidth(12);
            swatch.SetContentHeight(12);
            swatch.Valign = Gtk.Align.Center;
            swatch.SetDrawFunc((_, cr, w, h) =>
            {
                cr.SetSource(bucket is { } b ? AgeAccent(b) : Fill(Category.Other, 0));
                cr.Rectangle(0, 0, w, h);
                cr.Fill();
                if (bucket is null)
                {
                    Hatch(cr, new Rect(0, 0, w, h), spacing: 3, width: 1, Look.Theme.Bright.Opacity(0.5));
                }
            });
            var item = Gtk.Box.New(Gtk.Orientation.Horizontal, 6);
            item.Append(swatch);
            item.Append(Gtk.Label.New(text));
            legend.Append(item);
        }
    }
}
