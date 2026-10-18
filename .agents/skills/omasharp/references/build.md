# Dev loop, build, install, the bridge

## Dev loop (for the human)

```sh
dotnet watch        # run the app; rebuilds and reloads as you save
```

- **QML edits** (window apps) reload the window about 0.15 s after you save. The window is recreated, so UI-only state (scroll position, text being typed) resets; backend state stays.
- **C# edits** (window apps) hot-reload into the running app in about 1.5 s: method bodies, and new properties and methods too (QML sees new members after its next reload). Edits hot reload can't apply (a field's type, a base class) restart the app, which takes about 3 s.
- In dev mode the C# side runs on the normal .NET runtime (JIT), so the fast loop and the debugger work. Release builds are native AOT. Check the release build now and then, since some code is fine on JIT but not under AOT ([backend.md](backend.md)).
- Errors from QML and from C# go to the terminal.
- **Panels:** `dotnet watch` opens the panel in its own Quickshell process with the shell's theme, not inside `omarchy-shell`, so a broken panel can't hurt the desktop shell. QML edits reload it at once (this restarts the backend, so its state resets). C# edits restart it, about 3 s. Closing the panel ends the run. Set `OMASHARP_PANEL_PAYLOAD='{"screen":"<monitor>"}'` to choose the monitor.

## Build, install, uninstall

```sh
dotnet publish                  # window: bin/Release/net10.0/linux-x64/publish/{<appid>, lib<appid>.so, <appid>.rcc}
                                # panel:  plugin/bin/<Name> + plugin/Backend.qml
dotnet publish -t:Install       # window: ~/.local/share/omasharp/<appid>/, ~/.local/bin/<appid>, a .desktop entry (icon.svg/icon.png if present)
                                # panel:  copies plugin/ to ~/.config/omarchy/plugins/<appid>/ and enables it
dotnet build -t:Uninstall
```

- Window apps: the host (`<appid>`) is the same for every app. It's built once per machine (qmake + g++, about 7 s) and cached in `~/.cache/omasharp/`.
- A release build takes about 5 s (mostly the native AOT compiler); a QML-only change about 1.5 s. `dotnet watch` applies C# changes in about 1.5 s.
- **The app_id** is `AppId` in the csproj. Window rules match on it: the app floats if the user adds a rule such as `o.window("<appid>", { float = true })` in `~/.config/hypr/`. Don't change the user's Hyprland config without asking.

## The bridge: extending and updating it

`bridge/` (and this skill) are plain source you own (like shadcn components), not a package. The bridge covers what most apps need; if yours needs something it doesn't do, add it.

- **Prefer code outside `bridge/`**: your own C# files, QML components, helpers. Touch `bridge/` only for what can't live elsewhere (a new host capability, a fix). Keep such changes small and commented, and consider proposing them upstream.
- Where things are: `bridge/README.md`. C# side: `bridge/cs/`. C++ host: `bridge/host/` (a new host function = a field in `omasharp_host` in `omasharp.h`, the matching field in `HostApi` in `cs/Native.cs`, and its implementation in `main.cpp`; keep both struct orders identical). Changing the host is fine: it is rebuilt and cached automatically when its sources change.
- **Updating** the bridge and this skill (after `dotnet new install` of a newer template):
  ```sh
  dotnet build -t:UpdateBridge
  ```
  Files you never changed are replaced. Files you changed are kept, and the new version is saved next to each as `<file>.upstream` with the diff printed. Move your change into the new code, delete the `.upstream` file, then run `check.sh` again. `bridge/VERSION` is the version you have; `bridge/MANIFEST` records what you received (don't edit it).

## When something's wrong

| Symptom | Cause |
|---|---|
| `backend.foo` is `undefined` | not public, or a static member, or misspelt (QML names are camelCased) |
| A value never updates in QML | the setter doesn't call `Set`/`Notify`, or a computed property's inputs don't `Notify` it |
| Works in dev, fails in the release build | AOT: check the publish warnings (reflection, JSON, an unsafe package) |
| The build fails with OMASHARP…, CA… or IL… | do what the message says; [backend.md](backend.md#the-build-checks-the-backend) has the omasharp rules and how to turn one off for a member |
| `can't load lib<id>.so` | run the published `<id>` binary from its own folder (the lib must sit next to it) |
| You need GTK's Qt integration back (GTK file dialogs, GTK font settings) | the host switches Omarchy's `QT_QPA_PLATFORMTHEME=gtk3` to the portal theme (about 55 ms faster start); run with `OMASHARP_PLATFORMTHEME=gtk3` to keep it |
| `Failed to register with host portal ... already associated with an application ID` in the output | harmless, every Qt app prints it here; ignore |
| Panel says "Backend stopped" | the C# process crashed; `backend.lastError` and the shell log (`quickshell log --pid $(pgrep -f 'quickshell -n -p /usr/share/omarchy/shell')`) show why |
