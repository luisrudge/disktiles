// omasharp bridge: exposes the app's Backend class to QML.
// Values cross the boundary as JSON in both transports (in-process C ABI and
// stdio JSON lines), so Backend.cs is the same for window apps and panels.
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace Omasharp;

sealed class Model
{
    const DynamicallyAccessedMemberTypes Members =
        DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicMethods;

    readonly object _target;
    readonly Dictionary<string, PropertyInfo> _props = new();
    readonly Dictionary<string, string> _qmlNames = new(); // C# name -> QML name
    readonly Dictionary<string, MethodInfo[]> _methods = new();

    public event Action<string, string>? Changed; // QML name, JSON value; always on the UI thread

    public Model([DynamicallyAccessedMembers(Members)] Type type, object target)
    {
        _target = target;
        foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (p.GetMethod is not { IsPublic: true } || p.GetIndexParameters().Length > 0) continue;
            var name = QmlName(p.Name);
            CheckName(type, p.Name, name);
            _props[name] = p;
            _qmlNames[p.Name] = name;
        }
        foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.Instance))
        {
            if (m.IsSpecialName || m.IsGenericMethodDefinition || m.DeclaringType == typeof(object)) continue;
            // Dispose() is the bridge's to call (at exit), not QML's.
            if (m.Name == nameof(IDisposable.Dispose) && m.GetParameters().Length == 0 && target is IDisposable) continue;
            var name = QmlName(m.Name);
            CheckName(type, m.Name, name);
            _methods[name] = _methods.TryGetValue(name, out var list) ? [.. list, m] : [m];
        }
        if (target is INotifyPropertyChanged npc)
            npc.PropertyChanged += OnTargetPropertyChanged;
    }

    void OnTargetPropertyChanged(object? sender, PropertyChangedEventArgs e) => OnPropertyChanged(e.PropertyName);

    /// <summary>Stop listening to the target (dev hot reload replaces the model).</summary>
    public void Detach()
    {
        if (_target is INotifyPropertyChanged npc) npc.PropertyChanged -= OnTargetPropertyChanged;
    }

    public IEnumerable<string> PropertyNames => _props.Keys;
    public IEnumerable<string> MethodNames => _methods.Keys;
    public bool IsWritable(string name) => _props.TryGetValue(name, out var p) && p.SetMethod is { IsPublic: true };
    public Type PropertyType(string name) => _props[name].PropertyType;

    static string QmlName(string name) => QmlNames.Of(name);

    static void CheckName(Type type, string csName, string qmlName)
    {
        if (QmlNames.IsReserved(qmlName)) throw new BridgeException(QmlNames.ReservedMessage(type.Name, csName, qmlName));
    }

    public void NotifyAll() => OnPropertyChanged(null);

    void OnPropertyChanged(string? csName)
    {
        if (!Ui.IsUiThread) { Ui.Post(() => OnPropertyChanged(csName)); return; }
        if (string.IsNullOrEmpty(csName))
        {
            foreach (var name in _props.Keys) Changed?.Invoke(name, Get(name));
            return;
        }
        if (_qmlNames.TryGetValue(csName, out var qml)) Changed?.Invoke(qml, Get(qml));
    }

    // {"props":{"count":1,...},"writable":["count"],"methods":["increment"]}
    public string Describe()
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteStartObject("props");
            foreach (var (name, p) in _props) { w.WritePropertyName(name); Json.Write(w, SafeGet(p)); }
            w.WriteEndObject();
            w.WriteStartArray("writable");
            foreach (var name in _props.Keys) if (IsWritable(name)) w.WriteStringValue(name);
            w.WriteEndArray();
            w.WriteStartArray("methods");
            foreach (var name in _methods.Keys) w.WriteStringValue(name);
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    object? SafeGet(PropertyInfo p)
    {
        try { return p.GetValue(_target); }
        catch (TargetInvocationException e) { Log.Error($"get {p.Name}", e.InnerException ?? e); return null; }
    }

    public string Get(string name) =>
        _props.TryGetValue(name, out var p) ? Json.Encode(SafeGet(p)) : throw new BridgeException($"no property '{name}'");

    /// <summary>Sets a property and returns the value C# ended up with (a setter may clamp or refuse).</summary>
    public string Set(string name, string json)
    {
        if (!_props.TryGetValue(name, out var p)) throw new BridgeException($"no property '{name}'");
        if (p.SetMethod is not { IsPublic: true }) throw new BridgeException($"property '{name}' is read-only");
        using var doc = JsonDocument.Parse(json);
        Invoke(() => p.SetValue(_target, Json.Convert(doc.RootElement, p.PropertyType)));
        return Get(name);
    }

    // argsJson is a JSON array. Returns the JSON-encoded result ("null" for void). For async
    // methods (Task, Task<T>) it returns null and hands back the running task: the transport
    // resolves QML's Promise when it finishes (ResultJson gives its value).
    public string Call(string name, string argsJson) => Call(name, argsJson, out _) ?? "null";

    public string? Call(string name, string argsJson, out Task? pending)
    {
        pending = null;
        if (!_methods.TryGetValue(name, out var overloads)) throw new BridgeException($"no method '{name}'");
        using var doc = JsonDocument.Parse(argsJson);
        var args = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.EnumerateArray().ToArray() : [];
        var m = overloads.FirstOrDefault(o => o.GetParameters().Length == args.Length)
            ?? overloads.FirstOrDefault(o => o.GetParameters().Count(x => !x.IsOptional) <= args.Length && o.GetParameters().Length >= args.Length)
            ?? throw new BridgeException($"'{name}' does not take {args.Length} argument(s)");
        var ps = m.GetParameters();
        var values = new object?[ps.Length];
        for (int i = 0; i < ps.Length; i++)
            values[i] = i < args.Length ? Json.Convert(args[i], ps[i].ParameterType) : ps[i].DefaultValue;
        object? result = null;
        Invoke(() => result = m.Invoke(_target, values));
        if (result is Task task)
        {
            pending = task;
            return null;
        }
        return Json.Encode(result);
    }

    /// <summary>A finished task's value as JSON ("null" for Task), or throws its exception.</summary>
    public static string ResultJson(Task task)
    {
        if (task.IsFaulted) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(task.Exception!.InnerException ?? task.Exception).Throw();
        if (task.IsCanceled) throw new OperationCanceledException();
        return Json.Encode(TaskResult(task));
    }

    // Task<T>.Result without knowing T. The runtime type of an async method's task is a subclass
    // (and a plain Task is a Task<VoidTaskResult> inside), so look for Task<T> up the chain.
    [UnconditionalSuppressMessage("Trimming", "IL2075", Justification = "Task<>.Result is kept by the DynamicDependency.")]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicProperties, typeof(Task<>))]
    static object? TaskResult(Task task)
    {
        for (var t = task.GetType(); t != null && t != typeof(Task); t = t.BaseType)
            if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Task<>))
            {
                var arg = t.GetGenericArguments()[0];
                if (arg.Name == "VoidTaskResult") return null;
                return t.GetProperty("Result")?.GetValue(task);
            }
        return null;
    }

    static void Invoke(Action a)
    {
        try { a(); }
        catch (TargetInvocationException e) when (e.InnerException != null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerException).Throw();
        }
    }
}

