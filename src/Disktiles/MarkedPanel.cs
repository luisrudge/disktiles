// The panel's share of marking: the selection's mark button
// and badges, the "Marked" section, the notice line, and the disk meter's
// free-after-marks projection with the way to the review. All of it follows
// the marks (Marker.Changed) or the throttled selection, never the hover.
using Disktiles.Core;
using static Disktiles.Look;
using static Disktiles.Sidebar;

namespace Disktiles;

static class MarkedPanel
{
    /// Marked rows the panel lists before pointing at the review screen.
    const int MarkedRows = 6;

    public static void Install(App app, Marker marker)
    {
        app.AddCss(Css());
        SelectionExtra(app, marker);
        app.Sidebar.Add(Slot.Marked, MarkedSection(app, marker));
        app.Sidebar.Add(Slot.Notice, NoticeLine(marker));
        app.Disk.Widget.Append(Projection(app, marker));
    }

    /// Badges and the one action that matters: "Mark for removal", or, inside
    /// a marked directory, the offer to unmark that directory instead.
    static void SelectionExtra(App app, Marker marker)
    {
        var box = Gtk.Box.New(Gtk.Orientation.Vertical, 6);
        box.MarginTop = 8;
        var chips = Gtk.Box.New(Gtk.Orientation.Horizontal, 6);
        var button = Gtk.Button.New();
        button.FocusOnClick = false;
        box.Append(chips);
        box.Append(button);
        app.Selection.Extra.Append(box);

        int[]? shown = null;
        Action? act = null;
        string? drawn = null;
        button.OnClicked += (_, _) => act?.Invoke();
        void Update()
        {
            box.Visible = shown is not null && !shown.SequenceEqual(app.State.Crumbs) && app.State.Resolve(shown) is not null;
            if (!box.Visible)
            {
                return;
            }
            var crumbs = shown!;
            var path = marker.PathAt(crumbs);
            var marked = marker.Marks.Contains(path);
            var ancestor = marked ? null : marker.MarkedAncestor(path);
            act = ancestor is not null ? () => marker.Unmark(ancestor) : () => marker.Toggle(crumbs);
            // The selection follows the hover: rebuild only what changed.
            var state = $"{path}\n{marked}\n{ancestor}";
            if (state == drawn)
            {
                return;
            }
            drawn = state;
            Clear(chips);
            if (marked)
            {
                chips.Append(Chip("Marked", "danger"));
            }
            if (ancestor is not null)
            {
                chips.Append(Chip($"Goes with {Path.GetFileName(ancestor)}", "danger"));
            }
            chips.Visible = chips.GetFirstChild() is not null;
            button.Label = ancestor is not null ? $"Unmark {Path.GetFileName(ancestor)}" : marked ? "Unmark" : "Mark for removal";
            // Marking is what this tool is for: the filled button, unless it undoes.
            if (marked || ancestor is not null)
            {
                button.RemoveCssClass("mark");
            }
            else
            {
                button.AddCssClass("mark");
            }
        }
        app.Selection.Shown += (crumbs, _) =>
        {
            shown = crumbs;
            Update();
        };
        marker.Changed += Update;
    }

