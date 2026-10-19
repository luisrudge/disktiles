# DiskTiles

A C# + QML app for Omarchy, made with the omarchy-app template: logic in `Backend.cs`, UI in `Main.qml` (window) or `plugin/Panel.qml` (panel).

- Building, checking and installing it: the omasharp skill in `.agents/skills/omasharp/`.
- Never run `dotnet run` or `dotnet watch` as an agent: they open the app on the user's screen. Check with `sh .agents/skills/omasharp/scripts/check.sh`.

## This app

disktiles rebuilt on omasharp: a treemap of what uses the disk, marking, review and removal. README.md has the feature checklist; FEEDBACK.md is what this app found wrong or missing in omasharp (add to it when the template, bridge, skill or analyzers get in the way).

- `Core/`: scan, tree, treemap layout, filter, insights, marks, removal guards and trash (from the GTK original, Linux only). `App/`: the controller (keys, navigation, zoom, scanning, trail, filter, marking, review) as view models, no Qt. `Backend.cs`: the thin QML facade over `App`. `Main.qml` + `ui/`: rendering only; every key goes to `backend.press`. The treemap is a `Drawing` built in `App/Paint.cs` and shown by `DrawView`; QML doesn't import QtQuick.Controls (startup cost), so `ui/Label.qml` stands in for Label.
- Tests: `dotnet run --project tests` (core, removal on fixtures under `/tmp/claude-disktiles/`, and the controller driven like QML drives it). New `.cs` folders go into `tests/DiskTiles.Tests.csproj`.
- Checks: always pass a folder (`--args "/usr/share/doc"`): with none, the app scans `$HOME`. The window is 1440×900, so use `--size 1600x1000`. Debug builds take `--test-*` options (listed in `App/Hooks.cs`): `--test-keys "Return:menu=4"`, `--test-hover x,y`, `--test-log 1`, …
- **Removal: only ever on a scratch tree under `/tmp/claude-disktiles/`, always with `--test-trash-dir <scratch>/trash`** (an XDG trash of its own; without it "trash" means the user's trash). Never mark real files.
- Measuring: `bench/` (README.md, "Benchmark"). `DISKTILES_PERF=<file>` and `DISKTILES_WORKLOAD=…` need a `-p:TestHooks=true` release build.
- `bridge/host/drawview.cpp` has a local change (software-renderer fallback, FEEDBACK.md): keep it when updating the bridge.
- Releases: `bridge/install.sh`, `bridge/desktop.sh` and the Package/Install/Uninstall targets in `bridge/Omasharp.targets` are ahead of the published template (omasharp PRs #2 and #3, 0.1.4). `dotnet publish -t:Package` makes the release, `.github/workflows/release.yml` publishes it for a `v<Version>` tag, and users install it with the `curl … | sh` in README.md. Test `install.sh` only with a scratch `HOME` under `/tmp/claude-disktiles/` (and `OMASHARP_RELEASE_URL=file://…`).
