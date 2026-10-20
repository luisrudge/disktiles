# Feedback for omasharp

From building disktiles-omasharp, the first real app on the published template
(`Omasharp.Templates` 0.1.1 from nuget.org, bridge 0.1.1, `Omasharp.Analyzers` 0.1.1).
Each item: what happened, what I expected, a suggested fix. Changes made to this app's own
copy of the bridge or skill are listed at the end.

## Template and `dotnet new`

### 1. `--app-id` is rejected; the option is `--appId`

- **What happened:** `dotnet new omarchy-app -n DiskTiles -o . --app-id disktiles-omasharp`
  fails with exit 127 ("unknown option"). The option is `--appId` (or `-ap`).
- **Expected:** `--app-id`, the kebab-case spelling every other `dotnet new` option uses
  (`--dry-run`, `--no-update-check`), and the one a user types after reading "app id" in the docs.
- **Fix:** add `.template.config/dotnetcli.host.json` with
  `{ "symbolInfo": { "appId": { "longName": "app-id", "shortName": "" } } }`, and keep
  `--appId` working as an alias if that matters.

### 2. The generated `CLAUDE.md` deletes itself, and with it the pointer to `AGENTS.md`

- **What happened:** `CLAUDE.md` says "Read @AGENTS.md … create .claude/skills/omasharp as a
  symlink, then delete this file." I did. After that, nothing makes Claude Code load
  `AGENTS.md` in a new session; only the skill (through the symlink) is found.
- **Expected:** the setup step leaves `@AGENTS.md` reachable.
- **Fix:** keep `CLAUDE.md` as the one line `@AGENTS.md` and drop only the setup sentence
  (or ship the `.claude/skills/omasharp` symlink in the template and no setup step at all).

## Analyzers and build settings

### 3. CA1716 (VB keyword names) is on in a C#-only template

- **What happened:** `AnalysisLevel=10.0-recommended` turns on CA1716, which fails the build on
  members named `Next` or parameters named `error`, because they are Visual Basic keywords.
- **Expected:** a C#-only app template doesn't fail on VB keywords.
- **Fix:** `dotnet_diagnostic.CA1716.severity = none` in the template's `.editorconfig`, with
  that reason. (The GTK original turns it off for the same reason.)

No omasharp analyzer (OMASHARP0xx) false positives so far: the 334-line `Backend.cs` built
clean, and a deliberate `static` property and a `Delete()` method were caught (OMASHARP003,
OMASHARP001) with clear messages.

## check.sh and the benchmark tools

### 4. Every window floats and centres, so a normal-size app window is clipped

