// Back and forward through the directories visited: `<` and `>` beside the
// Size / Files / Age switch, alt ← and alt →, and the mouse's side buttons.
// Kept as paths, which outlive a new tree's crumbs, and pruned to what the
// tree still holds.
namespace Disktiles;

/// A history button: whether it goes anywhere, and its tooltip.
sealed record StepModel(bool Enabled, string Tooltip);

sealed partial class App
{
    const int HistoryDepth = 100;
    readonly List<string> _back = [], _forward = [];
    // The folder on screen, to remember when it is left; null right after a
    // new tree, whose first folder is no departure.
    string? _here;
    bool _stepping;

    void InitHistory()
    {
        Keys.Bind("alt+Left", "Navigate", "Back or forward, as do the mouse's side buttons", () => Step(back: true));
        Keys.Bind("alt+Right", "Navigate", "Back or forward, as do the mouse's side buttons", () => Step(back: false));
        State.TreeChanged += () =>
        {
            _back.RemoveAll(path => State.CrumbsOf(path) is null);
            _forward.RemoveAll(path => State.CrumbsOf(path) is null);
            _here = null;
        };
        State.FolderChanged += Remember;
    }

    // A departure: the folder left goes on the back stack, and whatever was
    // ahead is gone, as in a browser. A step of our own keeps the stacks.
    void Remember()
    {
        var now = State.Tree is null ? null : State.PathOf(State.Crumbs);
        if (!_stepping && _here is not null && now != _here)
        {
            if (_back.Count == 0 || _back[^1] != _here)
            {
                _back.Add(_here);
            }
            if (_back.Count > HistoryDepth)
            {
                _back.RemoveAt(0);
            }
            _forward.Clear();
        }
        _here = now;
        Touch(Part.Header);
    }

    public StepModel BackButton => StepButton(back: true, "Back · alt ← · side button");
    public StepModel ForwardButton => StepButton(back: false, "Forward · alt → · side button");

    StepModel StepButton(bool back, string keys) => StepTarget(back) is { } t
        ? new StepModel(true, $"{State.Tilde(State.PathOf(t.Crumbs))}\n{keys}")
        : new StepModel(false, keys);

    /// Where back (or forward) goes, and its place on that stack: the newest entry
    /// that is somewhere else, skipping any that is where we already are.
    (int At, int[] Crumbs)? StepTarget(bool back)
    {
        var stack = back ? _back : _forward;
        for (var at = stack.Count - 1; at >= 0; at--)
        {
            if (State.CrumbsOf(stack[at]) is { } crumbs && !crumbs.AsSpan().SequenceEqual(State.Crumbs))
            {
                return (at, crumbs);
            }
        }
        return null;
    }

    public bool Step(bool back)
    {
        if (State.Screen != Screen.Explore || _here is null || StepTarget(back) is not var (at, crumbs))
        {
            return false;
        }
        var (from, to) = back ? (_back, _forward) : (_forward, _back);
        from.RemoveRange(at, from.Count - at);
        to.Add(_here);
        _stepping = true;
        try
        {
            State.GoTo(crumbs);
        }
        finally
        {
            _stepping = false;
        }
        return true;
    }
}
