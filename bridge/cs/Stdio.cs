#if OMASHARP_STDIO
// Panel transport: the shell plugin runs this program as a child process and
// talks JSON lines over stdin/stdout. A crash here ends this process only.
//
//   shell -> backend  {"op":"get","id":1,"name":"count"}
//                     {"op":"set","name":"count","value":3}
//                     {"op":"call","id":2,"name":"increment","args":[]}
//   backend -> shell  {"op":"hello","props":{...},"writable":[...],"methods":[...]}
//                     {"op":"notify","name":"count","value":3}
//                     {"op":"raised","name":"copied","value":"..."}
//                     {"op":"result","id":2,"value":null}
//                     {"op":"error","id":2,"message":"..."}
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace Omasharp;

static class StdioHost
{
    static StreamWriter s_out = null!;

    static int Main(string[] args)
    {
        if (args is ["--qml-shim", var path])
        {
            var instance = new AppBackend();
            try { File.WriteAllText(path, Shim.Generate(new Model(typeof(AppBackend), instance))); }
            catch (BridgeException e) { Console.Error.WriteLine($"Backend.cs : error OMASHARP001 : {e.Message}"); return 1; } // an MSBuild error line
            finally { (instance as IDisposable)?.Dispose(); }
            return 0;
        }

        // stdout belongs to the protocol; anything the app prints goes to stderr.
        s_out = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        Console.SetOut(Console.Error);

        var queue = new BlockingCollection<Action>();
        Ui.Init(a => { try { queue.Add(a); } catch (InvalidOperationException) { } });

        Bridge.Args = args;
        Bridge.RaiseSink = (name, value) => Send($"{{\"op\":\"raised\",\"name\":{Json.Encode(name)},\"value\":{value}}}");
        var backend = new AppBackend();
        var model = new Model(typeof(AppBackend), backend);
        model.Changed += Notify;
        var hello = model.Describe();
        Send("{\"op\":\"hello\"," + hello[1..]);

        new Thread(() =>
        {
            var stdin = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8);
            string? line;
            while ((line = stdin.ReadLine()) != null)
            {
                var l = line;
                queue.Add(() => Handle(model, l));
            }
            queue.CompleteAdding(); // the shell went away: exit
        }) { IsBackground = true, Name = "stdin" }.Start();

        foreach (var action in queue.GetConsumingEnumerable()) Ui.Run(action);
        // The shell closed stdin: let Backend clean up (timers, watchers, files), then exit.
        try { (backend as IDisposable)?.Dispose(); }
        catch (Exception e) { Log.Error("Dispose", e); }
        Environment.Exit(0);
        return 0;
    }

    static void Send(string json) => s_out.WriteLine(json);

    static void Notify(string name, string value) =>
        Send($"{{\"op\":\"notify\",\"name\":{Json.Encode(name)},\"value\":{value}}}");

    static void Handle(Model model, string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        JsonElement id = default;
        string? name = null;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var msg = doc.RootElement;
            if (msg.TryGetProperty("id", out var i)) id = i.Clone();
            name = msg.TryGetProperty("name", out var n) ? n.GetString() : null;
            var op = msg.GetProperty("op").GetString();
            switch (op)
            {
                case "get":
                    Reply(id, model.Get(name!));
                    break;
                case "set":
                    // Echo what C# kept: a setter may clamp or refuse without raising a change.
                    Notify(name!, model.Set(name!, msg.GetProperty("value").GetRawText()));
                    break;
                case "call":
                    var args = msg.TryGetProperty("args", out var a) ? a.GetRawText() : "[]";
                    var value = model.Call(name!, args, out var pending);
                    if (pending == null) { Reply(id, value ?? "null"); break; }
                    // Async method: answer when it finishes (QML's Promise waits for it).
                    var callId = id; var callName = name;
                    pending.ContinueWith(t => Ui.Post(() =>
                    {
                        try { Reply(callId, Model.ResultJson(t)); }
                        catch (Exception e)
                        {
                            Console.Error.WriteLine($"omasharp: {callName}: {e}");
                            var idJson = callId.ValueKind == JsonValueKind.Undefined ? "null" : callId.GetRawText();
                            Send($"{{\"op\":\"error\",\"id\":{idJson},\"name\":{Json.Encode(callName)},\"message\":{Json.Encode(e.Message)}}}");
                        }
                    }), TaskScheduler.Default);
                    break;
                default:
                    throw new BridgeException($"unknown op '{op}'");
            }
        }
        catch (Exception e)
        {
            var err = e is BridgeException ? e.Message : e.ToString();
            Console.Error.WriteLine($"omasharp: {name}: {err}");
            var idJson = id.ValueKind == JsonValueKind.Undefined ? "null" : id.GetRawText();
            Send($"{{\"op\":\"error\",\"id\":{idJson},\"name\":{Json.Encode(name)},\"message\":{Json.Encode(e.Message)}}}");
            // A rejected set leaves QML holding a value C# never took: send the real one back.
            if (name != null && model.PropertyNames.Contains(name)) Notify(name, model.Get(name));
        }
    }

    static void Reply(JsonElement id, string value)
    {
        if (id.ValueKind == JsonValueKind.Undefined) return;
        Send($"{{\"op\":\"result\",\"id\":{id.GetRawText()},\"value\":{value}}}");
    }
}
#endif
