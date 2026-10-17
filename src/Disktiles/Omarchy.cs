// Omarchy and Hyprland integration (feature 6): the window's identity, the
// Omarchy theme followed live, and the key-hint bar. What Omarchy v4 uses
// and why each choice was made: OMARCHY.md.
using Disktiles.Core;

namespace Disktiles;

static class Omarchy
{
    // The key-hint bar, most useful first so a narrow window clips the
    // least useful: the keycap, what it does, and the key it stands for in
    // the keymap. A hint shows only once some feature binds its key.
    // Windows' monospace font (Consolas) has no ⌫.
    static readonly string BackspaceCap = OperatingSystem.IsWindows() ? "bksp" : "⌫";

    static readonly (string Cap, string Label, string Key)[] KeyBar =
    [
        ("space", "mark", "space"), ("enter", "open", "Return"), (BackspaceCap, "up", "BackSpace"),
        ("c", "review", "c"), ("hjkl", "move", "h"), ("/", "filter", "slash"),
        ("[ ]", "depth", "bracketleft"), ("t", "mode", "t"), ("0", "reset", "0"),
        ("v", "volumes", "v"), ("r", "rescan", "r"),
    ];

    // Kept alive for the life of the app: a collected watcher stops watching.
    static FileSystemWatcher? s_watcher;
    static Timer? s_debounce;

    public static void Install(App app)
    {
        // The title names the root. The app id stays io.github.luisrudge.disktiles
        // (Program.cs): see OMARCHY.md, "Window class".
        app.Window.Title = $"disktiles · {app.State.Tilde(app.State.Root)}";
        app.Sidebar.Widget.AddCssClass("sidebar");
        app.Treemap.Widget.AddCssClass("treemap");

        var css = Gtk.CssProvider.New();
        Gtk.StyleContext.AddProviderForDisplay(app.Window.GetDisplay(), css, Gtk.Constants.STYLE_PROVIDER_PRIORITY_APPLICATION);
        Apply(app, css, Load());
        if (OmarchyTheme.CurrentDir(app.State.Home) is { } dir && SynchronizationContext.Current is { } ui)
        {
            Follow(dir, ui, () =>
            {
                var next = Load();
                if (next != Look.Theme)
                {
                    Apply(app, css, next);
                }
            });
        }

        // The texture is painted at the surface's scale; moving to a monitor
        // with another scale (the laptop's 2x, an external 1x, a fractional
        // one) changes the scale without a resize, so repaint on it too.
        app.Window.GetSurface()?.OnNotify += (_, args) =>
        {
            if (args.Pspec.GetName() == "scale")
            {
                if (TestHooks.Trace)
                {
                    Console.Error.WriteLine($"scale: {app.Window.GetSurface()?.GetScale()}, treemap {app.Treemap.Width}x{app.Treemap.Height}");
                }
                app.Treemap.Render();
            }
        };

        // After every feature has bound its keys.
        GLib.Functions.IdleAdd(GLib.Constants.PRIORITY_DEFAULT_IDLE, () =>
        {
            KeyHints(app);
            return false;
        });
    }

    static OmarchyTheme Load() => OmarchyTheme.Current(Environment.GetEnvironmentVariable("HOME"),
        // ponytail: off Omarchy, GTK's colour-scheme setting read once at each load, not followed live.
        () => Gtk.Settings.GetDefault()?.GtkInterfaceColorScheme == Gtk.InterfaceColorScheme.Dark
            || Gtk.Settings.GetDefault()?.GtkApplicationPreferDarkTheme == true);

    static void Apply(App app, Gtk.CssProvider css, OmarchyTheme theme)
    {
        Look.Theme = theme;
        css.LoadFromString(Css(theme));
        // GTK's own widgets (scrollbars, dialogs) light or dark to match.
        Gtk.Settings.GetDefault()?.GtkInterfaceColorScheme = theme.Dark ? Gtk.InterfaceColorScheme.Dark : Gtk.InterfaceColorScheme.Light;
        // Cairo-painted parts do not restyle: the treemap texture and every
        // swatch repaint once.
        app.Treemap.Render();
        Redraw(app.Window);
        if (TestHooks.Trace)
        {
            Console.Error.WriteLine($"theme: {theme.Name} ({(theme.Dark ? "dark" : "light")}) background {theme.Background.Css} accent {theme.Accent.Css}");
        }

        static void Redraw(Gtk.Widget widget)
        {
            if (widget is Gtk.DrawingArea)
            {
                widget.QueueDraw();
            }
            for (var child = widget.GetFirstChild(); child is not null; child = child.GetNextSibling())
            {
                Redraw(child);
            }
        }
    }

