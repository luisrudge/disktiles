// Above the treemap: a row with the trail at the start and the view controls
// (Size/Files/Age, Hidden, Apparent, Depth) at the end; under it the scan
// totals, the filter and the legend.
using Disktiles.Core;
using static Disktiles.Look;

namespace Disktiles;

sealed class Header
{
    public readonly Gtk.Box Widget;
    /// Where you are: the trail. Fills the row.
    public readonly Gtk.Box Start;
    /// What is measured and how: controls, appended in install order.
    public readonly Gtk.Box End;
    /// The key to the kind colours; Age mode shows its own ramp instead.
    public readonly Gtk.FlowBox Categories;
    /// Under the trail, before the legend: the scan totals and the filter.
    public readonly Gtk.Box Under;
    /// The legends (Categories, and Age mode's ramp beside it). The filter
    /// takes their place while it is open.
    public readonly Gtk.Box Legends;

    public Header()
    {
        Start = Gtk.Box.New(Gtk.Orientation.Horizontal, 0);
        Start.Hexpand = true;
        End = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
        var top = Gtk.Box.New(Gtk.Orientation.Horizontal, 0);
        top.Append(Start);
        top.Append(End);
        Under = Gtk.Box.New(Gtk.Orientation.Horizontal, 16);
        Under.MarginStart = 10;
        Under.Valign = Gtk.Align.Center;
        Categories = Legend();
        Categories.Hexpand = true;
        Legends = Gtk.Box.New(Gtk.Orientation.Horizontal, 0);
        Legends.Hexpand = true;
        Legends.Append(Categories);
        var under = Gtk.Box.New(Gtk.Orientation.Horizontal, 0);
        under.Append(Under);
        under.Append(Legends);
        Widget = Gtk.Box.New(Gtk.Orientation.Vertical, 0);
        Widget.Append(top);
        Widget.Append(under);
    }

    // The categories and the hatch, as swatches that wrap in a narrow window.
    static Gtk.FlowBox Legend()
    {
        var legend = Gtk.FlowBox.New();
        legend.SelectionMode = Gtk.SelectionMode.None;
        legend.MaxChildrenPerLine = 16;
        legend.ColumnSpacing = 14;
        legend.MarginStart = legend.MarginEnd = 10;
        legend.MarginTop = legend.MarginBottom = 4;
        Add("Reclaimable", null);
        foreach (var category in Look.Legend)
        {
            Add(CategoryLabel(category), category);
        }
        return legend;

        void Add(string text, Category? category)
        {
            var swatch = Gtk.DrawingArea.New();
            swatch.SetContentWidth(12);
            swatch.SetContentHeight(12);
            swatch.Valign = Gtk.Align.Center;
            swatch.SetDrawFunc((_, cr, w, h) =>
            {
                // Accents, and the hatch over an Other fill.
                cr.SetSource(category is { } c ? Accent(c) : Fill(Category.Other, 0));
                cr.Rectangle(0, 0, w, h);
                cr.Fill();
                if (category is null)
                {
                    Hatch(cr, new Rect(0, 0, w, h), spacing: 3, width: 1, Look.Theme.Bright.Opacity(0.5));
                }
            });
            var label = Gtk.Label.New(text);
            var item = Gtk.Box.New(Gtk.Orientation.Horizontal, 6);
            item.Append(swatch);
            item.Append(label);
            legend.Append(item);
        }
    }
}
