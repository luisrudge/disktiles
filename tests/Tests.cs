// Tests for Backend.cs. Plain C#: no Qt, no window. Run: dotnet run --project tests
// The bridge code refers to the app's backend class as AppBackend (the app build generates
// this alias from <BackendType> in the csproj; the tests have to say it themselves).
global using AppBackend = Backend;

var failures = 0;
void Check(string name, bool ok)
{
    Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {name}");
    if (!ok) failures++;
}

// These checks are for the starter counter: replace them with checks for your app's logic.
var b = new Backend();
Check("starts at zero", b.Count == 0 && b.Label == "Nothing counted yet");
b.Increment();
b.Increment();
Check("counts", b.Count == 2 && b.Label == "Counted 2 times");
b.Reset();
Check("resets", b.Count == 0);

Console.WriteLine(failures == 0 ? "all passed" : $"{failures} failed");
return failures == 0 ? 0 : 1;
