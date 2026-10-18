# Panels

- `Panel.qml` follows Omarchy's own panels: an overlay that takes the keyboard; Escape or a click outside closes it. Keep every action reachable from the keyboard: the starter's `Button`s are `focusable` (Tab, Return), and Return/Ctrl+R are wired on the panel.
- **An installed panel's backend runs only while the panel is open.** Closing it (Escape, a click outside, `shell.hide`) ends the C# process, and the next open starts it fresh. To keep state, save it to disk (for example under `~/.local/state/<id>/`), or add `"keepLoaded": true` to `plugin/manifest.json`: the backend then starts with omarchy-shell and runs for the whole session.
- Calling a backend method while the backend isn't running returns a rejected Promise; check `backend.ready` before acting on input. `backend.running`, `backend.lastError` and `backend.restart()` are there for showing and recovering from a crash.
- Every method call returns a Promise, even for synchronous C# methods: `backend.add(2, 3).then(v => ...)`.
- `omarchy-shell shell toggle <appid>` opens (or closes) an installed panel; that's what to bind a key to, if the user asks for one.
- omarchy-shell may keep running the previous QML of an already-loaded panel after a reinstall. If changes don't show, `omarchy-shell shell rescanPlugins`; if they still don't, they will after the next shell restart.
- Only the `panel` plugin kind is supported. Bar widgets (`bar-widget`) are not yet: don't improvise one, tell the user.
- `Drawing`/`DrawView` are for window apps; in panels, use QML items.
- Colors, fonts and the shell's controls: [look.md](look.md).
