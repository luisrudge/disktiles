// A GTK4 front end on Disktiles.Core: scan a folder, draw it as a treemap
// with a legend, a hover card and a side panel, click into directories. How it is put together: ARCHITECTURE.md.
//
//     disktiles [OPTIONS] [FOLDER]        (default: $HOME; --help lists the options)
using System.Runtime.InteropServices;
using Disktiles;

if (OperatingSystem.IsWindows())
{
    WindowsConsole.AttachToParent();
}
if (args.Contains("--help") || args.Contains("-h"))
{
    Console.WriteLine(Cli.Usage);
    return 0;
}
if (args.Contains("--version") || args.Contains("-V"))
{
    Console.WriteLine($"disktiles {Cli.Version}");
    return 0;
}
try
{
    Cli.Current = Cli.Parse(args, PowerPresets.Load());
}
catch (ArgumentException error)
{
    Console.Error.WriteLine($"Error: {error.Message}");
    return 1;
}
// The application id is the Wayland app id: Hyprland's window class, and the desktop entry's name.
var application = Gtk.Application.New("io.github.luisrudge.disktiles", Gio.ApplicationFlags.NonUnique);
var selfCheckFailed = false;
application.OnActivate += (_, _) =>
{
    // Activated again (on macOS, a click on the Dock icon): the window comes forward.
    if (application.GetActiveWindow() is { } window)
    {
        window.Present();
        return;
    }
    // The window keeps the App alive through its signal handlers.
    var app = new App(application, Cli.Current.Root);
    if (Cli.Current.Snapshot is { } png)
    {
        TestHooks.SelfCheck(application, app, png, ok => selfCheckFailed = !ok);
    }
};
// null: GTK must not read the options as its own.
var status = application.RunWithSynchronizationContext(null);
return status != 0 ? status : selfCheckFailed ? 1 : 0;

/// Windows runs disktiles as a GUI program, with no console of its own. Started
/// from a terminal, it prints there (--help, --version, errors) by attaching to
/// that one; output already sent to a file or a pipe stays there.
static partial class WindowsConsole
{
    const int StdOutput = -11; // STD_OUTPUT_HANDLE
    const uint FileTypeUnknown = 0; // what GetFileType says of no handle
    const uint ParentProcess = unchecked((uint)-1); // ATTACH_PARENT_PROCESS

    [LibraryImport("kernel32.dll")]
    private static partial nint GetStdHandle(int which);

    [LibraryImport("kernel32.dll")]
    private static partial uint GetFileType(nint file);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AttachConsole(uint process);

    public static void AttachToParent()
    {
        if (GetFileType(GetStdHandle(StdOutput)) == FileTypeUnknown)
        {
            // Started from Explorer there is no console to attach to.
            _ = AttachConsole(ParentProcess);
        }
    }
}
