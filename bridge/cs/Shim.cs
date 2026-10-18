#if OMASHARP_STDIO
// Writes Backend.qml for shell plugins: a QML object with one property per C#
// property and one function per method, kept in sync over the stdio protocol.
// QML in a panel then reads like QML in a window app: backend.count, backend.increment().
using System.Text;

namespace Omasharp;

static class Shim
{
    public static string Generate(Model model)
    {
        var program = AppInfo.Name;
        var sb = new StringBuilder();
        sb.Append($$"""
            // Generated from Backend.cs by the omasharp bridge. Don't edit: rebuild instead.
            // Runs the C# backend as a child process; a crash there can't take the shell down.
            import QtQuick
            import Quickshell
            import Quickshell.Io

            QtObject {
              id: backend

              // The backend program (native AOT). Default: bin/{{program}} next to this file.
              property string program: decodeURIComponent(Qt.resolvedUrl("bin/{{program}}").toString().replace("file://", ""))
              // Dev mode (dotnet run / dotnet watch): run the JIT build instead.
              readonly property string _devDll: Quickshell.env("OMASHARP_BACKEND_DLL") || ""
              property var command: _devDll ? [Quickshell.env("DOTNET_HOST_PATH") || "dotnet", _devDll] : [program]
              property bool active: true
              readonly property bool ready: _ready
              readonly property bool running: _proc.running
              property string lastError: ""
              signal exited(int exitCode)
              // C# called Raise(name, value).
              signal raised(string name, var value)

              // Through `active`, so the Process keeps its binding to it.
              function restart() { active = false; active = true }


            """);
        foreach (var name in model.PropertyNames)
            sb.Append($"  property {QmlType(model.PropertyType(name))} {name}\n");
        sb.Append('\n');
        foreach (var name in model.PropertyNames)
            if (model.IsWritable(name))
                sb.Append($"  on{char.ToUpperInvariant(name[0])}{name[1..]}Changed: _changed(\"{name}\", {name})\n");
        sb.Append('\n');
        foreach (var name in model.MethodNames)
            sb.Append($"  function {name}() {{ return _call(\"{name}\", Array.prototype.slice.call(arguments)) }}\n");
        sb.Append("""

              // Plumbing below.
              property bool _ready: false
              property bool _applying: false
              property int _nextId: 1
              property var _pending: ({})
              property var _stderr: []

              function _send(msg) {
                if (_proc.running) { _proc.write(JSON.stringify(msg) + "\n"); return true }
                console.warn("Backend: not running, dropped", msg.op, msg.name)
                return false
              }
              function _changed(name, value) { if (!_applying && _ready) _send({ op: "set", name: name, value: value }) }
              function _call(name, args) {
                if (!_proc.running) return Promise.reject(new Error("backend not running: " + (lastError || "it exited")))
                var id = _nextId++
                var p = new Promise(function(resolve, reject) { backend._pending[id] = { resolve: resolve, reject: reject } })
                _send({ op: "call", id: id, name: name, args: args })
                return p
              }
              function _apply(name, value) {
                _applying = true
                try { backend[name] = value } catch (e) { console.warn("Backend:", name, e) }
                _applying = false
              }
              function _receive(line) {
                var msg
                try { msg = JSON.parse(line) } catch (e) { console.warn("Backend: bad line:", line); return }
                if (msg.op === "notify") _apply(msg.name, msg.value)
                else if (msg.op === "raised") backend.raised(msg.name, msg.value)
                else if (msg.op === "hello") {
                  for (var k in msg.props) _apply(k, msg.props[k])
                  _ready = true
                } else if (msg.op === "result" || msg.op === "error") {
                  var p = _pending[msg.id]
                  if (msg.op === "error") { lastError = msg.message; console.warn("Backend:", msg.name, msg.message) }
                  if (!p) return
                  delete _pending[msg.id]
                  if (msg.op === "result") p.resolve(msg.value); else p.reject(new Error(msg.message))
                }
              }

              property Process _proc: Process {
                command: backend.command
                running: backend.active
                stdinEnabled: true
                stdout: SplitParser { onRead: function(line) { backend._receive(line) } }
                stderr: SplitParser {
                  onRead: function(line) {
                    console.warn("Backend:", line)
                    backend._stderr = backend._stderr.concat([line]).slice(-30)
                  }
                }
                onStarted: { backend._stderr = []; backend.lastError = "" }
                onExited: function(exitCode) {
                  backend._ready = false
                  if (exitCode !== 0) {
                    var why = backend._stderr.filter(function(l) { return /^Unhandled exception|^Process terminated/.test(l) })
                    backend.lastError = why.length ? why[0] : "backend exited with code " + exitCode
                  }
                  for (var id in backend._pending) backend._pending[id].reject(new Error("backend exited"))
                  backend._pending = ({})
                  backend.exited(exitCode)
                }
              }
            }

            """);
        return sb.ToString();
    }

    static string QmlType(Type t)
    {
        if (Nullable.GetUnderlyingType(t) != null) return "var"; // int? etc. can be null; int can't
        if (t == typeof(string)) return "string";
        if (t == typeof(bool)) return "bool";
        if (t == typeof(int) || t == typeof(short) || t == typeof(byte)) return "int";
        if (t == typeof(double) || t == typeof(float) || t == typeof(decimal) || t == typeof(long)) return "real";
        return "var";
    }
}
#endif
