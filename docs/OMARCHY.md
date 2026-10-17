# disktiles on Omarchy and Hyprland

What Omarchy v4 runs, how disktiles fits into it, and what is still open.
Checked against the `omarchy` package 4.0.4-1, Hyprland 0.56.2 and GTK 4.22.4.
Code: `Theme.cs`, `Look.cs`, `Omarchy.cs`, `Footer.cs` in `src/Disktiles/`, and
`packaging/install.sh`.

## What Omarchy v4 uses

| Part | What it is in Omarchy v4 | What disktiles does with it |
|---|---|---|
| Theme | `~/.local/state/omarchy/current/theme/` (a real directory, not a symlink) with `colors.toml`, plus `current/theme.name`. `~/.config/omarchy/current` no longer exists. | Reads both files (below) and follows switches live. |
| Theme switch | `omarchy theme set` stages `current/next-theme`, `rm -rf current/theme`, `mv next-theme theme`, writes `theme.name`, then runs hooks (`omarchy-theme-set-gnome` sets `color-scheme` and `gtk-theme` Adwaita / Adwaita-dark and the icon theme through gsettings). The shell gets the palette over IPC. User hooks go in `~/.config/omarchy/hooks/theme-set.d`. | Watches `current/` (FileSystemWatcher: inotify on Linux), 200 ms debounce, reloads, and repaints only when the theme differs. No hook needs installing. |
| Shell | omarchy-shell: Quickshell (`quickshell -n -p /usr/share/omarchy/shell`) draws the bar, launcher, menus, OSD, lock screen and notifications. Its colours come from `current/theme/shell.toml`, which is derived from `colors.toml`. | Nothing to integrate: the app reads the same `colors.toml`, so it matches the shell. |
| Launcher | The shell's app library lists desktop entries and launches with `uwsm-app -- gtk-launch <id>.desktop`, then waits for a toplevel to clear its launch OSD. Icons come from the icon theme (`omarchy-tui-install` puts them in `~/.local/share/icons/hicolor/…` and runs `gtk-update-icon-cache`). | `packaging/install.sh` puts `disktiles` in `~/.local/bin` and writes `~/.local/share/applications/io.github.luisrudge.disktiles.desktop` and a hicolor scalable icon. |
| Notifications | `org.freedesktop.Notifications` is owned by quickshell. Omarchy scripts send them with `omarchy-notification-send`. | None sent (see "Open"). |
| Portals | xdg-desktop-portal 1.22 with `-hyprland` (screencast etc.) and `-gtk` (FileChooser). `hyprland-portals.conf`: `default=hyprland;gtkCommons`. Omarchy floats every `xdg-desktop-portal-gtk` window (`default/hypr/apps/system.lua`). `omarchy-file-select` is its own portal client. | GTK 4.22's `Gtk.FileDialog` uses the FileChooser portal whenever it is on the session bus (`gtkfilechoosernativeportal.c`: `gdk_display_should_use_portal`), so ctrl+o (feature 3) should call `Gtk.FileDialog.SelectFolder`. The dialog then appears as Omarchy's floating portal window. On Windows and macOS the same call opens the native dialog. Nothing Omarchy-specific is needed. |
| File manager | Nautilus 50 (`xdg-mime query default inode/directory` → `org.gnome.Nautilus.desktop`), started with `omarchy-launch-nautilus`. | `o` and right-click reveal the tile through GTK's file launcher (`FileManager.cs`): the portal's OpenDirectory, which opens Nautilus here with the file selected, and the default file manager elsewhere. |
| Terminal | `xdg-terminal-exec` (`~/.config/xdg-terminals.list`): Alacritty here; ghostty, foot and kitty are supported. | Unused. |
| Fonts | `omarchy font set` writes the fontconfig `monospace` family (`omarchy-font-current` = `fc-match monospace`). GTK's UI font: `Adwaita Sans 11`. | Keycaps use CSS `font-family: monospace`, which resolves through fontconfig to Omarchy's chosen font. Body text keeps GTK's UI font. |
| Hyprland config | Lua: `~/.config/hypr/hyprland.lua` requires `default.hypr.omarchy`, then the user's `hypr.*` files. Window rules use `o.window(match, rules)` → `hl.window_rule`; bindings use `o.bind("SUPER + SHIFT + X", "Name", …)`. Dispatch: `hyprctl dispatch 'hl.dsp.exec_cmd(…)'`. | Snippets below. |
| Scale | HiDPI laptops run at 2x or fractional scales such as 1.25. | The treemap texture is painted at the surface scale and repainted when the scale changes. Checked at 2 and at 1.25 on a headless monitor: GTK reports `scale: 1.25`. |

## Theme

`Theme.cs` reads and follows the Omarchy theme:

- It reads `$HOME/.local/state/omarchy/current`, or `$HOME/.config/omarchy/current`
  only when the state path does not exist at all. A broken current theme must
  not bring back a stale legacy one.
