# bridge/

The code that connects `Backend.cs` to QML. You rarely need to touch it. The tools for checking an app live in the omasharp skill, in `.agents/skills/omasharp/scripts/`.

| Path | What it does |
|---|---|
| `cs/Model.cs` | Finds the backend's public properties and methods and camelCases their names for QML. Encodes values as JSON. Turns `PropertyChanged` into notifications on the UI thread. |
| `cs/Ui.cs` | `BackendBase` (`Set`, `Notify`), `Ui.Post`, the UI-thread `SynchronizationContext` and `Bridge.IsNativeAot`. |
| `cs/QmlNames.cs` | How member names look in QML, and the names QML can't take. The `Omasharp.Analyzers` package compiles this same file. |
| `cs/Native.cs` | Window apps. The C ABI (`omasharp_api`) that the host calls. |
| `cs/Dev.cs` | Window apps in dev mode. The managed `Main` the host runs through hostfxr, and the hot reload hook. |
| `cs/Stdio.cs`, `cs/Shim.cs` | Panels. The JSON-lines protocol over stdin and stdout, and the generator for `plugin/Backend.qml`. |
| `cs/Drawing.cs`, `host/drawview.cpp` | Native drawing. C# builds a `Drawing` and calls `Bridge.Show(name, drawing)`, and QML's `DrawView` draws it through the scene graph, with a view transform for zoom and pan. |
| `host/` | The C++ host, built with qmake and g++. It holds `backend` in a `QQmlPropertyMap`, exposes `theme` from Omarchy's `colors.toml`, and runs dev mode and QML hot reload. Every app uses the same host. It's built once into `~/.cache/omasharp/host-<Qt version>-<hash>/` and copied in as `<AppId>`. |
| `shell/dev-shell.qml` | The standalone Quickshell harness for developing panels. |
| `Omasharp.targets` | The build glue. AOT settings, warnings as errors, the analyzers (`Omasharp.Analyzers` at the same version as `VERSION`), the host build, the panel publish, dev mode, Install and Uninstall, and `OmasharpLaunch`, the run command the skill's `check.sh` uses. |
| `update.sh` | Runs for `dotnet build -t:UpdateBridge`. Brings in a newer bridge and skill from the installed template and keeps your changes. |
| `VERSION`, `MANIFEST` | The version this app has, and a fingerprint of every bridge and skill file it received. |

How an app starts:

- **Window app, release.** `<appid>` (the host) loads `lib<appid>.so` (the C#, compiled with native AOT) and calls `omasharp_api()`. It registers `<appid>.rcc` (the app's QML, fonts and images) and runs `qrc:/Main.qml`.
- **Window app, dev.** `<appid> --dev App.dll --qml-dir .` starts .NET through hostfxr. `Main` in App.dll calls back into `omasharp_host_run`.
- **Panel.** omarchy-shell loads `plugin/Panel.qml`. Its `Backend {}` (the generated `Backend.qml`) runs `plugin/bin/<name>` and talks JSON lines to it.
