// ctrl = / ctrl - / ctrl 0: interface zoom, in fixed steps. GTK has no
// runtime scale for a whole window, so the text scales (the font DPI, which
// every em in the CSS follows), and so do the treemap's header bands and
// labels and the panel.

namespace Disktiles;

static class InterfaceZoom
{
    static readonly double[] Steps = [0.75, 0.875, 1.0, 1.125, 1.25, 1.5, 1.75];
    const int Normal = 2;
    static int s_step = Normal;

    public static double Factor => Steps[s_step];
    public static event Action? Changed;

    public static void Install(App app)
    {
        var settings = Gtk.Settings.GetForDisplay(app.Window.GetDisplay());
        // The desktop's own text scale (Omarchy's, through the settings
        // portal) is the 100% step; -1 is GTK's 96 dpi default.
        var dpi = settings.GtkXftDpi > 0 ? settings.GtkXftDpi : 96 * 1024;
        var defaults = AppState.DefaultLayout;
        app.Sidebar.Widget.GetSizeRequest(out var panelWidth, out _);

        void Set(int step)
        {
            if (step == s_step)
            {
                return;
            }
            s_step = step;
            settings.GtkXftDpi = (int)(dpi * Factor);
            app.Sidebar.Widget.SetSizeRequest((int)(panelWidth * Factor), -1);
            Navigation.ResetPanel(app);
            app.State.SetLayout(app.State.Layout with
            {
                Header = defaults.Header * (float)Factor,
                HeaderInner = defaults.HeaderInner * (float)Factor,
            });
            Changed?.Invoke();
        }
        app.Keys.Bind("ctrl+equal ctrl+plus ctrl+KP_Add", "View", "Interface zoom", () => Set(Math.Min(s_step + 1, Steps.Length - 1)), on: null);
        app.Keys.Bind("ctrl+minus ctrl+KP_Subtract", "View", "Interface zoom", () => Set(Math.Max(s_step - 1, 0)), on: null);
        app.Keys.Bind("ctrl+0 ctrl+KP_0", "View", "Interface zoom", () => Set(Normal), on: null);
    }
}
