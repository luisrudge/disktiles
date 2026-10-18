// The app's logic. QML sees this object as `backend`: public properties and methods,
// camelCased (Count -> backend.count, Increment() -> backend.increment()).
// The same file works in a window app and in a shell panel. See the omasharp skill (.agents/skills/omasharp/).
using Omasharp;

public class Backend : BackendBase
{
    int _count;

    // Set() stores the value and tells QML; Notify() covers computed properties.
    public int Count { get => _count; set { if (Set(ref _count, value)) Notify(nameof(Label)); } }

    public string Label => Count == 0 ? "Nothing counted yet" : $"Counted {Count} time{(Count == 1 ? "" : "s")}";

    public void Increment() => Count++;

    public void Reset() => Count = 0;
}
