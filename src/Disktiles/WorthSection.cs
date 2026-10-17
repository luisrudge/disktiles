// "Worth a look": the core's findings (Insights), each titled by its path's tail.
using Disktiles.Core;
using static Disktiles.Look;
using static Disktiles.Sidebar;

namespace Disktiles;

sealed class WorthSection
{
    /// How many findings the panel lists.
    const int Limit = 6;

    public readonly Gtk.Box Widget;
    /// A row was activated: its crumbs from the scan root.
    public event Action<int[]>? Jump;

    readonly Gtk.Label _total;
    readonly Gtk.ListBox _list;
    List<Candidate> _candidates = [];

    public WorthSection(AppState state)
    {
        Widget = Gtk.Box.New(Gtk.Orientation.Vertical, 6);
        Widget.Append(Separator());
        var header = Gtk.Box.New(Gtk.Orientation.Horizontal, 0);
        var caps = Caps("Worth a look");
        caps.Hexpand = true;
        _total = Text("", "accent");
        header.Append(caps);
        header.Append(_total);
        Widget.Append(header);
        _list = Gtk.ListBox.New();
        _list.SelectionMode = Gtk.SelectionMode.None;
        _list.AddCssClass("worth");
        _list.OnRowActivated += (_, e) =>
        {
            var index = e.Row.GetIndex();
            if (index >= 0 && index < _candidates.Count)
            {
                Jump?.Invoke(_candidates[index].Crumbs);
            }
        };
        Widget.Append(_list);
        state.TreeChanged += () => Fill(state.Tree!.Value, Insights.WorthALook(state.Tree!.Value, state.Now, Limit));
    }

    void Fill(NodeRef tree, List<Candidate> candidates)
    {
        _candidates = candidates;
        while (_list.GetFirstChild() is { } row)
        {
            _list.Remove(row);
        }
        ulong total = 0;
        var largest = candidates.Count > 0 ? candidates[0].Bytes : 1;
        foreach (var candidate in candidates)
        {
            total += candidate.Bytes;
            var (title, reason) = InsightText(tree, candidate);
            var name = Text(title, "row-title", Pango.EllipsizeMode.Middle);
            name.Hexpand = true;
            var amount = Text(HumanBytes(candidate.Bytes), "row-size");
            var top = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
            top.Append(name);
            top.Append(amount);
            var bar = Gtk.ProgressBar.New();
            bar.Fraction = (double)candidate.Bytes / largest;
            bar.AddCssClass("accent");
            var row = Gtk.Box.New(Gtk.Orientation.Vertical, 3);
            row.AddCssClass("row");
            row.Append(top);
            row.Append(Text(reason, "dim"));
            row.Append(bar);
            _list.Append(row);
        }
        _total.SetText(candidates.Count == 0 ? "nothing big" : HumanBytes(total));
    }

    /// A finding's title and reason: the last two parts of the path, and why.
    static (string Title, string Reason) InsightText(NodeRef tree, Candidate candidate)
    {
        var names = new List<string>();
        var node = tree;
        foreach (var index in candidate.Crumbs)
        {
            node = node.Children[index];
            names.Add(node.Name);
        }
        var tail = string.Join(Path.DirectorySeparatorChar, names.Skip(Math.Max(0, names.Count - 2)));
        return candidate.Finding switch
        {
            FindingKind.Reclaimable => (tail, ReclaimLabel(candidate.Reason)),
            FindingKind.Worktrees => (tail, $"{candidate.Count} worktree{(candidate.Count == 1 ? "" : "s")} · oldest {candidate.OldestDays} d"),
            _ => ($"{tail} > {Insights.StaleDays} days", $"{candidate.Count} experiment{(candidate.Count == 1 ? "" : "s")} untouched"),
        };
    }
}
