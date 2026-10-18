// Where you are (feature 4): the trail from `/`, where a crumb in the tree
// goes there and its ▾ lists its siblings, largest first, to jump sideways;
// above the scanned root a crumb is dimmer, and clicking it widens the scan
// to there. Under it the scan totals and the filter (FilterBar.cs); in the
// panel, git (Git.cs).
using Disktiles.Core;

namespace Disktiles;

/// One piece of the trail. `Kind`: "sep" (a / or …), "above" (above the
/// scanned root: widens the scan), "crumb" (in the tree). `Index` is what
/// Crumb(i) and CrumbMenu(i) take; -1 for separators.
sealed record CrumbModel(string Label, string Kind, bool Current, bool Pending, bool HasMenu, int Index);

/// A sibling menu row: the name, its share of the largest, its size.
sealed record SiblingRow(string Name, bool IsDir, double Fraction, string Size, string Color, bool Current);

sealed record SiblingsModel(bool Visible, int Crumb, string Title, List<SiblingRow> Rows, int Highlighted, string More);

/// The scan's totals under the trail: bytes, counts, and unreadable paths.
sealed record TotalsModel(string Bytes, string Counts, string Unreadable);

sealed partial class App
{
    /// Steps a trail shows before it folds its middle into an ellipsis.
    const int TrailSteps = 7;
    /// Rows a sibling menu lists; the rest are counted.
    const int SiblingRows = 24;

    // What each shown crumb does, and its menu's parent and index if it has one.
    readonly List<(Action Go, (int[] Parent, int Index)? Menu)> _crumbs = [];
    // The directory a widening scan is reading.
    string? _widening;
    SiblingsModel _siblings = new(false, -1, "", [], 0, "");
    int[] _siblingParent = [];
    List<int> _siblingIndices = [];

    void InitTrail()
    {
        State.FolderChanged += () => Touch(Part.Header);
        InitFilter();
        InitGit();
    }

    public List<CrumbModel> Trail
    {
        get
        {
            _crumbs.Clear();
            var items = new List<CrumbModel>();
            // The directories above the scanned root, then the root and the path into the tree.
            var steps = new List<(string Label, string? Above, int[] Crumbs)>();
            for (var path = Path.GetDirectoryName(State.Root); path is not null; path = Path.GetDirectoryName(path))
            {
                steps.Insert(0, (Label(path), path, []));
            }
            steps.Add((Label(State.Root), null, []));
            if (State.Tree is { } node)
            {
                for (var depth = 0; depth < State.Crumbs.Length; depth++)
                {
                    var index = State.Crumbs[depth];
                    if (index >= node.Children.Length)
                    {
                        break;
                    }
                    node = node.Children[index];
                    steps.Add((node.Name, null, State.Crumbs[..(depth + 1)]));
                }
            }

            // A deep trail keeps its first two steps and its last four.
            var (hideFrom, hideTo) = steps.Count > TrailSteps ? (2, steps.Count - (TrailSteps - 3)) : (0, 0);
            var last = steps.Count - 1;
            for (var i = 0; i < steps.Count; i++)
            {
                if (i >= hideFrom && i < hideTo)
                {
                    if (i == hideFrom)
                    {
                        items.Add(new("/", "sep", false, false, false, -1));
                        items.Add(new("…", "sep", false, false, false, -1));
                    }
                    continue;
                }
                // The root is its own separator: "/" then "home", not "/ / home".
                if (i > 1)
                {
                    items.Add(new("/", "sep", false, false, false, -1));
                }
                var (label, above, crumbs) = steps[i];
                if (above is not null)
                {
                    _crumbs.Add((() => Widen(above), null));
                    items.Add(new(label, "above", false, _widening == above && Running is not null, false, _crumbs.Count - 1));
                }
                else if (crumbs.Length == 0)
                {
                    // The scanned root: no siblings in the tree to offer.
                    _crumbs.Add((() => State.GoTo([]), null));
                    items.Add(new(label, "crumb", i == last, false, false, _crumbs.Count - 1));
                }
                else
                {
                    var menu = (crumbs[..^1], crumbs[^1]);
                    var index = _crumbs.Count;
                    // The current crumb opens the menu instead, being there already.
                    _crumbs.Add((i == last ? () => OpenSiblings(index) : () => State.GoTo(crumbs), menu));
                    items.Add(new(label, "crumb", i == last, false, true, index));
                }
            }
            return items;
        }
    }

    // The last part of a path, or the whole path for the root.
    static string Label(string path) => Path.GetFileName(path) is { Length: > 0 } name ? name : path;

    /// A click on the n-th crumb (CrumbModel.Index), or --test-crumb n.
    public string Crumb(int index)
    {
        _ = Trail;  // the crumbs as shown now
        if (index < 0 || index >= _crumbs.Count)
        {
            return "no such crumb";
        }
        _crumbs[index].Go();
        return "pressed";
    }