    static Gtk.Box MarkedSection(App app, Marker marker)
    {
        var section = Gtk.Box.New(Gtk.Orientation.Vertical, 4);
        section.Append(Separator());
        var title = Caps("Marked");
        title.Hexpand = true;
        var total = Text("", "dim");
        var header = Gtk.Box.New(Gtk.Orientation.Horizontal, 0);
        header.Append(title);
        header.Append(total);
        section.Append(header);
        var rows = Gtk.Box.New(Gtk.Orientation.Vertical, 2);
        section.Append(rows);

        void Update()
        {
            var count = marker.Marks.Count;
            var plan = marker.Plan;
            title.SetText(count == 0 ? "MARKED" : $"MARKED · {count}");
            total.SetText(count == 0 ? "" : HumanBytes(plan.Bytes));
            Clear(rows);
            if (count == 0)
            {
                rows.Append(Text("Space marks the tile you point at", "dim"));
                return;
            }
            foreach (var item in marker.Marks.Items.Take(MarkedRows))
            {
                var category = app.State.Tree is { } tree && Marks.Find(marker.Root, tree, item.Path) is { } node ? node.Category : Category.Other;
                var dot = Gtk.DrawingArea.New();
                dot.SetContentWidth(8);
                dot.SetContentHeight(8);
                dot.Valign = Gtk.Align.Center;
                dot.SetDrawFunc((_, cr, w, h) =>
                {
                    cr.SetSource(Accent(category));
                    cr.Arc(w / 2.0, h / 2.0, Math.Min(w, h) / 2.0, 0, 2 * Math.PI);
                    cr.Fill();
                });
                var unmark = Gtk.Button.NewWithLabel("×");
                unmark.AddCssClass("flat");
                unmark.AddCssClass("unmark");
                unmark.FocusOnClick = false;
                var path = item.Path;
                unmark.OnClicked += (_, _) => marker.Unmark(path);
                var row = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
                row.Append(dot);
                row.Append(Text(app.State.Tilde(item.Path), "small", Pango.EllipsizeMode.Middle));
                row.Append(Text(HumanBytes(item.Bytes), "dim"));
                row.Append(unmark);
                rows.Append(row);
            }
            if (count > MarkedRows)
            {
                rows.Append(Text($"+{count - MarkedRows} more on the review screen", "dim"));
            }
            if (plan.Covered.Count > 0 || plan.Blocked.Count > 0)
            {
                rows.Append(Text($"{plan.Covered.Count} nested · {plan.Blocked.Count} kept back", "dim"));
            }
        }
        marker.Changed += Update;
        Update();
        return section;
    }

    /// The last thing marking said, above the disk.
    static Gtk.Label NoticeLine(Marker marker)
    {
        var line = Text("", "notice");
        line.Wrap = true;
        line.MaxWidthChars = 1;
        line.Hexpand = true;
        line.Visible = false;
        string? css = null;
        marker.NoticeChanged += () =>
        {
            line.Visible = marker.Notice is not null;
            if (marker.Notice is not var (text, status))
            {
                return;
            }
            line.SetText(text);
            if (css is not null)
            {
                line.RemoveCssClass(css);
            }
            line.AddCssClass(css = $"notice-{status.ToString().ToLowerInvariant()}");
        };
        return line;
    }

    /// Below the disk's numbers: free space once the marks go, counting
    /// only what comes back to this volume, what is
    /// left out and why, and the way to the review, named by its saving.
    static Gtk.Box Projection(App app, Marker marker)
    {
        var box = Gtk.Box.New(Gtk.Orientation.Vertical, 6);
        box.Visible = false;
        var after = Text("", "projection");
        after.Xalign = 1;
        var meter = Gtk.DrawingArea.New();
        meter.SetContentHeight(8);
        var leftOut = Text("", "dim");
        leftOut.Wrap = true;
        var review = Gtk.Button.New();
        review.AddCssClass("review");
        review.FocusOnClick = false;
        var label = Text("", "review-label", Pango.EllipsizeMode.End);
        var content = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
        content.Append(label);
        content.Append(Keycap("c"));
        review.Child = content;
        review.OnClicked += (_, _) => app.State.SetScreen(Screen.Review);
        box.Append(after);
        box.Append(meter);
        box.Append(leftOut);
        box.Append(review);

        SpaceInfo? space = null;
        ulong reclaiming = 0;
        // Used space from the left; the slice the marks give back is the
        // hatched end of it, so the gap it leaves is what comes back.
        meter.SetDrawFunc((_, cr, w, h) =>
        {
            if (space is not { Total: > 0 } volume)
            {
                return;
            }
            var usedNow = (double)volume.Used / volume.Total;
            var usedAfter = Math.Min((double)volume.AfterRemoving(reclaiming).Used / volume.Total, usedNow);
            cr.SetSource(Look.Theme.Foreground.Opacity(0.08));
            cr.Rectangle(0, 0, w, h);
            cr.Fill();
            cr.SetSource(Look.Theme.Foreground.Opacity(0.22));
            cr.Rectangle(0, 0, w * usedAfter, h);
            cr.Fill();
            var slice = new Rect((float)(w * usedAfter), 0, (float)(w * (usedNow - usedAfter)), h);
            cr.SetSource(Look.Highlight.Opacity(0.25));
            cr.Rectangle(slice.X, slice.Y, slice.W, slice.H);
            cr.Fill();
            Hatch(cr, slice, spacing: 4, width: 1, Look.Highlight.Opacity(0.5));
        });

        void Update()
        {
            box.Visible = marker.Marks.Count > 0;
            if (!box.Visible)
            {
                return;
            }
            var plan = marker.Plan;
            space = SpaceInfo.Of(app.State.Root);
            reclaiming = plan.Reclaim;
            after.Visible = meter.Visible = space is not null && reclaiming > 0;
            if (space is { } volume)
            {
                after.SetText($"→ {HumanBytes(volume.AfterRemoving(reclaiming).Available)} free");
            }
            meter.QueueDraw();
            leftOut.Visible = NotCounted(plan) is not null;
            leftOut.SetText($"not counted here: {NotCounted(plan)}");
            label.SetText($"Review {marker.Marks.Count} marked · frees {HumanBytes(reclaiming)}…");
        }
        marker.Changed += Update;
        app.State.TreeChanged += Update;
        return box;
    }

