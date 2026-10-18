# Checking your work (without touching the user's screen)

```sh
sh .agents/skills/omasharp/scripts/check.sh                            # tests, dev build, screenshot -> obj/shot.png
sh .agents/skills/omasharp/scripts/check.sh --keys "86.40{Tab}3{Enter}" # type into the app first, then screenshot
sh .agents/skills/omasharp/scripts/check.sh --release                  # the same with the release (native AOT) build
```

Run it in the app's folder (or pass the folder as the last argument). `--help` lists every option.

- It needs the user's Hyprland session to be running: its own compositor runs on a hidden workspace there; no monitor is added. Nothing appears on the user's screen, and typed keys and the clipboard stay inside that compositor. The app also gets its own D-Bus session (no notifications, portal dialogs or browser tabs on the user's desktop). Files, the network and programs your backend starts are **not** sandboxed. It works even while the screen is locked.
- It fails (non-zero exit) if the tests fail, the build fails, the build has any warning (warnings are errors) or the release build has IL2xxx/IL3xxx warnings, or the app didn't start or died, or its QML threw an error while running (a `TypeError`/`ReferenceError` or an `Unable to assign [undefined]` in your own `.qml` files, listed under `--- QML errors ---`). It prints:
  - the **visible text**, top to bottom, with the control type, `[checked]`/`[unchecked]` on switches and checkboxes, and `[focus]` on the focused one (also saved in `obj/shot.txt`; password fields show only their masked text). This is the quickest way to check results.
  - the path of **`obj/shot.png`**: open it to check layout, colors and clipping.
  - the app's **output**, including QML errors with file and line (all of it in `obj/omasharp/run.log`). A QML error means part of the UI is missing or wrong, even if the build succeeded.
- `--keys` is text to type, plus keys in braces: `{Tab}` `{Enter}` `{Esc}` `{Up}` `{Down}` `{Left}` `{Right}` `{Backspace}` `{Delete}` `{Home}` `{End}` `{Space}` `{F5}`, and combinations like `{Ctrl+a}` `{Ctrl+Shift+z}` `{Alt+1}`. `{Wait:1500}` pauses 1.5 s (for async work: `"icon{Enter}{Wait:2000}{Down}"`). `{{` types a literal `{`. No shell sees the keys, so `$`, quotes and backticks are typed as they are. The first focused control gets the keys, so give your main input `focus: true`.
- Timing: the app gets 3 s to start, keys are typed 30 ms apart, and the screenshot is taken 0.8 s after the last key. End `--keys` with `{Wait:...}` if the result needs longer.
- There is no mouse. Give every action you want to check a keyboard shortcut (`Shortcut { sequence: "Ctrl+Return"; onActivated: ... }`), which is good UX on Omarchy anyway.
- The screen is 1280×960 (logical pixels); a bigger window is clipped. Change it with `--size 1600x1000`.
- `--args "--flag value /some/path"` passes command-line arguments to the app (split on spaces, no quotes inside); the app sees them in `Bridge.Args`.
- `--offscreen` runs a window app with Qt's offscreen platform instead: visible text only, no screenshot and no keys, but no Hyprland needed (CI).
- **Logic:** `dotnet run --project tests` runs `tests/Tests.cs` alone: plain C# checks of `Backend`, no Qt. Add your checks there; if you split the C# into more files, add them to `tests/*.Tests.csproj` too. `--no-tests` skips them in `check.sh`.
- Qt logs go to the journal when stderr isn't a terminal; `check.sh` and dev mode set `QT_FORCE_STDERR_LOGGING=1` for you.