    /// Its ▾: the siblings of that crumb, largest first.
    public string CrumbMenu(int index)
    {
        _ = Trail;
        if (index < 0 || index >= _crumbs.Count)
        {
            return "no such crumb";
        }
        if (_crumbs[index].Menu is null)
        {
            return "no menu";
        }
        OpenSiblings(index);
        return "pressed";
    }

    public SiblingsModel Siblings => _siblings;

    // The siblings of a crumb, with a share bar in each one's colour and its
    // size. The menu owns the arrows, Enter and Escape while open; any other
    // key closes it and goes on to its binding.
    void OpenSiblings(int crumb)
    {
        if (_crumbs[crumb].Menu is not var (parent, current) || State.Resolve(parent) is not { } node)
        {
            return;
        }
        var rows = node.Children.Select((child, index) => (Child: child, Index: index, Value: child.Value(State.Metric)))
            .OrderByDescending(row => row.Value).ToList();
        var more = Math.Max(0, rows.Count - SiblingRows);
        rows = [.. rows.Take(SiblingRows)];
        var largest = Math.Max(1, rows.Count > 0 ? rows[0].Value : 1);
        _siblingParent = parent;
        _siblingIndices = [.. rows.Select(row => row.Index)];
        _siblings = new SiblingsModel(true, crumb, $"Siblings in {(parent.Length == 0 ? State.Tilde(State.Root) : node.Name)}",
            [.. rows.Select(row => new SiblingRow(row.Child.Name, row.Child.IsDir, (double)row.Value / largest,
                State.Metric == Metric.Bytes ? Look.HumanBytes(row.Value) : Look.HumanCount(row.Value),
                Look.Accent(row.Child.Category).Qml, row.Index == current))],
            Math.Max(0, rows.FindIndex(row => row.Index == current)), more > 0 ? $"+{more} smaller" : "");
        Keys.Release(SiblingKeys);
        Keys.Grab(SiblingKeys);
        Touch(Part.Overlay);
        Log($"siblings of [{string.Join(',', parent)}]: {string.Join(", ", rows.Take(5).Select(row => $"{row.Child.Name} {row.Value}"))}{(rows.Count > 5 ? ", …" : "")}; +{more} smaller; highlighted {_siblings.Highlighted}");
    }

    bool SiblingKeys(Key key)
    {
        var highlighted = _siblings.Highlighted;
        var count = _siblings.Rows.Count;
        switch (key.Name)
        {
            case "Down" or "j":
                highlighted = Math.Min(highlighted + 1, count - 1);
                break;
            case "Up" or "k":
                highlighted = Math.Max(highlighted - 1, 0);
                break;
            case "Home":
                highlighted = 0;
                break;
            case "End":
                highlighted = count - 1;
                break;
            case "Return" or "space" or "Right" or "l":
                if (count > 0)
                {
                    PickSibling(highlighted);
                }
                return true;
            case "Escape" or "Left" or "h":
                CloseSiblings();
                return true;
            default:
                CloseSiblings();
                return false;
        }
        _siblings = _siblings with { Highlighted = highlighted };
        Touch(Part.Overlay);
        return true;
    }

    public void CloseSiblings()
    {
        if (_siblings.Visible)
        {
            _siblings = _siblings with { Visible = false };
            Keys.Release(SiblingKeys);
            Touch(Part.Overlay);
        }
    }

    /// Into a sibling directory, or beside a file, selected.
    public void PickSibling(int row)
    {
        if (!_siblings.Visible || row < 0 || row >= _siblingIndices.Count)
        {
            return;
        }
        var parent = _siblingParent;
        int[] crumbs = [.. parent, _siblingIndices[row]];
        CloseSiblings();
        if (State.Resolve(crumbs) is { IsDir: true })
        {
            State.GoTo(crumbs);
        }
        else
        {
            State.GoTo(parent);
            State.Select(crumbs);
        }
    }

    /// Scan from `above`, a directory holding the scanned root. The view
    /// stays until the wider tree lands, which opens with the directory you
    /// came from selected.
    // ponytail: a full rescan of `above`, though the subtree under the old
    // root is already measured. Memoize in Scanner when widening feels slow.
    void Widen(string above)
    {
        var from = State.Root;
        if (State.Tree is null || from == above || !Space.PathStartsWith(from, above))
        {
            return;
        }
        _widening = above;
        Start(above, tree =>
        {
            _widening = null;
            if (tree is { } wider)
            {
                var top = Space.Components(Space.StripPrefix(from, above) ?? "") is [var first, ..] ? first : "";
                var index = wider.Children.IndexOf(top);
                if (index >= 0)
                {
                    State.Select([index]);
                }
            }
            Touch(Part.Header);
        });
        Touch(Part.Header);
    }

    public TotalsModel Totals => State.Tree is { } tree
        ? new(Look.HumanBytes(tree.Bytes), $"· {Look.HumanCount(tree.Files)} files · {Look.HumanCount(tree.Dirs)} dirs",
            Unreadable > 0 ? $"· {Look.HumanCount(Unreadable)} unreadable" : "")
        : new("", "", "");
}