    /// What the meter leaves out: bytes marked on another volume come back
    /// there, and bytes no volume can be found for are not claimed at all.
    public static string? NotCounted(Plan plan)
    {
        var parts = new List<string>();
        if (plan.Foreign > 0)
        {
            parts.Add($"{HumanBytes(plan.Foreign)} on other volumes");
        }
        if (plan.Unattributed > 0)
        {
            parts.Add($"{HumanBytes(plan.Unattributed)} could not be placed");
        }
        return parts.Count == 0 ? null : string.Join(", ", parts);
    }

    public static Gtk.Label Chip(string text, string css)
    {
        var chip = Gtk.Label.New(text);
        chip.AddCssClass("chip");
        chip.AddCssClass(css);
        chip.Ellipsize = Pango.EllipsizeMode.End;
        chip.MaxWidthChars = 28;
        return chip;
    }

    public static Gtk.Label Keycap(string key)
    {
        var cap = Gtk.Label.New(key);
        cap.AddCssClass("keycap");
        return cap;
    }

    public static void Clear(Gtk.Box box)
    {
        while (box.GetFirstChild() is { } child)
        {
            box.Remove(child);
        }
    }

    // The theme's danger, warning and success, and the highlight (Omarchy.cs).
    static string Css() => """
        .chip { font-size: 0.8em; padding: 1px 6px; border: 1px solid; border-radius: 3px; }
        .chip.danger { color: @dt_danger; border-color: alpha(@dt_danger, 0.6); }
        .chip.warning { color: @dt_warning; border-color: alpha(@dt_warning, 0.6); }
        .chip.secondary { color: @dt_secondary; border-color: alpha(@dt_foreground, 0.3); }
        button.mark { background: @dt_highlight; color: @dt_on_highlight; font-weight: 600; }
        button.unmark { padding: 0 6px; min-height: 0; opacity: 0.7; }
        button.unmark:hover { color: @dt_danger; opacity: 1; }
        .small { font-size: 0.9em; }
        .notice { padding: 4px 8px; border: 1px solid; font-size: 0.9em; }
        .notice-neutral { color: @dt_secondary; border-color: alpha(@dt_foreground, 0.3); }
        .notice-success { color: @dt_success; border-color: alpha(@dt_success, 0.5); }
        .notice-warning { color: @dt_warning; border-color: alpha(@dt_warning, 0.5); }
        .notice-error { color: @dt_danger; border-color: alpha(@dt_danger, 0.5); }
        .projection { color: @dt_highlight; font-size: 1.3em; font-weight: bold; }
        button.review { border: 1px solid @dt_highlight; background: alpha(@dt_highlight, 0.1); border-radius: 0; padding: 6px 10px; }
        button.review:hover { background: alpha(@dt_highlight, 0.18); }
        .review-label { color: @dt_highlight; }
        """;
}
