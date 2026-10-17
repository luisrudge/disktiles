// `o`, right-click and ctrl+shift+R: show the tile in the file manager. GTK's
// file launcher uses the desktop portal's OpenDirectory, which on Omarchy
// hands the file to Nautilus (org.freedesktop.FileManager1) with it selected;
// without a portal it asks FileManager1 itself, then opens the folder. On
// Windows and macOS GTK uses Explorer and Finder. No process of our own, no
// D-Bus code.
using Disktiles.Core;

namespace Disktiles;

static class FileManager
{
    /// DISKTILES_REVEAL=dry logs what would be shown instead of opening a
    /// window: headless tests must not put Nautilus on the user's screen.
    static readonly bool Dry = TestHooks.Env("DISKTILES_REVEAL") == "dry";

    public static void Install(App app)
    {
        var where = OperatingSystem.IsMacOS() ? "Finder" : OperatingSystem.IsWindows() ? "File Explorer" : "the file manager";
        app.Keys.Bind("o ctrl+R", "Navigate", $"Show it in {where}", () =>
        {
            Reveal(app, app.State.ActionTarget ?? app.State.Crumbs);
        });
    }

    public static async void Reveal(App app, int[] crumbs)
    {
        var path = app.State.PathOf(crumbs);
        // The portal cannot say a path is gone, so check what it can't.
        if (!Path.Exists(path) && Space.LinkTarget(path) is null)
        {
            app.Footer.SetStatus($"{app.State.Tilde(path)} is no longer on disk");
            return;
        }
        if (Dry)
        {
            Console.Error.WriteLine($"reveal: {path}");
            return;
        }
        try
        {
            await Gtk.FileLauncher.New(Gio.FileHelper.NewForPath(path)).OpenContainingFolderAsync(app.Window);
        }
        catch (Exception error)
        {
            app.Footer.SetStatus($"Could not show {app.State.Tilde(path)}: {error.Message}");
        }
    }
}
