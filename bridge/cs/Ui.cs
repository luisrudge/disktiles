using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Omasharp;

/// <summary>Base class for Backend: property-change plumbing for QML bindings.</summary>
public abstract class BackendBase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Set a field and tell QML, if the value changed.</summary>
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Notify(name!);
        return true;
    }

    /// <summary>Tell QML these properties changed (use for computed properties). Safe from any thread.</summary>
    protected void Notify(params string[] names)
    {
        foreach (var name in names) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>Tell QML something happened (quit, copy, open a dialog...): QML gets
    /// backend.raised(name, value). Safe from any thread.</summary>
    protected static void Raise(string name, object? value = null) => Bridge.Raise(name, value);
}

public static class Bridge
{
    /// <summary>True in release builds (native AOT), false in dev mode (the normal .NET runtime).</summary>
    public static bool IsNativeAot { get; } = DetectAot();

    /// <summary>The app's command-line arguments, without the program name and the host's own
    /// options (Qt's and dev mode's). Set before Backend is constructed.</summary>
    public static string[] Args { get; internal set; } = [];

    internal static Action<string, string>? RaiseSink { get; set; } // set by the host transport
    internal static Action<string, byte[]>? DrawSink { get; set; } // set by the window host

    /// <summary>Show a Drawing in every QML `DrawView { source: name }` (window apps). Safe from
    /// any thread; the last drawing sent for a name wins.</summary>
    public static void Show(string name, Drawing drawing)
    {
        var bytes = drawing.ToBytes();
        // On the UI thread already: hand it over now. A post waits its turn behind every frame
        // of a running animation (Qt's threaded render loop), which can be several frames.
        if (Ui.IsUiThread) DrawSink?.Invoke(name, bytes);
        else Ui.Post(() => DrawSink?.Invoke(name, bytes));
    }

    /// <summary>Send backend.raised(name, value) to QML. Safe from any thread.</summary>
    public static void Raise(string name, object? value = null)
    {
        var json = Json.Encode(value);
        Ui.Post(() => RaiseSink?.Invoke(name, json));
    }

    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("SingleFile", "IL3000",
        Justification = "Location is empty under native AOT; that's what we test for.")]
    static bool DetectAot() => string.IsNullOrEmpty(typeof(object).Assembly.Location);
}

/// <summary>The UI thread. Backend code runs on it; background work posts back with Ui.Post (or just awaits).</summary>
public static class Ui
{
    static Action<Action>? s_post;
    static int s_threadId;

    // Before Init (unit tests, generating a panel's Backend.qml) every thread counts: Post runs at once.
    public static bool IsUiThread => s_post == null || Environment.CurrentManagedThreadId == s_threadId;

    /// <summary>Run an action on the UI thread. Safe from any thread. Outside the app (unit tests) it runs at once.</summary>
    public static void Post(Action action)
    {
        if (s_post != null) s_post(action);
        else action();
    }

    internal static void Init(Action<Action> post)
    {
        s_post = post;
        s_threadId = Environment.CurrentManagedThreadId;
        SynchronizationContext.SetSynchronizationContext(new UiContext());
    }

    internal static void Run(Action action)
    {
        try { action(); }
        catch (Exception e) { Log.Error("posted action", e); }
    }

    sealed class UiContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) => Ui.Post(() => d(state));
        public override void Send(SendOrPostCallback d, object? state)
        {
            if (IsUiThread) { d(state); return; }
            using var done = new ManualResetEventSlim();
            Ui.Post(() => { try { d(state); } finally { done.Set(); } });
            done.Wait();
        }
        public override SynchronizationContext CreateCopy() => this;
    }
}
