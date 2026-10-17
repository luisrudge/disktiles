// A GTK4 front end on Disktiles.Core: scan a folder, draw it as a treemap
// with a legend, a hover card and a side panel, click into directories. How it is put together: ARCHITECTURE.md.
//
//     disktiles [OPTIONS] [FOLDER]        (default: $HOME; --help lists the options)
using Disktiles;

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
// The window keeps the App alive through its signal handlers.
application.OnActivate += (sender, args) => _ = new App((Gtk.Application)sender, Cli.Current.Root);
// null: GTK must not read the options as its own.
return application.RunWithSynchronizationContext(null);
