# Running the app, build, install, the bridge

## Running the app (for the human)

```sh
dotnet run          # a dev build, opened on the screen; edit, close it, run it again
```

- It runs the dev build, the same one `check.sh` checks: the C# side on the normal .NET runtime (JIT), so builds are fast and the debugger works, and the QML straight from the project folder. Release builds are native AOT. Check the release build now and then (`check.sh --release`), since some code is fine on JIT but not under AOT ([backend.md](backend.md)).
- Errors from QML and from C# go to the terminal.
- Nothing reloads while it runs: close the app and run it again to see an edit.
- **Panels:** `dotnet run` opens the panel in its own Quickshell process with the shell's theme, not inside `omarchy-shell`, so a broken panel can't hurt the desktop shell. Closing the panel ends the run. Set `OMASHARP_PANEL_PAYLOAD='{"screen":"<monitor>"}'` to choose the monitor.

## Build, install, uninstall

```sh
dotnet publish                  # window: bin/Release/net10.0/linux-x64/publish/{<appid>, lib<appid>.so, <appid>.rcc}
                                # panel:  plugin/bin/<Name> + plugin/Backend.qml
dotnet publish -t:Install       # window: ~/.local/share/omasharp/<appid>/, ~/.local/bin/<appid>, a .desktop entry (app.desktop, icon.svg/icon.png if present),
                                #         through the same install.sh users run (see "Sharing a window app")
                                # panel:  copies plugin/ to ~/.config/omarchy/plugins/<appid>/ and enables it
dotnet build -t:Uninstall
dotnet publish -t:Package       # window: a release in artifacts/: <appid>-linux-x64.tar.gz, SHA256SUMS, install.sh
```

- **The launcher entry** (window apps): Install (and a release) writes a minimal `.desktop` entry. For more (a comment, categories, keywords, opening files with `%f`, actions), put your own `app.desktop` next to the csproj and Install uses it instead. Write `@EXEC@` where the installed binary goes. Install sets `StartupWMClass`, and `Icon` when the app has `icon.svg` or `icon.png`:
  ```ini
  [Desktop Entry]
  Type=Application
  Name=Tally
  Comment=Count things
  Exec=@EXEC@ %f
  Terminal=false
  Categories=Utility;
  Actions=reset;

  [Desktop Action reset]
  Name=Start from zero
  Exec=@EXEC@ --reset
  ```
  Package (and so Install) checks it with `desktop-file-validate` (when it's installed) and fails on an invalid entry. Installing refreshes the launcher's caches. Uninstall removes all of it.
- Window apps: the host (`<appid>`) is the same for every app. It's built once per machine (qmake + g++, about 7 s) and cached in `~/.cache/omasharp/`.
- A release build takes about 5 s (mostly the native AOT compiler); a QML-only change about 1.5 s. A dev build is faster, which is why `check.sh` uses it by default.
- **The app_id** is `AppId` in the csproj (`dotnet new omarchy-app -n Tally --app-id tally-app` sets it; the default is the name in lowercase). Window rules match on it: the app floats if the user adds a rule such as `o.window("<appid>", { float = true })` in `~/.config/hypr/`. Don't change the user's Hyprland config without asking.

## Sharing a window app

People install a window app with one command: no sudo, no .NET (the app is native code on the Qt Omarchy ships), nothing outside their home folder. It's how many Omarchy apps outside Omarchy's own package repository ship, and it works the same on Omarchy's stable, rc and edge channels.

```sh
curl -fsSL https://github.com/<owner>/<repo>/releases/latest/download/install.sh | sh              # install, or update
curl -fsSL https://github.com/<owner>/<repo>/releases/latest/download/install.sh | sh -s uninstall # remove
```

To publish a release (when the user asks; it's public):
1. The app is a GitHub repo, with `.github/workflows/release.yml` from the template.
2. Set `<Version>` in the csproj, commit, then push a tag that matches it: `git tag v0.2.0 && git push origin v0.2.0`.
3. The workflow runs the unit tests, builds with `dotnet publish -t:Package` and attaches its files to a GitHub release. A tag with a suffix (`v0.2.0-rc.1`) makes a pre-release, which `releases/latest` skips.

Put the two commands in the app's README.

- **Built on Omarchy's stable packages.** The workflow takes its toolchain from Omarchy's stable mirror, not Arch's: the host needs the Qt it was built with or newer, so a build on the newest Arch Qt wouldn't start on Omarchy stable until it caught up. Don't change that to `archlinux:latest`'s own packages.
- **What `install.sh` does:** it downloads `<appid>-linux-x64.tar.gz` and `SHA256SUMS` from the release and checks one against the other before unpacking. It checks with `ldd` that the system has every library the app needs, at least as new as the ones it was built with; if not, it says to update the system and installs nothing. Then it puts the app where `-t:Install` does: the new copy goes in next to the old one and then takes its place, so a copy that's running keeps working, and a second install of the same app waits for the first. `OMASHARP_RELEASE=v0.1.0` installs that release instead of the latest; `OMASHARP_RELEASE_URL=<url>` downloads from another place with the same files (`file://` works, for testing). In an unpacked tarball, `sh install.sh` installs that copy; piped from curl, it always downloads.
- `-t:Install` runs the same `install.sh` on the same files (`obj/omasharp/release/<appid>/`), so what a developer installs is what users get.
- `-t:Package` writes the repo into `install.sh`: `AppRepository` in the csproj (`owner/repo`), which GitHub Actions fills in. Without it, that `install.sh` only installs from an unpacked tarball.
- Releases are x86_64 only (`RuntimeIdentifier` is `linux-x64`). Panels have no release: `-t:Package` is for window apps.
- Omarchy's own repository (`omarchy pkg add <name>`) takes apps through a PKGBUILD in [omacom/omarchy-pkgs](https://github.com/omacom/omarchy-pkgs). It can repackage the same tarball.

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
| The build fails with OMASHARP…, CA… or IL… | do what the message says; [backend.md](backend.md#the-build-checks-the-backend) has the Omasharp rules and how to turn one off for a member |
| `can't load lib<id>.so` | run the published `<id>` binary from its own folder (the lib must sit next to it) |
| You need GTK's Qt integration back (GTK file dialogs, GTK font settings) | the host switches Omarchy's `QT_QPA_PLATFORMTHEME=gtk3` to the portal theme (about 55 ms faster start); run with `OMASHARP_PLATFORMTHEME=gtk3` to keep it |
| `Failed to register with host portal ... already associated with an application ID` in the output | harmless, every Qt app prints it here; ignore |
| Panel says "Backend stopped" | the C# process crashed; `backend.lastError` and the shell log (`quickshell log --pid $(pgrep -f 'quickshell -n -p /usr/share/omarchy/shell')`) show why |
