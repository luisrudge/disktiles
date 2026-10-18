// Marking (feature 5): space or x marks the tile you point at (or the
// keyboard selection after an arrow), ctrl-click and middle-click mark
// without moving anything, and a mark covers everything inside it. The
// panel's parts are here; the review, running and done screens in Review.cs.
//
// Marks are absolute paths (Disktiles.Core.Marks), so they survive a rescan.
using Disktiles.Core;

namespace Disktiles;

enum Tone { Neutral, Success, Warning, Error }

/// The selection's one action: "Mark for removal", or inside a marked
/// directory the offer to unmark that one instead; `Chips` its badges.
sealed record MarkButtonModel(bool Visible, string Label, bool Filled, List<string> Chips);

/// A marked path in the panel.
sealed record MarkedRow(string Path, string Tilde, string Size, string Color);

sealed record MarkedModel(string Title, string Total, List<MarkedRow> Rows, string More, string Nested);

/// The last thing marking said; `Status` neutral, success, warning or error.
sealed record NoticeModel(string Text, string Status);

/// Below the disk's numbers: free space once the marks go, counting only
/// what comes back to this volume, what is left out, and the way to the review.
sealed record ProjectionModel(bool Visible, string After, double UsedNow, double UsedAfter, string LeftOut, string Review);

sealed partial class App
{
    /// Marked rows the panel lists before pointing at the review screen.
    const int MarkedRows = 6;

    public readonly Marks Marks = new();
    public TrashBackend Trash { get; private set; } = Removal.DetectTrashBackend();
    /// The trash when there is one: it commits without asking.
    public RemovalMode RemovalMode { get; private set; }
    public (string Text, Tone Tone)? Notice { get; private set; }
    Plan? _plan;
    string? _trashDir;

    void InitMarking()
    {
        // --test-trash-dir: an XDG trash of the test's own, never the user's.
        if (Hook("trash-dir") is { } trash)
        {
            (_trashDir, Trash) = (trash, TrashBackend.XdgHome);
        }
        RemovalMode = Trash.IsAvailable() ? RemovalMode.Trash : RemovalMode.Permanent;
        // Sizes again from every new tree.
        State.TreeChanged += () =>
        {
            _plan = null;
            if (State.Tree is { } tree && Marks.Count > 0)
            {
                Marks.Refresh(MarkRoot, tree);
                MarksChanged();
            }
        };
        Keys.Bind("space x", "Mark", "mark or unmark", () =>
        {
            if (State.Tree is not null && State.ActionTarget is { } target)
            {
                ToggleMark(target);
            }
        });
        Keys.Bind("c", "Mark", "review the marked list", OpenReview);
        InitReview();
    }

    /// The scanned root, absolute and normalized, as marks are keyed.
    public string MarkRoot => Removal.Normalize(State.Root);

    /// What the review shows and the removal runs, planned once per change.
    public Plan Plan => _plan ??= Removal.Plan(Marks.Items, MarkRoot);

    /// Plan again on demand: the mount table may have landed since.
    public void Replan() => _plan = null;

    string PathAt(int[] crumbs) => Removal.Normalize(State.PathOf(crumbs));

    public void SetRemovalMode(RemovalMode mode)
    {
        if (mode == RemovalMode.Trash && !Trash.IsAvailable())
        {
            return;
        }
        RemovalMode = mode;
        CloseDialog();
        Touch(Part.Review);
        Log($"removal mode {mode}");
    }

    public void Say(string text, Tone status)
    {
        Notice = (text, status);
        Touch(Part.Panel | Part.Review);
        Log($"notice {status}: {text}");
    }

    void Quiet()
    {
        if (Notice is not null)
        {
            Notice = null;
            Touch(Part.Panel | Part.Review);
        }
    }

    /// Mark or unmark the node at `crumbs`. Marking a directory absorbs the
    /// marks inside it; inside a marked directory nothing is marked or kept
    /// on its own: the notice says which mark it goes with.
    public void ToggleMark(int[] crumbs)
    {
        if (crumbs.Length == 0)
        {
            Say("the scanned root cannot be removed; open a directory first", Tone.Warning);
            return;
        }
        if (State.Resolve(crumbs) is not { } node)
        {
            return;
        }
        var path = PathAt(crumbs);
        Quiet();
        if (!Marks.Contains(path) && MarkedAncestor(path) is { } ancestor)
        {
            Say($"{State.Tilde(path)} goes with the marked {State.Tilde(ancestor)}; unmark that to keep it", Tone.Warning);
            return;
        }
        if (Marks.Toggle(new Target(path, node.Bytes, node.IsDir, Marks.IsHidden(path))))
        {
            var inside = Marks.Items.Where(item => item.Path != path && Space.PathStartsWith(item.Path, path))
                .Select(item => item.Path).ToList();
            foreach (var inner in inside)
            {
                Marks.Remove(inner);
            }
            if (inside.Count > 0)
            {
                Say($"{State.Tilde(path)} now covers {inside.Count} mark{(inside.Count == 1 ? "" : "s")} inside it", Tone.Neutral);
            }
        }
        MarksChanged();
    }

    /// The outermost marked directory `path` is inside; never `path` itself.
    public string? MarkedAncestor(string path) =>
        Marks.Items.Where(item => item.Path != path && Space.PathStartsWith(path, item.Path))
            .Select(item => item.Path).MinBy(ancestor => ancestor.Length);