- **What happened:** the private compositor's rule is `float = true, center = true` for every
  window, on a 1280×960 screen. disktiles opens at 1440×900 (the original's size), so both
  sides were cut off in `obj/shot.png`. Omarchy tiles new windows, so a real user never sees
  that.
- **Expected:** the screenshot shows the app as a user would see it, or a warning when the
  window is larger than the screen.
- **Fix:** a `--tile` mode (no float rule: the window fills the screen like a tiled window on
  Omarchy), and print "window is WxH, larger than the WxH screen: use --size" when it doesn't fit.
  Workaround here: `--size 1600x1000`.

### 5. No way to give the app environment variables

- **What happened:** the app is started by the nested Hyprland (`hl.dsp.exec_cmd`), so it gets
  the session's environment, not the caller's. `HOME=/tmp/fake sh check.sh` still ran the app
  with the real `HOME` (the build used the fake one). I wanted a fake Omarchy state directory to
  check a live theme switch without touching the user's theme, and the earlier port's
  `DISKTILES_*` switches had to become `--test-*` options for the same reason.
- **Expected:** `check.sh --env KEY=VALUE` (repeatable), or passing through `OMASHARP_SHOT_ENV_*`.
- **Fix:** shot.sh writes `export KEY=VALUE` lines into the app's launcher script, as it already
  does for `QT_FORCE_STDERR_LOGGING`. Workaround here: the dev build under
  `QT_QPA_PLATFORM=offscreen` with the fake `HOME`, and the app's `--snapshot` for the picture.

### 6. `tools/bench.sh`: parallel runs measure, close and kill each other's apps

- **What happened:** `bench.sh` launches the app on `special:omasharp-bench`, then takes the
  first window on that workspace as "the app". Another thread on this machine was benchmarking
  omacalc with `bench.sh` at the same time. One of my runs picked up their omacalc (window in
  18 ms, omacalc's memory), measured it, closed it, and its cleanup `kill -9`-ed it. Their runs
  could just as well have measured my disktiles windows. Nothing in the output says so.
- **Expected:** a benchmark that only ever touches the process it started.
- **Fix:** a workspace per run (`special:omasharp-bench-$$`) and find the window by the pid the
  launcher writes before it `exec`s the app (shot.sh already finds its compositor by pid). This
  repo's `bench/bench.sh` has that change. Also worth printing the app's class next to each
  result, so a wrong match is visible.
- Related: interleaving makes the comparison fair, but other work on the machine still inflates
  both apps' times; a load-average column (as `bench/run-all.sh` logs) would make a noisy run
  obvious.

## Bridge

### 7. DrawView draws nothing but text under Qt's software renderer (fixed here)

- **What happened:** with `QT_QPA_PLATFORM=offscreen` (and so with `QT_QUICK_BACKEND=software`,
  which `check.sh --offscreen` sets, on a machine or VM without a GPU, or wherever Qt falls back
  to software), the treemap was labels on an empty background. `drawview.cpp` puts every shape
  in one `QSGGeometryNode` with `QSGVertexColorMaterial`, and the software scene graph doesn't
  draw custom materials; only the `QSGTextNode`s show.
- **Expected:** the same picture, slower.
- **Fix (in this app's `bridge/host/drawview.cpp`):** when
  `window()->rendererInterface()->graphicsApi() == QSGRendererInterface::Software`, paint the
  triangles into a `QImage` with `QPainter` and show it with `createImageNode()`, under the same
  transform node. About 30 lines; checked with an offscreen `--snapshot`. Worth a note in
  drawing.md too: `--offscreen` text checks can't see a DrawView's shapes.

### 8. C# `Theme` has no theme name

- **What happened:** disktiles shows the theme's name (`current/theme.name`). `Theme` exposes
  the colours only, so the app reads that file itself.
- **Fix:** `Theme.Name` (and `theme.name` in QML), from `current/theme.name`.

### 9. `Theme.Colors` mixes the defaults into the user's theme

- **What happened:** a theme without `lighter_background` gets Tokyo Night's
  `lighterBackground` from `Defaults()`, so an app can't tell what the theme defines and derive
  its own fallback (the original derives missing roles from the theme's own colours). All 22
  themes that ship with Omarchy 4 define every role, so it doesn't bite today.
- **Fix:** keep defaults only when there is no `colors.toml` at all, or expose `Theme.Has(key)`.

### 10. The bridge's `Install` writes a minimal desktop entry and can't take more

- **What happened:** `-t:Install` writes `Name`, `Exec`, `Icon`, `StartupWMClass`, `Terminal`.
  The GTK original's entry also has `GenericName`, `Comment`, `Exec … %f` (open a folder),
  `Categories`, `Keywords`, `StartupNotify` and a desktop action ("Scan the whole disk",
  `--disk`). There is no property or item to add keys, and it doesn't refresh
  `update-desktop-database` or the icon cache.
- **Expected:** csproj properties such as `AppComment`, `AppCategories`, `AppKeywords`,
  `AppExecArgs` (`%f`), and an item list for actions, or a `desktop.in` template file in the app.
- **Workaround here (until omasharp PR #2):** a `DiskTilesDesktopEntry` target `AfterTargets="Install"` in
  `DiskTiles.csproj` that rewrites the entry (tested with `HOME` set to a scratch folder; it
  passes `desktop-file-validate`). It reads the bridge's private `_Home`, which is fragile.
- Smaller: `WriteLinesToFile` drops empty lines, so the entry can't have the blank line before
  `[Desktop Action …]` (valid without it, but unusual).

### 11. `grabToImage` of the root leaves out the window's colour

- Not a bridge bug, but every app that wants a screenshot of itself (disktiles' `--snapshot`, a
  release self-check) hits it: the PNG has a transparent background, so the header and footer
  look black. Workaround: a `Rectangle { color: theme.background }` filling the grabbed item.
  A line in the skill (or a `Bridge.Snapshot(path)` in the host that uses
  `QQuickWindow::grabWindow()`) would save the next app the hour.

## Skill and docs

### 12. The skill steers window apps to QtQuick.Controls, which costs startup time

- **What happened:** look.md says to use `Label` from QtQuick.Controls, and the template's
  `Main.qml` is an `ApplicationWindow`. In the earlier port's measurements, importing Controls
  and using `ApplicationWindow` cost 20–25 ms of startup in this host, and Controls'
  BusyIndicator fade-out delayed the first frame that mattered. disktiles avoids Controls
  (`ui/Label.qml` is a `Text` with the Omarchy font) to keep its first treemap ahead of GTK's.
- **Fix:** say so in look.md ("for the fastest start, a plain `Window` and a `Text`-based
  `Label`"), or ship a Controls-free `Label` in the template.

### 13. No way to share an app with people who don't build it

- **What happened:** the only install the template knows is `dotnet publish -t:Install` from a
  checkout, which needs the .NET SDK, qmake and g++. Apps for Omarchy outside its package
  repository ship a `curl … | sh` installer instead (omairc, Omadesign, disktree), and Omarchy is
  moving away from the AUR.
- **Fix (omasharp PR #3, stacked on #2; this app has it):** `dotnet publish -t:Package` makes a
  release (tarball, `SHA256SUMS`, `install.sh`), the template gets `.github/workflows/release.yml`
  (built on Omarchy's stable mirror, since the host needs the Qt it was built with or newer), and
  `-t:Install` runs the same `install.sh`.

### 14. The release workflow doesn't start where actions must be pinned

- **What happened:** the template's `release.yml` uses `actions/checkout@v5`. This app's repo
  requires actions pinned to a full commit SHA (an Actions setting), so the `v0.3.0` tag's run
  failed at startup ("a workflow file issue") and released nothing.
- **Fix (here; not in the template yet):** `actions/checkout@<sha> # v7.0.1`, as the GTK app's
  workflows had it. The template could pin it too.

### 15. What worked well

- `dotnet new install` + `dotnet new omarchy-app` gave a project that built, tested and passed
  `check.sh` (dev and `--release`) on the first try.
- The bridge already has the earlier port's performance patches (`Bridge.Show` without a post
  on the UI thread, the portal platform theme instead of GTK 3), so the native treemap carried
  over unchanged.
- The analyzers: no false positives on a 334-line `Backend`, and clear messages on purpose-made
  mistakes. The IL/AOT analyzers on every dev build caught nothing late.
- `check.sh` ran fine while another thread's check was running at the same time (it finds its
  compositor by pid), and the visible-text dump made most checks a `grep`.

## Changes made to this app's copy of the bridge and skill

The bridge and skill are Omasharp 0.1.7, which has this app's earlier changes (the
software-renderer fallback, item 7, in 0.1.3; the releases, item 13, in 0.1.4).

| File | Change | Why |
|---|---|---|
| `.github/workflows/release.yml` | `actions/checkout` pinned to a SHA (item 14) | this repo only runs pinned actions |
| `bench/bench.sh` (copy of `tools/bench.sh`) | a workspace per run, the app found by pid (item 6) | parallel benchmarks killed each other's apps |
