// Back and forward through the directories visited: `<` and `>` beside the
// Size / Files / Age switch, alt ← and alt →, and the mouse's side buttons.
// Kept as paths, which outlive a new tree's crumbs, and pruned to what the
// tree still holds.
namespace Disktiles;

sealed class History
{
    const int Depth = 100;
    readonly AppState _state;
    readonly List<string> _back = [], _forward = [];
    readonly Gtk.Button _backButton, _forwardButton;
    // The folder on screen, to remember when it is left; null right after a
    // new tree, whose first folder is no departure.
    string? _here;
    bool _stepping;

    public static void Install(App app)
    {
        var history = new History(app);
        app.Keys.Bind("alt+Left", "Navigate", "Back or forward, as do the mouse's side buttons", () => history.Step(back: true));
        app.Keys.Bind("alt+Right", "Navigate", "Back or forward, as do the mouse's side buttons", () => history.Step(back: false));
        app.Treemap.Clicked += (_, button) =>
        {
            if (button is 8 or 9 && app.State.Screen == Screen.Explore)
            {
                history.Step(back: button == 8);
            }
        };
    }

    History(App app)
    {
        _state = app.State;
        _backButton = Button("<", back: true);
        _forwardButton = Button(">", back: false);
        var box = Gtk.Box.New(Gtk.Orientation.Horizontal, 0);
        box.Valign = Gtk.Align.Center;
        box.Append(_backButton);
        box.Append(_forwardButton);
        app.Header.End.Append(box);

        _state.TreeChanged += () =>
        {
            _back.RemoveAll(path => _state.CrumbsOf(path) is null);
            _forward.RemoveAll(path => _state.CrumbsOf(path) is null);
            _here = null;
        };
        _state.FolderChanged += Remember;
        Remember();
    }

    Gtk.Button Button(string label, bool back)
    {
        var button = Gtk.Button.NewWithLabel(label);
        button.AddCssClass("flat");
        button.FocusOnClick = false;
        button.Sensitive = false;
        button.OnClicked += (_, _) => Step(back);
        return button;
    }

    // A departure: the folder left goes on the back stack, and whatever was
    // ahead is gone, as in a browser. A step of our own keeps the stacks.
    void Remember()
    {
        var now = _state.Tree is null ? null : _state.PathOf(_state.Crumbs);
        if (!_stepping && _here is not null && now != _here)
        {
            if (_back.Count == 0 || _back[^1] != _here)
            {
                _back.Add(_here);
            }
            if (_back.Count > Depth)
            {
                _back.RemoveAt(0);
            }
            _forward.Clear();
        }
        _here = now;
        Refresh(_backButton, Target(back: true), "Back · alt ← · side button");
        Refresh(_forwardButton, Target(back: false), "Forward · alt → · side button");
    }

    void Refresh(Gtk.Button button, (int At, int[] Crumbs)? target, string keys)
    {
        button.Sensitive = target is not null;
        button.TooltipText = target is { } t ? $"{_state.Tilde(_state.PathOf(t.Crumbs))}\n{keys}" : keys;
    }

    /// Where back (or forward) goes, and its place on that stack: the newest entry
    /// that is somewhere else, skipping any that is where we already are.
    (int At, int[] Crumbs)? Target(bool back)
    {
        var stack = back ? _back : _forward;
        for (var at = stack.Count - 1; at >= 0; at--)
        {
            if (_state.CrumbsOf(stack[at]) is { } crumbs && !crumbs.AsSpan().SequenceEqual(_state.Crumbs))
            {
                return (at, crumbs);
            }
        }
        return null;
    }

    bool Step(bool back)
    {
        if (_here is null || Target(back) is not var (at, crumbs))
        {
            return false;
        }
        var (from, to) = back ? (_back, _forward) : (_forward, _back);
        from.RemoveRange(at, from.Count - at);
        to.Add(_here);
        _stepping = true;
        try
        {
            _state.GoTo(crumbs);
        }
        finally
        {
            _stepping = false;
        }
        return true;
    }
}