- `theme/colors.toml` must have `background`, `foreground` and `accent`, plus
  `red|color1`, `yellow|color3` and `green|color2`, all as `#RRGGBB`.
  `mode = "dark"|"light"`; without it, the lighter of background and
  foreground decides. Missing roles are derived:
  - surface = `lighter_background`, or 5% toward the foreground
  - inset = `dark_background`, or 8% toward the foreground
  - bright = `bright_foreground`
  - secondary = 75% toward the foreground
  - selection and border = `selection` and `muted`
  - on-accent = black or white, whichever contrasts more
- Any error gives Tokyo Night, never a half-read theme. `theme.name` names it,
  "Omarchy" by default.
- It watches directories, not files, so atomic saves and directory swaps survive.

The TOML is read as flat `key = "value"` lines; Omarchy's palettes are flat.
`Look.cs` builds the palette on top of it:

- category fills: muted HSL per hue, lifted by depth, mixed 12% into the inset
- the category accent: strips, swatches and bars
- the age ramp on the theme accent's hue
- the highlight (the theme's warning amber)
- the hatch (bright at 16% on dark, foreground at 18% on light)
- label colours

All 22 themes that ship with Omarchy 4 load. A live test cycled every one by
swapping a fixture `current/` under `/tmp`, the way `omarchy theme set` does
it, and finished with an in-place `colors.toml` edit. The app reloaded on
every step.

Off Omarchy: macOS, Windows and GNOME, KDE and
similar desktops follow the system's light or dark (Flexoki Light or Tokyo
Night); everything else stays Tokyo Night. The system appearance is read
when the theme loads, not followed live.

GTK side: the theme is set as named colours (`@dt_background`, `@dt_accent`,
`@dt_highlight`, … see `Omarchy.Css`). Every stylesheet uses those names, so
reloading one provider restyles the whole window. On top of that:

- The window sits on the background, the panel on the surface and the
  treemap on the inset; hairlines are the foreground at 12%.
- Controls use the shell's fill and border alphas (normal 4%, hover 8%,
  selected 18%, border 40%). Corners are square.
- `gtk-interface-color-scheme` follows the theme's mode, so GTK's own
  widgets match.
- Cairo-painted parts (the treemap texture, the swatches) repaint once per
  switch, never on hover.

## Window class

The app id, and so the Hyprland window class, is
`io.github.luisrudge.disktiles`, a reverse-DNS id:

1. GApplication requires a dotted id.
2. The desktop entry is named after the id, which is how the shell and the
   portal match a window to its entry and icon.

The title is `disktiles · ~/projects`. The default size is 1440x900.

There is no minimum size. Omarchy tiles new windows, and a half-width tile
on a 1280x800 (logical) screen is 640 wide. A minimum above that makes GTK
draw past the tile.

Not single instance: every launch is its own window (`NonUnique`). GTK picks
up `XDG_ACTIVATION_TOKEN` from the launcher by itself.

## Hyprland rules (Lua, Hyprland 0.56)

Window rules in the syntax Omarchy's own `default/hypr/apps/*.lua` use, for
`~/.config/hypr/hyprland.lua` (below `require("default.hypr.omarchy")`):

```lua
-- disktiles is designed for a roomy window: float it at its size.
o.window("^(io.github.luisrudge.disktiles)$", { float = true })
o.window("^(io.github.luisrudge.disktiles)$", { size = { 1400, 900 } })
o.window("^(io.github.luisrudge.disktiles)$", { center = true })
```

On a 1280x800 (logical) screen, 1400x900 is larger than the screen; there,
tiling (no rule) or a smaller size fits better.

An optional binding, in `~/.config/hypr/bindings.lua`, in the same form as
Omarchy's app bindings. It focuses an open window instead of opening another;
SUPER+SHIFT+K is unbound in Omarchy's defaults:

```lua
o.bind("SUPER + SHIFT + K", "Disktiles", { launch = "~/.local/bin/disktiles", focus = "^io.github.luisrudge.disktiles$" })
```

## Keys

Omarchy keeps SUPER for the compositor and its shell. The app binds plain
keys and ctrl/alt chords only. The footer is the key-hint bar:

- the most useful hints first (space mark · enter open · ⌫ up · c review ·
  hjkl move · / filter · [ ] depth · t mode · 0 reset · v volumes · r rescan)
- then "? all keys" and `scan 257.6k entries · 1.4 s` at the right
- a hint appears once some feature binds its key in the Keymap, so the bar
  never offers a key that does nothing

## Install

```sh
sh packaging/install.sh             # executable in ~/.local/bin, desktop entry, icon
sh packaging/install.sh uninstall
```

The icon is `packaging/disktiles.svg`. The entry passes
`desktop-file-validate`.

## Open

- Notifications: none are sent. If a long scan or a removal should notify,
  `Gtk.Application.SendNotification` reaches the Omarchy shell (through the
  notification portal or `org.freedesktop.Notifications`). It is cross-platform; shelling out to
  `omarchy-notification-send` would not be.
- The treemap still draws labels with Cairo's toy font API ("sans-serif"),
  not Pango with GTK's UI font.
- Font changes (`omarchy font set`) reach the keycaps only after a restart.
