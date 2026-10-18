#!/usr/bin/env python3
# keys.py KEYS: type KEYS with wtype. Plain text is typed as is; {Key} and {Mod+Key} press keys:
#   {Tab} {Enter} {Esc} {Up} {Down} {Left} {Right} {Backspace} {Delete} {Home} {End} {Space} {F5}
#   {Ctrl+a} {Ctrl+Shift+z} {Alt+1} {Super+Return}       {Wait:500} pauses 500 ms       {{ types a literal {
import os, re, sys

ALIASES = {"enter": "Return", "return": "Return", "esc": "Escape", "escape": "Escape", "tab": "Tab",
           "backspace": "BackSpace", "del": "Delete", "delete": "Delete", "space": "space",
           "up": "Up", "down": "Down", "left": "Left", "right": "Right", "home": "Home", "end": "End",
           "pgup": "Prior", "pageup": "Prior", "pgdn": "Next", "pagedown": "Next", "insert": "Insert"}
MODS = {"ctrl": "ctrl", "control": "ctrl", "shift": "shift", "alt": "alt", "super": "logo", "logo": "logo", "win": "logo"}

def text_args(text):
    # wtype would read text starting with '-' as an option: press leading dashes as keys.
    out = []
    while text.startswith("-"): out += ["-k", "minus"]; text = text[1:]
    return out + ([text] if text else [])

def args_for(keys):
    out, text, i = [], "", 0
    while i < len(keys):
        if keys.startswith("{{", i): text += "{"; i += 2; continue
        w = re.match(r"\{Wait:(\d+)\}", keys[i:], re.I)
        if w:
            if text: out += text_args(text); text = ""
            out += ["-s", w.group(1)]; i += len(w.group(0)); continue
        m = re.match(r"\{([A-Za-z0-9_]+(?:\+[A-Za-z0-9_]+)*)\}", keys[i:])
        if not m: text += keys[i]; i += 1; continue
        if text: out += text_args(text); text = ""
        parts = m.group(1).split("+")
        mods, key = [MODS.get(p.lower()) for p in parts[:-1]], parts[-1]
        if None in mods: sys.exit(f"keys: unknown modifier in {m.group(0)} (use Ctrl, Shift, Alt, Super)")
        key = ALIASES.get(key.lower(), key)
        for mod in mods: out += ["-M", mod]
        out += ["-k", key]
        for mod in reversed(mods): out += ["-m", mod]
        i += len(m.group(0))
    if text: out += text_args(text)
    return out

# -s 300: give the app time to load wtype's keymap first (otherwise the first key can arrive as Escape).
os.execvp("wtype", ["wtype", "-s", "300", "-d", "30"] + args_for(sys.argv[1]))