    // The theme reloads on file-system events under the `current`
    // directory: `omarchy theme set` stages `current/next-theme`, swaps it in
    // for `current/theme` and rewrites `current/theme.name`. A burst of
    // events settles for 200 ms, then the theme is read on the UI thread.
    static void Follow(string dir, SynchronizationContext ui, Action reload)
    {
        var debounce = s_debounce = new Timer(_ => ui.Post(_ => reload(), null));
        s_watcher = new FileSystemWatcher(dir)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
        };
        void Settle(object? sender, EventArgs e) => debounce.Change(200, Timeout.Infinite);
        s_watcher.Changed += Settle;
        s_watcher.Created += Settle;
        s_watcher.Deleted += Settle;
        s_watcher.Renamed += Settle;
        s_watcher.Error += (_, e) => Console.Error.WriteLine($"Cannot watch Omarchy theme: {e.GetException().Message}");
        try
        {
            s_watcher.EnableRaisingEvents = true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Cannot watch Omarchy theme: {error.Message}");
        }
    }

    static void KeyHints(App app)
    {
        bool Bound(string key) => app.Keys.Bindings.Any(b => b.On is null or Screen.Explore && b.Alternatives.Contains(key));
        foreach (var (cap, label, key) in KeyBar)
        {
            if (Bound(key))
            {
                app.Footer.AddHint(Footer.Hint(cap, label));
                if (TestHooks.Trace)
                {
                    Console.Error.WriteLine($"footer: {cap} {label}");
                }
            }
        }
        if (Bound("question"))
        {
            app.Footer.End.Append(Footer.Hint("?", "all keys"));
        }
    }

    // The theme as named colours (@dt_*) every other stylesheet refers to,
    // and the surfaces: the window on the background, the
    // panel on the surface, the treemap on the inset, hairlines at 12% of
    // the foreground, controls as Omarchy's shell draws them (shell.toml's
    // fill and border alphas), and square corners, as Omarchy's surfaces are.
    static string Css(OmarchyTheme t)
    {
        var css = new System.Text.StringBuilder();
        foreach (var (name, colour) in new[]
        {
            ("background", t.Background), ("surface", t.Surface), ("inset", t.Inset),
            ("foreground", t.Foreground), ("secondary", t.Secondary), ("bright", t.Bright),
            ("accent", t.Accent), ("on_accent", t.OnAccent), ("selection", t.Selection), ("border", t.Border),
            ("danger", t.Danger), ("warning", t.Warning), ("success", t.Success),
            ("highlight", Look.Highlight), ("on_highlight", Look.OnHighlight),
        })
        {
            css.Append($"@define-color dt_{name} {colour.Css};\n");
        }
        css.Append("""
            window.background { background-color: @dt_background; color: @dt_foreground; }
            .sidebar { background-color: @dt_surface; }
            .treemap { background-color: @dt_inset; }
            separator, paned > separator { background-color: alpha(@dt_foreground, 0.12); min-width: 1px; min-height: 1px; }
            .footer { border-top: 1px solid alpha(@dt_foreground, 0.12); padding: 4px 16px; }
            .keycap { font-family: monospace; font-weight: bold; font-size: 0.85em; padding: 1px 4px;
                      border: 1px solid @dt_border; background-color: @dt_inset; color: @dt_foreground; }
            .hint, .footer-status { color: @dt_secondary; font-size: 0.9em; }
            .footer-status { color: alpha(@dt_secondary, 0.7); }
            selection { background-color: alpha(@dt_foreground, 0.35); }
            button, entry, popover > contents, tooltip, row, progressbar > trough, progressbar > trough > progress, .card { border-radius: 0; }
            button { background-image: none; background-color: alpha(@dt_foreground, 0.04); color: @dt_foreground;
                     border: 1px solid alpha(@dt_foreground, 0.4); box-shadow: none; }
            button:hover { background-color: alpha(@dt_foreground, 0.08); }
            button:checked, button:active { background-color: alpha(@dt_foreground, 0.18); }
            button.flat { background-color: transparent; border-color: transparent; }
            /* Amber is the main action, what Enter does; danger the one that cannot be undone. */
            button.suggested-action { background-color: @dt_highlight; color: @dt_on_highlight; border-color: @dt_highlight; }
            button.suggested-action:hover { background-color: alpha(@dt_highlight, 0.85); }
            button.destructive-action { background-color: alpha(@dt_danger, 0.12); color: @dt_danger; border-color: alpha(@dt_danger, 0.6); }
            button.destructive-action:hover { background-color: alpha(@dt_danger, 0.2); }
            entry { background-color: alpha(@dt_foreground, 0.04); color: @dt_foreground;
                    border: 1px solid alpha(@dt_foreground, 0.4); box-shadow: none; outline: none; caret-color: @dt_foreground; }
            entry:focus-within { background-color: alpha(@dt_foreground, 0.08); }
            popover > contents, tooltip { background-color: @dt_background; color: @dt_foreground;
                                          border: 1px solid alpha(@dt_foreground, 0.4); box-shadow: none; }
            row:hover { background-color: alpha(@dt_foreground, 0.08); }
            row:selected { background-color: alpha(@dt_foreground, 0.18); color: @dt_foreground; }
            scrollbar slider { background-color: alpha(@dt_foreground, 0.3); }
            progressbar > trough { background-color: alpha(@dt_foreground, 0.08); }
            progressbar > trough > progress { background-color: @dt_accent; }

            """);
        foreach (var category in Enum.GetValues<Category>())
        {
            css.Append($"progressbar.cat-{category} > trough > progress {{ background-color: {Look.Css(category)}; }}\n");
        }
        return css.ToString();
    }
}
