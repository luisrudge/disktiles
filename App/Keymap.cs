// The one key dispatcher: features bind keys here with a description and a
// group, so the "?" overlay can list them all, and the test hook --test-keys
// replays presses through the same path. QML hands every key press over as
// Qt delivers it (QtKeys.Describe names it).
//
// A key is named by its GDK keyval name with the modifiers in front, as the
// GTK app named them: "Escape", "BackSpace", "Return", "question",
// "bracketleft", "ctrl+o", "alt+Left", "shift+Tab". Shift is part of the name
// only for keys that type nothing: shift+a is "A", shift+/ is "question".
namespace Disktiles;

/// A press: its name as above, and the character it types, if any.
readonly record struct Key(string Name, string? Text);

/// `Keys` holds alternatives separated by spaces: "BackSpace u".
sealed record Binding(string Keys, string Group, string Description, Screen? On, int Priority, Func<bool> Action)
{
    public readonly string[] Alternatives = Keys.Split(' ', StringSplitOptions.RemoveEmptyEntries);
}

sealed class Keymap(AppState state)
{
    readonly List<Binding> _bindings = [];
    readonly List<Func<Key, bool>> _grabs = [];

    /// Highest priority first, then in the order they were bound.
    public IReadOnlyList<Binding> Bindings => _bindings;

    /// `action` returns whether it took the key; false lets the next binding
    /// for the same key try (Escape: clear the filter, else the selection,
    /// else go up). `on` is the screen it works on, null for every screen.
    public void Bind(string keys, string group, string description, Func<bool> action,
        Screen? on = Screen.Explore, int priority = 0)
    {
        var binding = new Binding(keys, group, description, on, priority, action);
        var at = _bindings.FindIndex(b => b.Priority < priority);
        _bindings.Insert(at < 0 ? _bindings.Count : at, binding);
    }

    public void Bind(string keys, string group, string description, Action action,
        Screen? on = Screen.Explore, int priority = 0) =>
        Bind(keys, group, description, () => { action(); return true; }, on, priority);

    /// A modal (a dialog, the help overlay, a text field) sees every key
    /// before the bindings, the last grab first, until it is released.
    public void Grab(Func<Key, bool> handler) => _grabs.Add(handler);

    public void Release(Func<Key, bool> handler) => _grabs.Remove(handler);

    public bool Press(Key key)
    {
        for (var i = _grabs.Count - 1; i >= 0; i--)
        {
            if (_grabs[i](key))
            {
                return true;
            }
        }
        foreach (var binding in _bindings)
        {
            if ((binding.On is null || binding.On == state.Screen) && binding.Alternatives.Contains(key.Name) && binding.Action())
            {
                return true;
            }
        }
        return false;
    }

    /// Whether some binding for the Explore screen (or every screen) has `key`.
    public bool Bound(string key) => _bindings.Any(b => b.On is null or Screen.Explore && b.Alternatives.Contains(key));
}

/// Qt's key events, named the way the keymap names keys.
static class QtKeys
{
    // Qt::KeyboardModifier.
    public const int Shift = 0x02000000, Control = 0x04000000, Alt = 0x08000000, Meta = 0x10000000, Keypad = 0x20000000;

    static readonly Dictionary<int, string> Special = new()
    {
        [0x01000000] = "Escape", [0x01000001] = "Tab", [0x01000003] = "BackSpace", [0x01000004] = "Return",
        [0x01000005] = "Return", [0x01000006] = "Insert", [0x01000007] = "Delete", [0x01000010] = "Home",
        [0x01000011] = "End", [0x01000012] = "Left", [0x01000013] = "Up", [0x01000014] = "Right",
        [0x01000015] = "Down", [0x01000016] = "Page_Up", [0x01000017] = "Page_Down",
    };

