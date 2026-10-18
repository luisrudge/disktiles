#if OMASHARP_NATIVE
// Window transport: the C++ host loads this assembly in-process, either as a
// native AOT libapp.so (release) or through hostfxr on the normal .NET runtime
// (dev mode), and calls through the table returned by omasharp_api().
// Must match bridge/host/omasharp.h.
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;

namespace Omasharp;

[StructLayout(LayoutKind.Sequential)]
unsafe struct HostApi
{
    public delegate* unmanaged<byte*, byte*, void> Notify; // name, JSON value; UI thread only
    public delegate* unmanaged<long, void> Post;           // any thread; host calls RunPosted(token) on the UI thread
    public delegate* unmanaged<byte*, void> Method;        // dev hot reload: a method appeared on Backend
    public delegate* unmanaged<byte*, byte*, void> Raised; // name, JSON value; UI thread only
    public byte* Args;                                     // JSON array of the app's arguments
    public delegate* unmanaged<byte*, byte*, int, void> Draw; // Bridge.Show: name, Drawing buffer, length; UI thread
    public double MonoAdvance;                             // monospace character width per pixel of font size
    public delegate* unmanaged<long, byte*, int, void> Resolve; // async call id, JSON value or error message, isError
}

[StructLayout(LayoutKind.Sequential)]
unsafe struct Api
{
    public int Version;
    public delegate* unmanaged<byte*> AppInfo;                      // {"id":..,"name":..}
    public delegate* unmanaged<HostApi*, byte*> Start;              // creates Backend, returns Describe() JSON
    public delegate* unmanaged<byte*, int*, byte*> Get;             // name, out isError -> JSON value | message
    public delegate* unmanaged<byte*, byte*, int*, byte*> Set;      // name, JSON value, out isError -> null | message
    public delegate* unmanaged<byte*, byte*, int*, byte*> Call;     // name, JSON args array, out isError -> JSON | message
    public delegate* unmanaged<byte*, void> Free;
    public delegate* unmanaged<long, void> RunPosted;
    public delegate* unmanaged<void> Shutdown;
}

static unsafe class NativeExports
{
    const int Abi = 3; // OMASHARP_ABI in omasharp.h
    static Api* s_api;
    static HostApi s_host;
    static Model? s_model;
    static object? s_backend;
    static readonly ConcurrentDictionary<long, Action> s_posted = new();
    static long s_nextToken;

    [UnmanagedCallersOnly(EntryPoint = "omasharp_api")]
    public static Api* GetApi() => CreateApi();

    internal static Api* CreateApi()
    {
        if (s_api != null) return s_api;
        var api = (Api*)NativeMemory.AllocZeroed((nuint)sizeof(Api));
        api->Version = Abi;
        api->AppInfo = &AppInfoJson;
        api->Start = &Start;
        api->Get = &Get;
        api->Set = &Set;
        api->Call = &Call;
        api->Free = &Free;
        api->RunPosted = &RunPosted;
        api->Shutdown = &Shutdown;
        return s_api = api;
    }

    [UnmanagedCallersOnly]
    static byte* AppInfoJson() =>
        Utf8($"{{\"id\":{Json.Encode(Omasharp.AppInfo.Id)},\"name\":{Json.Encode(Omasharp.AppInfo.Name)},\"runtime\":{Json.Encode(Bridge.IsNativeAot ? "aot" : "jit")}}}");

    [UnmanagedCallersOnly]
    static byte* Start(HostApi* host)
    {
        s_host = *host;
        Ui.Init(action =>
        {
            var token = Interlocked.Increment(ref s_nextToken);
            s_posted[token] = action;
            s_host.Post(token);
        });
        if (host->Args != null)
        {
            using var args = System.Text.Json.JsonDocument.Parse(Str(host->Args));
            Bridge.Args = [.. args.RootElement.EnumerateArray().Select(a => a.GetString() ?? "")];
        }
        if (host->MonoAdvance > 0) Drawing.MonoAdvance = host->MonoAdvance;
        Bridge.DrawSink = (name, bytes) =>
        {
            var n = Utf8(name);
            try { fixed (byte* b = bytes) s_host.Draw(n, b, bytes.Length); }
            finally { NativeMemory.Free(n); }
        };
        Bridge.RaiseSink = (name, value) =>
        {
            var n = Utf8(name);
            var v = Utf8(value);
            try { s_host.Raised(n, v); } finally { NativeMemory.Free(n); NativeMemory.Free(v); }
        };
        try
        {
            s_backend = new AppBackend();
            s_model = CreateModel();
            return Utf8(s_model.Describe());
        }
        catch (Exception e)
        {
            Log.Error("starting Backend", e);
            return null;
        }
    }