    public void Unmark(string path)
    {
        Marks.Remove(path);
        MarksChanged();
    }

    public void UnmarkAll()
    {
        Marks.Clear();
        Quiet();
        MarksChanged();
    }

    /// The selection's button: unmark the marked directory it is inside, or toggle it.
    public void MarkSelection()
    {
        if (_selectionShown is not { } at || at.SequenceEqual(State.Crumbs))
        {
            return;
        }
        if (MarkedAncestor(PathAt(at)) is { } ancestor && !Marks.Contains(PathAt(at)))
        {
            Unmark(ancestor);
        }
        else
        {
            ToggleMark(at);
        }
    }

    // Paint, panel and review follow; a scan landing or a mark changing
    // under the dialog changes what would run, so it closes.
    void MarksChanged()
    {
        _plan = null;
        CloseDialog();
        Repaint();
        Touch(Part.Panel | Part.Selection | Part.Review);
        Log($"marks [{string.Join(", ", Marks.Items.Select(item => $"{item.Path} {item.Bytes}"))}], plan {Plan.Targets.Count} targets, {Plan.Blocked.Count} blocked ({string.Join("; ", Plan.Blocked.Select(b => b.Reason))}), reclaim {Plan.Reclaim}");
    }

    /// The hover card's badge for `path`: "Marked for removal", or "Inside
    /// marked X". Null when neither.
    string? Badge(string path) => Marks.Contains(path) ? "Marked for removal"
        : MarkedAncestor(path) is { } ancestor ? $"Inside marked {Path.GetFileName(ancestor)}"
        : null;

    /// The marked nodes' crumbs, joined, for painting.
    HashSet<string> MarkedCrumbs()
    {
        var marked = new HashSet<string>();
        if (State.Tree is not { } tree)
        {
            return marked;
        }
        foreach (var item in Marks.Items)
        {
            if (Marks.CrumbsFor(MarkRoot, tree, item.Path) is { } crumbs)
            {
                marked.Add(string.Join(',', crumbs));
            }
        }
        return marked;
    }

    MarkButtonModel MarkButton(int[]? shown)
    {
        if (shown is null || shown.SequenceEqual(State.Crumbs) || State.Resolve(shown) is null)
        {
            return new(false, "", false, []);
        }
        var path = PathAt(shown);
        var marked = Marks.Contains(path);
        var ancestor = marked ? null : MarkedAncestor(path);
        var chips = new List<string>();
        if (marked)
        {
            chips.Add("Marked");
        }
        if (ancestor is not null)
        {
            chips.Add($"Goes with {Path.GetFileName(ancestor)}");
        }
        var label = ancestor is not null ? $"Unmark {Path.GetFileName(ancestor)}" : marked ? "Unmark" : "Mark for removal";
        // Marking is what this tool is for: the filled button, unless it undoes.
        return new(true, label, !marked && ancestor is null, chips);
    }

    public MarkedModel Marked
    {
        get
        {
            var count = Marks.Count;
            if (count == 0)
            {
                return new("MARKED", "", [], "", "");
            }
            var plan = Plan;
            var rows = Marks.Items.Take(MarkedRows).Select(item =>
            {
                var category = State.Tree is { } tree && Marks.Find(MarkRoot, tree, item.Path) is { } node ? node.Category : Category.Other;
                return new MarkedRow(item.Path, State.Tilde(item.Path), Look.HumanBytes(item.Bytes), Look.Accent(category).Qml);
            }).ToList();
            return new($"MARKED · {count}", Look.HumanBytes(plan.Bytes), rows,
                count > MarkedRows ? $"+{count - MarkedRows} more on the review screen" : "",
                plan.Covered.Count > 0 || plan.Blocked.Count > 0 ? $"{plan.Covered.Count} nested · {plan.Blocked.Count} kept back" : "");
        }
    }

    public NoticeModel? NoticeLine => Notice is var (text, status) ? new(text, status.ToString().ToLowerInvariant()) : null;

    public ProjectionModel Projection
    {
        get
        {
            if (Marks.Count == 0)
            {
                return new(false, "", 0, 0, "", "");
            }
            var plan = Plan;
            var space = SpaceInfo.Of(State.Root);
            var reclaiming = plan.Reclaim;
            var (after, usedNow, usedAfter) = space is { Total: > 0 } volume && reclaiming > 0
                ? ($"→ {Look.HumanBytes(volume.AfterRemoving(reclaiming).Available)} free", (double)volume.Used / volume.Total,
                    Math.Min((double)volume.AfterRemoving(reclaiming).Used / volume.Total, (double)volume.Used / volume.Total))
                : ("", 0.0, 0.0);
            return new(true, after, usedNow, usedAfter, NotCounted(plan) is { } leftOut ? $"not counted here: {leftOut}" : "",
                $"Review {Marks.Count} marked · frees {Look.HumanBytes(reclaiming)}…");
        }
    }

    /// What the meter leaves out: bytes marked on another volume come back
    /// there, and bytes no volume can be found for are not claimed at all.
    public static string? NotCounted(Plan plan)
    {
        var parts = new List<string>();
        if (plan.Foreign > 0)
        {
            parts.Add($"{Look.HumanBytes(plan.Foreign)} on other volumes");
        }
        if (plan.Unattributed > 0)
        {
            parts.Add($"{Look.HumanBytes(plan.Unattributed)} could not be placed");
        }
        return parts.Count == 0 ? null : string.Join(", ", parts);
    }
}
