// The one key dispatcher: features bind keys here with a description and a
// group, so a "?" overlay can list them all, and the test hook DISKTILES_KEYS
// replays presses through the same path.
//
// A key is named by its GDK keyval name with the modifiers in front:
// "Escape", "BackSpace", "Return", "question", "bracketleft", "ctrl+o",
// "alt+Left", "shift+Tab". Shift is part of the name only for keys that type
// nothing: shift+a is "A", shift+/ is "question".
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

    public bool Press(uint keyval, Gdk.ModifierType modifiers) => Press(Describe(keyval, modifiers));

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

    public static Key Describe(uint keyval, Gdk.ModifierType modifiers)
    {
        if (keyval == Gdk.Constants.KEY_ISO_Left_Tab)
        {
            keyval = Gdk.Constants.KEY_Tab;
            modifiers |= Gdk.ModifierType.ShiftMask;
        }
        else if (keyval == Gdk.Constants.KEY_KP_Enter)
        {
            keyval = Gdk.Constants.KEY_Return;
        }
        var unicode = Gdk.Functions.KeyvalToUnicode(keyval);
        var control = modifiers.HasFlag(Gdk.ModifierType.ControlMask);
        var alt = modifiers.HasFlag(Gdk.ModifierType.AltMask);
        var name = (control ? "ctrl+" : "") + (alt ? "alt+" : "")
            + (modifiers.HasFlag(Gdk.ModifierType.SuperMask) ? "super+" : "")
            // Tab and Return map to control characters: they type nothing too.
            + (modifiers.HasFlag(Gdk.ModifierType.ShiftMask) && unicode < 0x20 ? "shift+" : "")
            + Gdk.Functions.KeyvalName(keyval);
        var text = unicode >= 0x20 && !control && !alt ? char.ConvertFromUtf32((int)unicode) : null;
        return new Key(name, text);
    }

    /// "ctrl+o" back into a keyval and modifiers, as GTK would deliver it;
    /// null for a name GDK does not know.
    public static (uint Keyval, Gdk.ModifierType Modifiers)? Parse(string chord)
    {
        var parts = chord.Split('+');
        var modifiers = Gdk.ModifierType.NoModifierMask;
        foreach (var part in parts[..^1])
        {
            modifiers |= part switch
            {
                "ctrl" => Gdk.ModifierType.ControlMask,
                "alt" => Gdk.ModifierType.AltMask,
                "super" => Gdk.ModifierType.SuperMask,
                "shift" => Gdk.ModifierType.ShiftMask,
                _ => Gdk.ModifierType.NoModifierMask,
            };
        }
        var keyval = Gdk.Functions.KeyvalFromName(parts[^1]);
        return keyval is 0 or Gdk.Constants.KEY_VoidSymbol ? null : (keyval, modifiers);
    }
}