sealed class BridgeException(string message) : Exception(message);

static class Log
{
    public static void Error(string what, Exception e) => Console.Error.WriteLine($"omasharp: {what}: {e}");
}

static class Json
{
    public static string Encode(object? value)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms)) Write(w, value);
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    public static void Write(Utf8JsonWriter w, object? value) => Write(w, value, 0);

    static void Write(Utf8JsonWriter w, object? value, int depth)
    {
        if (depth > 32) { w.WriteNullValue(); return; } // a cycle, most likely
        switch (value)
        {
            case null: w.WriteNullValue(); break;
            case string s: w.WriteStringValue(s); break;
            case bool b: w.WriteBooleanValue(b); break;
            case int i: w.WriteNumberValue(i); break;
            case long l: w.WriteNumberValue(l); break;
            case double d: if (double.IsFinite(d)) w.WriteNumberValue(d); else w.WriteNullValue(); break;
            case float f: if (float.IsFinite(f)) w.WriteNumberValue(f); else w.WriteNullValue(); break;
            case decimal m: w.WriteNumberValue(m); break;
            case uint u: w.WriteNumberValue(u); break;
            case ulong ul: w.WriteNumberValue(ul); break;
            case ushort us: w.WriteNumberValue(us); break;
            case sbyte sb: w.WriteNumberValue(sb); break;
            case short s16: w.WriteNumberValue(s16); break;
            case byte u8: w.WriteNumberValue(u8); break;
            case Enum e: w.WriteStringValue(e.ToString()); break;
            case DateTime dt: w.WriteStringValue(dt); break;
            case System.Collections.IDictionary dict:
                w.WriteStartObject();
                foreach (System.Collections.DictionaryEntry kv in dict) { w.WritePropertyName(kv.Key.ToString()!); Write(w, kv.Value, depth + 1); }
                w.WriteEndObject();
                break;
            case System.Collections.IEnumerable seq:
                w.WriteStartArray();
                foreach (var item in seq) Write(w, item, depth + 1);
                w.WriteEndArray();
                break;
            default: WriteObject(w, value, depth); break;
        }
    }

    static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, (string, PropertyInfo)[]> s_shapes = new();

    [UnconditionalSuppressMessage("Trimming", "IL2070", Justification = "The app assembly is rooted (TrimmerRootAssembly).")]
    static (string, PropertyInfo)[] Shape(Type type) =>
        [.. type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetMethod is { IsPublic: true } && p.GetIndexParameters().Length == 0)
            .Select(p => (char.ToLowerInvariant(p.Name[0]) + p.Name[1..], p))];

    // The app's own classes and records become JS objects: { publicProperty: value, ... }.
    // Safe under native AOT because Omasharp.targets roots the app's assembly (its types keep
    // their property metadata). Framework types we don't know fall back to ToString().
    [UnconditionalSuppressMessage("Trimming", "IL2075", Justification = "The app assembly is rooted (TrimmerRootAssembly).")]
    static void WriteObject(Utf8JsonWriter w, object value, int depth)
    {
        var type = value.GetType();
        if (type.Assembly != typeof(AppBackend).Assembly) { w.WriteStringValue(value.ToString()); return; }
        w.WriteStartObject();
        foreach (var (name, p) in s_shapes.GetOrAdd(type, Shape))
        {
            w.WritePropertyName(name);
            object? v;
            try { v = p.GetValue(value); } catch { v = null; }
            Write(w, v, depth + 1);
        }
        w.WriteEndObject();
    }

    public static object? Convert(JsonElement e, Type type)
    {
        var t = Nullable.GetUnderlyingType(type) ?? type;
        if (e.ValueKind == JsonValueKind.Null || e.ValueKind == JsonValueKind.Undefined)
            return t.IsValueType && t == type ? throw new BridgeException($"null is not a {type.Name}") : null;
        if (t == typeof(string)) return e.ValueKind == JsonValueKind.String ? e.GetString() : e.GetRawText();
        if (t == typeof(bool)) return e.GetBoolean();
        if (t == typeof(int)) return (int)Math.Round(e.GetDouble());
        if (t == typeof(long)) return (long)Math.Round(e.GetDouble());
        if (t == typeof(short) || t == typeof(ushort) || t == typeof(byte) || t == typeof(sbyte) || t == typeof(uint) || t == typeof(ulong))
            return System.Convert.ChangeType(Math.Round(e.GetDouble()), t, System.Globalization.CultureInfo.InvariantCulture);
        if (t == typeof(double)) return e.GetDouble();
        if (t == typeof(float)) return (float)e.GetDouble();
        if (t == typeof(decimal)) return e.GetDecimal();
        if (t == typeof(object)) return Natural(e);
        if (t.IsEnum) return e.ValueKind == JsonValueKind.String ? Enum.Parse(t, e.GetString()!, true) : Enum.ToObject(t, e.GetInt64());
        if (t == typeof(string[])) return e.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() : x.GetRawText()).ToArray();
        if (t == typeof(double[])) return e.EnumerateArray().Select(x => x.GetDouble()).ToArray();
        if (t == typeof(int[])) return e.EnumerateArray().Select(x => (int)Math.Round(x.GetDouble())).ToArray();
        throw new BridgeException($"can't convert JSON to {type.Name}");
    }

    static object? Natural(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString(),
        JsonValueKind.Number => e.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Array => e.EnumerateArray().Select(Natural).ToList(),
        JsonValueKind.Object => e.EnumerateObject().ToDictionary(p => p.Name, p => Natural(p.Value)),
        _ => null,
    };
}