    static readonly Dictionary<char, string> Punctuation = new()
    {
        [' '] = "space", ['!'] = "exclam", ['"'] = "quotedbl", ['#'] = "numbersign", ['$'] = "dollar",
        ['%'] = "percent", ['&'] = "ampersand", ['\''] = "apostrophe", ['('] = "parenleft", [')'] = "parenright",
        ['*'] = "asterisk", ['+'] = "plus", [','] = "comma", ['-'] = "minus", ['.'] = "period", ['/'] = "slash",
        [':'] = "colon", [';'] = "semicolon", ['<'] = "less", ['='] = "equal", ['>'] = "greater",
        ['?'] = "question", ['@'] = "at", ['['] = "bracketleft", ['\\'] = "backslash", [']'] = "bracketright",
        ['^'] = "asciicircum", ['_'] = "underscore", ['`'] = "grave", ['{'] = "braceleft", ['|'] = "bar",
        ['}'] = "braceright", ['~'] = "asciitilde",
    };

    static readonly Dictionary<char, string> KeypadNames = new()
    {
        ['+'] = "KP_Add", ['-'] = "KP_Subtract", ['*'] = "KP_Multiply", ['/'] = "KP_Divide",
    };

    /// The keymap's name for a Qt key event (QKeyEvent::key, modifiers, text);
    /// null for a modifier pressed on its own.
    public static Key? Describe(int key, int modifiers, string? text)
    {
        var control = (modifiers & Control) != 0;
        var alt = (modifiers & Alt) != 0;
        var shift = (modifiers & Shift) != 0;
        var prefix = (control ? "ctrl+" : "") + (alt ? "alt+" : "") + ((modifiers & Meta) != 0 ? "super+" : "");
        if (key == 0x01000002)
        {
            // Backtab: shift+Tab.
            return new Key(prefix + "shift+Tab", null);
        }
        if (Special.TryGetValue(key, out var special))
        {
            return new Key(prefix + (shift ? "shift+" : "") + special, null);
        }
        if (key >= 0x01000030 && key <= 0x01000052)
        {
            return new Key(prefix + (shift ? "shift+" : "") + $"F{key - 0x01000030 + 1}", null);
        }
        if (key >= 0x01000000)
        {
            return null;  // Shift, Control, Alt, Meta and the like on their own
        }
        var typed = !control && !alt && text is { Length: > 0 } && !char.IsControl(text[0]) ? text : null;
        // What the key types, or with ctrl/alt (which type nothing), the key itself:
        // Qt's key codes for letters are upper case, the rest are their ASCII.
        char c;
        if (typed is not null && typed.Length == 1)
        {
            c = typed[0];
        }
        else if (typed is not null)
        {
            return new Key(prefix + typed, typed);
        }
        else if (key is >= 'A' and <= 'Z')
        {
            c = shift ? (char)key : char.ToLowerInvariant((char)key);
        }
        else if (key < 0x80)
        {
            c = (char)key;
        }
        else
        {
            c = char.ConvertFromUtf32(key)[0];
        }
        string name;
        if ((modifiers & Keypad) != 0 && (char.IsAsciiDigit(c) || KeypadNames.ContainsKey(c)))
        {
            name = char.IsAsciiDigit(c) ? $"KP_{c}" : KeypadNames[c];
        }
        else
        {
            name = Punctuation.TryGetValue(c, out var punct) ? punct : c.ToString();
        }
        return new Key(prefix + name, typed);
    }

    /// "ctrl+o" back into a press, as QML would deliver it: for --test-keys.
    public static Key Parse(string chord)
    {
        var parts = chord.Split('+');
        var name = parts[^1];
        var prefix = string.Join("", parts[..^1].Select(p => p + "+"));
        var single = Punctuation.FirstOrDefault(kv => kv.Value == name).Key;
        string? text = prefix.Length > 0 || prefix.Contains("shift") ? null
            : name.Length == 1 ? name
            : single != default ? single.ToString()
            : null;
        return new Key(chord, text);
    }
}
