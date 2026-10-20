#if OMASHARP_NATIVE && OMASHARP_DEV
// Dev mode (Debug builds of window apps): `dotnet run` and the skill's check.sh start
// the Omasharp host, which runs this Main on the normal .NET runtime through hostfxr.
// Main hands the bridge's function table back to the host, which then runs Qt on
// this (the main) thread.
using System.Runtime.InteropServices;

namespace Omasharp;

static unsafe class DevMain
{
    static int Main()
    {
        if (!NativeLibrary.TryGetExport(NativeLibrary.GetMainProgramHandle(), "omasharp_host_run", out var run))
        {
            Console.Error.WriteLine("Start this app with `dotnet run` (it needs the Omasharp host).");
            return 1;
        }
        return ((delegate* unmanaged<Api*, int>)run)(NativeExports.CreateApi());
    }
}
#endif
