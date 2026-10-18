// Below the treemap: key hints, most useful first so a narrow window clips
// the least useful, then "? all keys", the zoom scale and the status line.
namespace Disktiles;

/// A keycap and what it does.
sealed record Hint(string Cap, string Label);

sealed partial class App
{
    // The keycap, what it does, and the key it stands for in the keymap. A
    // hint shows only once some feature binds its key.
    static readonly (string Cap, string Label, string Key)[] KeyBar =
    [
        ("space", "mark", "space"), ("enter", "open", "Return"), ("⌫", "up", "BackSpace"),
        ("c", "review", "c"), ("hjkl", "move", "h"), ("/", "filter", "slash"),
        ("[ ]", "depth", "bracketleft"), ("t", "mode", "t"), ("0", "reset", "0"),
        ("v", "volumes", "v"), ("r", "rescan", "r"),
    ];

    public List<Hint> Hints =>
    [
        .. KeyBar.Where(hint => Keys.Bound(hint.Key)).Select(hint => new Hint(hint.Cap, hint.Label)),
        .. Keys.Bound("question") ? [new Hint("?", "all keys")] : Array.Empty<Hint>(),
    ];
}