    static Model CreateModel()
    {
        var model = new Model(typeof(AppBackend), s_backend!);
        model.Changed += (name, value) =>
        {
            var n = Utf8(name);
            var v = Utf8(value);
            try { s_host.Notify(n, v); } finally { NativeMemory.Free(n); NativeMemory.Free(v); }
        };
        return model;
    }

    // Dev hot reload: Backend may have new members. Look again, tell the host about
    // new methods, and resend every property (new ones included).
    internal static void Reload()
    {
        if (s_backend == null) return;
        s_model?.Detach();
        s_model = CreateModel();
        Console.Error.WriteLine($"omasharp: Backend now has {s_model.PropertyNames.Count()} properties, {s_model.MethodNames.Count()} methods");
        foreach (var name in s_model.MethodNames)
        {
            var n = Utf8(name);
            try { s_host.Method(n); } finally { NativeMemory.Free(n); }
        }
        s_model.NotifyAll();
    }

    [UnmanagedCallersOnly]
    static byte* Get(byte* name, int* isError) => Guard(isError, () => s_model!.Get(Str(name)));

    [UnmanagedCallersOnly]
    static byte* Set(byte* name, byte* json, int* isError) =>
        Guard(isError, () => { s_model!.Set(Str(name), Str(json)); return null; });

    // isError: 0 = value, 1 = error message, 2 = still running: the value is an id that
    // host->Resolve gets once the task finishes (QML sees a Promise).
    [UnmanagedCallersOnly]
    static byte* Call(byte* name, byte* args, int* isError)
    {
        Task? pending = null;
        var r = Guard(isError, () => s_model!.Call(Str(name), Str(args), out pending) ?? "null");
        if (pending == null || *isError != 0) return r;
        NativeMemory.Free(r);
        var id = Interlocked.Increment(ref s_nextCall);
        pending.ContinueWith(t => Ui.Post(() =>
        {
            string text; int err;
            try { text = Model.ResultJson(t); err = 0; }
            catch (Exception e) { text = e.Message; err = 1; if (e is not BridgeException) Log.Error("async call", e); }
            var b = Utf8(text);
            try { s_host.Resolve(id, b, err); } finally { NativeMemory.Free(b); }
        }), TaskScheduler.Default);
        *isError = 2;
        return Utf8(id.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    static long s_nextCall;

    [UnmanagedCallersOnly]
    static void Free(byte* p) => NativeMemory.Free(p);

    [UnmanagedCallersOnly]
    static void RunPosted(long token)
    {
        if (s_posted.TryRemove(token, out var action)) Ui.Run(action);
    }

    [UnmanagedCallersOnly]
    static void Shutdown()
    {
        try { (s_backend as IDisposable)?.Dispose(); }
        catch (Exception e) { Log.Error("Dispose", e); }
    }

    static byte* Guard(int* isError, Func<string?> f)
    {
        try
        {
            *isError = 0;
            var r = f();
            return r == null ? null : Utf8(r);
        }
        catch (Exception e)
        {
            *isError = 1;
            if (e is not BridgeException) Log.Error("call", e);
            return Utf8(e.Message);
        }
    }

    static string Str(byte* p) => Marshal.PtrToStringUTF8((nint)p) ?? "";

    static byte* Utf8(string s)
    {
        var n = Encoding.UTF8.GetByteCount(s);
        var p = (byte*)NativeMemory.Alloc((nuint)n + 1);
        Encoding.UTF8.GetBytes(s, new Span<byte>(p, n));
        p[n] = 0;
        return p;
    }
}
#endif
