# C# ⟷ QML in detail

| C# | QML |
|---|---|
| `int _count;` `public int Count { get => _count; set => Set(ref _count, value); }` | `backend.count` (bindable; assigning sets it in C#) |
| `public string Label => ...` (get only) | `backend.label` (read-only: assignments are rejected and logged) |
| `public void Increment()` | `backend.increment()` |
| `public int Add(int a, int b)` | `backend.add(2, 3)`; in a window it returns 5. In a **panel** it returns a Promise: `backend.add(2, 3).then(v => ...)` |
| `public async Task<Weather> FetchAsync(string city)` | `backend.fetchAsync("Oslo").then(w => label.text = w.temp)`: async methods return a Promise that settles when the task finishes, with its value (or rejects with the exception's message) |
| `public async Task SaveAsync()` | `backend.saveAsync()`: a Promise resolving to `null` when done |
| `public List<Item> Items { get; }` with `record Item(string Name, long Size)` | `backend.items`: a JS array of `{ name, size }`. Use it as a model: `Repeater { model: backend.items; Label { required property var modelData; text: modelData.name } }` (or `ListView`) |
| exception thrown in C# | a JS exception in a window (`try/catch`), a rejected Promise in a panel |

- Values QML can read: `string`, `bool`, numbers (`int`, `long`, `double`, `decimal`, ...), enums (as strings), `DateTime` (ISO string), **your own classes and records** (JS objects with their public properties, camelCased), and arrays, lists and `Dictionary<string, T>` of any of those, nested. Method **arguments** from QML, and values QML assigns to properties, must be `string`, `bool`, a number, an enum, `string[]`/`double[]`/`int[]`, or `object` (a parsed JSON value): pass an id or index, not an object.
- **Changing a list:** QML only sees a new list when you notify. Either assign a new list to a property with `Set`, or change it in place and call `Notify(nameof(Items))`. Each notify sends the whole list, so for thousands of rows send a page or a summary instead.
- **Bindings update only when C# says so.** Use `Set(ref _field, value)` in setters. For computed properties, call `Notify(nameof(Label))` whenever an input changes.
- **Threads:** Backend code runs on the UI thread. After `await` you are back on the UI thread. `Notify`/`Set` from a background thread (timers, file watchers, `Task.Run`) is safe: the bridge moves the update to the UI thread. To run something on the UI thread yourself, use `Ui.Post(() => ...)`.
- `Bridge.IsNativeAot` tells you whether you're in a release build or in dev mode.
- `Bridge.Args` holds the app's command-line arguments (without Qt's or dev mode's own options). It's set before `Backend` is constructed.
- **C# telling QML something happened** (quit, copy text, open a dialog, play an animation): call `Raise("name", value)` from `Backend` (any thread). QML receives it as a signal:
  ```qml
  Connections { target: backend; function onRaised(name, value) { if (name === "quit") Qt.quit() } }
  ```
- Timers: for UI refreshes, a QML `Timer` calling a backend method is simplest. In C#, write `System.Threading.Timer` in full: with implicit usings, plain `Timer` is ambiguous with `System.Timers.Timer`.
- **Long-running work** (searching, scanning, network): run it with `Task.Run`, cancel the previous run, and report progress with `Ui.Post`. After `await` you're back on the UI thread, so set properties normally:
  ```csharp
  CancellationTokenSource? _work;
  public async Task Search(string term)
  {
      _work?.Cancel();
      var cts = _work = new CancellationTokenSource();
      Status = "Searching…";
      try
      {
          var found = await Task.Run(() => Find(term, cts.Token,
              progress: n => Ui.Post(() => Status = $"{n} files looked at")), cts.Token);
          if (!cts.IsCancellationRequested) { Results = found; Status = $"{found.Count} matches"; }
      }
      catch (OperationCanceledException) { }
  }
  ```
  Report progress every few hundred items or every ~100 ms, not on every item. In unit tests `Ui.Post` runs the action at once.
- In a panel, `backend.ready` (the first values have arrived), `backend.running`, `backend.lastError` and `backend.restart()` also exist.
- Names QML can't take fail the build (OMASHARP001), with a message: JS reserved words (`Delete()` would be `backend.delete`, use `Remove()`; also `New`, `Default`, `Import`, `Switch`, …), names starting with `_`, and the bridge's own members (`Ready`, `Running`, `Active`, `Command`, `Program`, `LastError`, `Restart`, `Raised`, `Exited`, `Call`, `Keys`).

## The build checks the backend

Every warning fails the build, so a mistake shows up as a build error with its fix, not as a wrong value in QML. Besides .NET's own rules (CA…, and IL2xxx/IL3xxx for native AOT, on every build), omasharp's analyzers check `Backend` against what QML can do:

| Error | Means |
|---|---|
| OMASHARP001 | a name QML can't take (above): rename it |
| OMASHARP002 | two members with the same QML name (`Total` and `total`) |
| OMASHARP003 | public but invisible to QML: static, a field, a constant, a C# `event` (use `Raise`), an indexer, a generic method. Make it an instance property/method, or non-public (`internal` is enough for tests) |
| OMASHARP004 | C# changes a property whose setter never tells QML: use `Set(ref _field, value)` |
| OMASHARP005 | a computed property whose inputs C# changes, with no `Notify(nameof(It))` anywhere |
| OMASHARP006 | `Notify("...")` with a name that isn't a public property: use `nameof` |
| OMASHARP007 | QML can't call or set it: a parameter type QML can't send, `ref`/`out`/`params`, or overloads (give each its own name) |
| OMASHARP008 | QML can't use the result: `ValueTask` (return `Task`), `IAsyncEnumerable`, tuples (return a record) |
| OMASHARP009 / 010 | `async void` (return `Task`) / `ConfigureAwait(false)` (remove it) |

- **Don't make `Backend`'s public members `static`**: QML only sees instance members. (.NET's "can be static" rule, CA1822, is already off for them.)
- A backend that owns a timer, a file watcher or a process implements `IDisposable`; the bridge calls `Dispose()` when the app exits (window closed, or the shell closed the panel). QML doesn't see `Dispose`.
- When a rule is wrong for one member, turn it off right there with the reason: `[System.Diagnostics.CodeAnalysis.SuppressMessage("Omasharp", "OMASHARP003", Justification = "only the tests call it")]`, or `#pragma warning disable OMASHARP003` / `#pragma warning restore OMASHARP003` around the lines. For the whole app, `.editorconfig` (`dotnet_diagnostic.ID.severity = none`, with the reason in a comment). Never to make a build pass.

## Native AOT rules (release builds)

The same rules as any native AOT .NET app:

- No `Reflection.Emit`, no `dynamic`, no loading assemblies at run time.
- `System.Text.Json`: use a source-generated `JsonSerializerContext` (`[JsonSerializable(typeof(MyType))]`). Reflection-based serialization fails or gets trimmed.
- NuGet packages must be trim/AOT-safe. IL2xxx/IL3xxx warnings say they aren't: they fail every build (and `check.sh --release` checks the native AOT build's own), because they're crashes waiting to happen.
- Globalization is invariant (no culture-specific formatting, smaller and faster). For culture data, set `<InvariantGlobalization>false</InvariantGlobalization>` in the csproj.
