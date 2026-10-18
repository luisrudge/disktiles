#if OMASHARP_NATIVE && OMASHARP_DEV
// Dev mode (Debug builds of window apps): `dotnet run` / `dotnet watch` start the
// omasharp host, which runs this Main on the normal .NET runtime through hostfxr.
// Main hands the bridge's function table back to the host, which then runs Qt on
// this (the main) thread. Hot reload (`dotnet watch`) patches Backend code in place;
// afterwards the bridge looks at Backend again (new properties and methods show up)
// and re-sends every property so the window shows the new values.
using System.Runtime.InteropServices;

[assembly: System.Reflection.Metadata.MetadataUpdateHandler(typeof(Omasharp.DevMain))]

namespace Omasharp;

static unsafe class DevMain
{
    static int Main()
    {
        if (!NativeLibrary.TryGetExport(NativeLibrary.GetMainProgramHandle(), "omasharp_host_run", out var run))
        {
            Console.Error.WriteLine("Start this app with `dotnet run` or `dotnet watch` (it needs the omasharp host).");
            return 1;
        }
        return ((delegate* unmanaged<Api*, int>)run)(NativeExports.CreateApi());
    }

    // Called by the runtime after `dotnet watch` applies a hot reload delta.
    internal static void UpdateApplication(Type[]? types)
    {
        Console.Error.WriteLine("omasharp: C# hot reload applied");
        Ui.Post(NativeExports.Reload);
    }
}
#endif
