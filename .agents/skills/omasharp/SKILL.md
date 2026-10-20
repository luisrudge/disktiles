---
name: omasharp
description: Build, change and check this C# + QML Omarchy app (made with Omasharp's omarchy-app template). Use for any work in this app - Backend.cs, Main.qml, plugin/Panel.qml, tests, how it looks, building, checking that it works without touching the user's screen, installing it, or updating its bridge.
---

# A C# + QML app on Omarchy

The app is two files you edit plus glue you normally don't:

| File | What it is |
|---|---|
| `Backend.cs` | All logic. QML sees it as `backend`. |
| `Main.qml` (window app) or `plugin/Panel.qml` (panel) | All UI. |
| the app's `.csproj` | `OmarchyKind` (window or panel), `AppId`, `AppName`. |
| `tests/` | Plain C# checks of `Backend` (no Qt). |
| `bridge/` | The glue: C# bridge, C++ host, build targets. Copied into the app, so it's yours, but leave it alone unless you're fixing the bridge itself. |

**Window apps** run as one native program: `Backend.cs` compiled with native AOT, the QML packed next to it, and a small Qt host. Users need no .NET, just the Qt that Omarchy ships.

**Panels** run inside `omarchy-shell`. The QML is a shell plugin, and `Backend.cs` runs as a separate native program that talks JSON over stdin/stdout. If it crashes, the shell keeps running and the panel shows the error.

Setup, once per machine: the .NET SDK 10 (`omarchy pkg add dotnet-sdk`). qmake6 and g++ ship with Omarchy.

## Workflow

1. Edit `Backend.cs` (logic) and `Main.qml` or `plugin/Panel.qml` (UI). Replace the starter checks in `tests/Tests.cs` (they test the template's counter) with checks for your logic.
2. Check it:
   ```sh
   sh .agents/skills/omasharp/scripts/check.sh                       # tests, dev build, run it invisibly
   sh .agents/skills/omasharp/scripts/check.sh --keys "12{Tab}3{Enter}"   # type into the app first
   ```
   It runs the unit tests, then starts the app in a private, invisible compositor and prints every visible text in the window, top to bottom, plus any QML error. A QML error in your own files fails the check. The app starts with empty data and settings (its `XDG_*_HOME` folders are in `obj/omasharp/xdg/`), never the user's; `--seed DIR` starts it from yours ([references/checking.md](references/checking.md)).
3. **Read the printed values and check they're right** (is the result correct? does the list show what it should?), then open `obj/shot.png` to check the layout. A successful build proves nothing about the UI. Repeat 1–3 until both are right.
4. Finish with **`sh .agents/skills/omasharp/scripts/check.sh --release`**: the same check on the release (native AOT) build. It fails on IL2xxx/IL3xxx warnings and must show the same values.
5. Install only if the user asked: `dotnet publish -t:Install`. Publishing a release for other people (window apps: one `curl … | sh`, no sudo) also only when asked: [references/build.md](references/build.md#sharing-a-window-app).

**Don't run `dotnet run` (or `dotnet watch`) yourself**: they open the app on the user's screen and never return. `dotnet run` is for the human who wants to see the app.

## What QML can see

Everything public on `Backend`, with the first letter lowercased:

| C# | QML |
|---|---|
| `public int Count { get => _count; set => Set(ref _count, value); }` | `backend.count` (bindable; assigning sets it in C#) |
| `public string Label => ...` (get only) | `backend.label` (read-only) |
| `public void Increment()` | `backend.increment()` |
| `public int Add(int a, int b)` | `backend.add(2, 3)`; in a window it returns 5. In a **panel** it returns a Promise: `backend.add(2, 3).then(v => ...)` |
| `public async Task<Weather> FetchAsync(string city)` | `backend.fetchAsync("Oslo").then(w => label.text = w.temp)`: a Promise that settles when the task finishes |
| `public List<Item> Items { get; }` with `record Item(string Name, long Size)` | `backend.items`: a JS array of `{ name, size }`, usable as a `Repeater`/`ListView` model |

- **Bindings update only when C# says so.** Use `Set(ref _field, value)` in setters. For computed properties, call `Notify(nameof(Label))` whenever an input changes. Lists: assign a new list with `Set`, or call `Notify(nameof(Items))` after changing it in place.
- Backend code runs on the UI thread; `Notify`/`Set` from any other thread is safe.
- `Delete()`, `New()`, names starting with `_` and a few others can't be used from QML; the build says so.
- **Every warning fails the build**, including Omasharp's checks of `Backend` (OMASHARP001…): each message says the fix. Don't make `Backend`'s public members `static` (QML only sees instance members). The rules, `IDisposable` and turning a rule off for one member: [references/backend.md](references/backend.md#the-build-checks-the-backend).

## More, when the task needs it

| Read | For |
|---|---|
| [references/checking.md](references/checking.md) | everything `check.sh` does: keys, arguments, screen size, timing, what it prints, unit tests |
| [references/backend.md](references/backend.md) | C# ⟷ QML in detail: types, async, threads, events from C# (`Raise`), long-running work, timers, native AOT rules |
| [references/look.md](references/look.md) | looking like Omarchy: `theme.*` colors and fonts in windows, the shell's `Commons.Color`/`Style`/`qs.Ui` in panels |
| [references/panels.md](references/panels.md) | panels: lifetime, `keepLoaded`, keyboard, opening one, what isn't supported |
| [references/drawing.md](references/drawing.md) | charts, maps, treemaps: `Drawing` + `DrawView` instead of `Canvas` |
| [references/sqlite.md](references/sqlite.md) | storing data: SQLite (the system's, nothing to ship), where the database goes, AOT-safe queries |
| [references/build.md](references/build.md) | running the app (for the human), build/install/uninstall, sharing a window app (releases, `install.sh`), the app_id, extending and updating `bridge/`, troubleshooting |
