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

## check.sh

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

## Bridge

### 5. C# `Theme` has no theme name

- **What happened:** disktiles shows the theme's name (`current/theme.name`). `Theme` exposes
  the colours only, so the app reads that file itself.
- **Fix:** `Theme.Name` (and `theme.name` in QML), from `current/theme.name`.

### 6. `Theme.Colors` mixes the defaults into the user's theme

- **What happened:** a theme without `lighter_background` gets Tokyo Night's
  `lighterBackground` from `Defaults()`, so an app can't tell what the theme defines and derive
  its own fallback (the original derives missing roles from the theme's own colours). All 22
  themes that ship with Omarchy 4 define every role, so it doesn't bite today.
- **Fix:** keep defaults only when there is no `colors.toml` at all, or expose `Theme.Has(key)`.

## Changes made to this app's copy of the bridge and skill

None yet.
