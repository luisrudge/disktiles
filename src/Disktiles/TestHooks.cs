// Switches for measuring and testing without a mouse or a keyboard, all from
// the environment:
//
//   DISKTILES_FPS=1          the frame-rate overlay (f toggles it), and one line a second to stderr
//   DISKTILES_TRACE=1        every pointer event and what the hover made of it, to stderr
//   DISKTILES_HOVER=x,y      a pointer parked at one point
//   DISKTILES_SWEEP=1        a pointer that moves every frame (=nopanel: leave the panel alone)
//   DISKTILES_CLICK=x,y[,b[,n]]  a press at (x, y), button b (1 default, 3 right, 8/9 back/forward),
//                           n its press count (2: a double click); ";" between several
//   DISKTILES_POINT=x,y      a pointer that moved to (x, y): keys act on the tile under it
//   DISKTILES_REVEAL=dry     log what `o` and right-click would show instead of opening the file manager
//   DISKTILES_SCROLL=x,y,dy[,shift][,N][;...]  a scroll step of dy notches at (x, y), N times; ; for more
//   DISKTILES_ZOOMSWEEP=1    a wheel spun in and out at a slowly moving point, 15 notches/s
//                           (=magnify: magnify and shrink only, never going in or up)
//   DISKTILES_KEYS="k k …"   key presses through the keymap: "Down Down Return", "ctrl+o"
//   DISKTILES_LOG=1          what scans, modes and targets do, one line each to stderr
//   DISKTILES_PICK=/path     ctrl+o picks this folder instead of opening the portal's chooser
//                           (a portal window would open on the focused, real screen)
//   DISKTILES_CRUMB=n[,menu] press the n-th crumb of the trail (from 0), or open its ▾ menu
//   DISKTILES_CTRLCLICK=x,y  a ctrl-click at (x, y): marks the tile there (Marking.Click); ";" between several
//   DISKTILES_SAVE_TO=file   the review's "s" writes the list here instead of asking where (Review.cs)
//   DISKTILES_CLIPBOARD_TO=file  the review's "a" writes the prompt here, never to the clipboard (Review.cs)
//   DISKTILES_DRIVE=fifo     commands from a pipe as they arrive: "hover X Y" (window pixels), "key CHORD"
//   DISKTILES_SNAP=file.png[,s]  the window rendered to a PNG by GTK itself, s seconds (default 2) after
//                           the steps: a picture even when the screen is locked, at the window's scale
//
// Coordinates are logical pixels in the treemap. After the first scan the
// click, the ctrl-click, the scroll, the crumb, then the keys play, ~150 ms
// apart, each logged to stderr.
//
// Only DISKTILES_LOG and DISKTILES_TRACE are read by every build: they only
// print. The rest act for the user, so they exist in debug builds and in
// `dotnet publish -p:TestHooks=true` (TEST_HOOKS), never in a release.
using System.Diagnostics;

namespace Disktiles;

static class TestHooks
{
    public static readonly bool Trace = Environment.GetEnvironmentVariable("DISKTILES_TRACE") is not null;
    public static readonly string? PickFolder = TestHooks.Env("DISKTILES_PICK");
    static readonly bool LogEnabled = Environment.GetEnvironmentVariable("DISKTILES_LOG") is not null;

    public static void Log(string what)
    {
        if (LogEnabled)
        {
            Console.Error.WriteLine($"log: {what}");
        }
    }
    /// A test switch's value; null in a release build, whatever is set.
    public static string? Env(string name) =>
#if TEST_HOOKS
        Environment.GetEnvironmentVariable(name);
#else
        null;
#endif

    const uint StepMs = 150;

    public static void Install(App app)
    {
#if TEST_HOOKS
        InstallFps(app);
        var started = false;
        app.State.TreeChanged += () =>
        {
            if (!started)
            {
                started = true;
                AfterScan(app);
            }
        };
#endif
    }

    static void AfterScan(App app)
    {
        var treemap = app.Treemap;
        if (Points("DISKTILES_HOVER") is [var x, var y])
        {
            // Retried until the window has settled at its size and the
            // pointer lands on a tile, for up to five seconds.
            var tries = 0;
            GLib.Functions.TimeoutAdd(GLib.Constants.PRIORITY_DEFAULT, 200, () =>
            {
                treemap.Hover(x, y);
                return treemap.Hovered is null && ++tries < 25;
            });
        }
        // A pointer that never stops: a Lissajous path over the treemap,
        // moved once per frame, with the hover and the panel following it.
        if (TestHooks.Env("DISKTILES_SWEEP") is { } sweep)
        {
            treemap.Widget.AddTickCallback((_, clock) =>
            {
                var t = clock.GetFrameTime() / 1e6;
                var (w, h) = (treemap.Width, treemap.Height);
                treemap.Hover((0.5 + 0.45 * Math.Sin(t * 1.3)) * w, (0.5 + 0.45 * Math.Sin(t * 1.9 + 1)) * h,
                    panel: sweep != "nopanel");
                return true;
            });
        }

        if (TestHooks.Env("DISKTILES_DRIVE") is { } drive)
        {
            Drive(app, drive);
        }

        var steps = new Queue<(string What, Action Act)>();
        void Step(string what, Action act) => steps.Enqueue((what, act));
        foreach (var click in TestHooks.Env("DISKTILES_CLICK")?.Split(';') ?? [])
        {
            if (Parse(click) is [var cx, var cy, .. var rest])
            {
                var b = rest is [var n, ..] ? (uint)n : 1;
                var presses = rest is [_, var p] ? (int)p : 1;
                steps.Enqueue(($"click {cx},{cy} button {b} x{presses}", () => treemap.Click(cx, cy, b, presses)));
            }
        }
        foreach (var mark in TestHooks.Env("DISKTILES_CTRLCLICK")?.Split(';') ?? [])
        {
            if (Parse(mark) is [var mx, var my])
            {
                steps.Enqueue(($"ctrl-click {mx},{my}", () => Marking.Click(app, mx, my)));
            }
        }
        // A pointer that moved there: the keys then act on the tile under it.
        if (Points("DISKTILES_POINT") is [var px, var py])
        {
            Step($"point {px},{py}", () =>
            {
                treemap.Hover(px, py);
                app.State.PointerActive = true;
            });
        }
        foreach (var spec in TestHooks.Env("DISKTILES_SCROLL")?.Split(';') ?? [])
        {
            if (spec.Split(',') is not [var sx, var sy, var sdy, .. var flag]
                || !double.TryParse(sx, out var x2) || !double.TryParse(sy, out var y2) || !double.TryParse(sdy, out var dy))
            {
                continue;
            }
            var modifiers = flag.Contains("shift") ? Gdk.ModifierType.ShiftMask : Gdk.ModifierType.NoModifierMask;
            var times = flag.Select(f => int.TryParse(f, out var n) ? n : 0).DefaultIfEmpty(0).Max();
            for (var i = 0; i < Math.Max(times, 1); i++)
            {
                Step($"scroll {x2},{y2} dy {dy} {modifiers}", () =>
                {
                    treemap.Scroll(x2, y2, 0, dy, modifiers);
                    var view = treemap.View;
                    Console.Error.WriteLine($"test: view {view.Scale:0.00}x at {view.OriginX:0.0},{view.OriginY:0.0}");
                });
            }
        }
        if (TestHooks.Env("DISKTILES_ZOOMSWEEP") is { } zoom)
        {
            // Every fourth frame a notch, 1.5 s in, then 1.5 s out.
            var frame = 0;
            treemap.Widget.AddTickCallback((_, clock) =>
            {
                if (++frame % 4 != 0 || treemap.Tiles.Count == 0)
                {
                    return true;
                }
                var t = clock.GetFrameTime() / 1e6;
                var (x, y) = ((0.5 + 0.2 * Math.Sin(t * 0.7)) * treemap.Width, (0.5 + 0.2 * Math.Sin(t * 0.9)) * treemap.Height);
                var dy = t % 3 < 1.5 ? -1 : 1;
                if (zoom == "magnify")
                {
                    Zoom.ZoomAt(app, x, y, dy < 0 ? 1.15 : 1 / 1.15, descend: false);
                }
                else
                {
                    treemap.Scroll(x, y, 0, dy, Gdk.ModifierType.NoModifierMask);
                }
                return true;
            });
        }
        if (TestHooks.Env("DISKTILES_CRUMB")?.Split(',') is [var crumb, .. var menu] && int.TryParse(crumb, out var at))
        {
            steps.Enqueue(($"crumb {at}{(menu is ["menu"] ? " menu" : "")}", () =>
                Console.Error.WriteLine($"test: crumb -> {Trail.Press(at, menu is ["menu"])}")));
        }
        foreach (var chord in TestHooks.Env("DISKTILES_KEYS")?.Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? [])
        {
            Step($"key {chord}", () =>
            {
                var handled = Keymap.Parse(chord) is var (keyval, modifiers) && app.Keys.Press(keyval, modifiers);
                Console.Error.WriteLine($"test: key {chord} -> {(handled ? "handled" : "unhandled")}, {Where(app)}, view {treemap.View.Scale:0.00}x, depth {app.State.Layout.MaxDepth}");
            });
        }
        if (TestHooks.Env("DISKTILES_SNAP")?.Split(',') is [var snap, .. var after])
        {
            var wait = after is [var w] && double.TryParse(w, out var seconds) ? seconds : 2;
            var delay = (uint)(steps.Count * StepMs + wait * 1000);
            GLib.Functions.TimeoutAdd(GLib.Constants.PRIORITY_DEFAULT, delay, () =>
            {
                Console.Error.WriteLine($"test: snap {snap}: {Snap(app.Window, snap).What}");
                return false;
            });
        }
        if (steps.Count == 0)
        {
            return;
        }
        // Once the treemap has tiles to aim at and its size held still for a
        // tick (the window settles after the first scan), one step per tick.
        var settled = (Width: -1.0, Height: -1.0);
        var playing = false;
        GLib.Functions.TimeoutAdd(GLib.Constants.PRIORITY_DEFAULT, StepMs, () =>
        {
            if (!playing)
            {
                var size = (Width: (double)treemap.Width, Height: (double)treemap.Height);
                playing = treemap.Tiles.Count > 0 && size == settled;
                settled = size;
                if (!playing)
                {
                    return true;
                }
            }
            var (what, act) = steps.Dequeue();
            act();
            if (!what.StartsWith("key "))
            {
                Console.Error.WriteLine($"test: {what}, {Where(app)}");
            }
            return steps.Count > 0;
        });
    }

    // The window as GTK would draw it, through its own renderer: the
    // compositor (and a lock screen over it) never takes part.
    /// DISKTILES_DRIVE: commands from a pipe, one a line, played on the main
    /// loop as they arrive: `hover X Y` (window logical pixels, where a script
    /// also warps the real cursor, which sends the app no motion of its own)
    /// and `key CHORD` (as DISKTILES_KEYS).
    static void Drive(App app, string fifo)
    {
        var reader = new Thread(Read) { IsBackground = true, Name = "disktiles-drive" };
        reader.Start();

        void Read()
        {
            foreach (var line in File.ReadLines(fifo))
            {
                GLib.Functions.IdleAdd(GLib.Constants.PRIORITY_DEFAULT, () =>
                {
                    switch (line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    {
                        case ["hover", var sx, var sy] when double.TryParse(sx, out var x) && double.TryParse(sy, out var y):
                            var point = new Graphene.Point { X = (float)x, Y = (float)y };
                            if (app.Window.ComputePoint(app.Treemap.Widget, point, out var local))
                            {
                                app.Treemap.Hover(local.X, local.Y);
                                app.State.PointerActive = true;
                            }
                            break;
                        case ["key", var chord]:
                            if (Keymap.Parse(chord) is var (keyval, modifiers))
                            {
                                app.Keys.Press(keyval, modifiers);
                            }
                            break;
                    }
                    return false;
                });
            }
        }
    }

    static (bool Saved, string What) Snap(Gtk.Window window, string path)
    {
        var (w, h) = (window.GetWidth(), window.GetHeight());
        var scale = window.GetScaleFactor();
        var snapshot = Gtk.Snapshot.New();
        snapshot.Scale(scale, scale);
        Gtk.WidgetPaintable.New(window).Snapshot(snapshot, w, h);
        if (snapshot.ToNode() is not { } node || window.GetRenderer() is not { } renderer)
        {
            return (false, "nothing drawn");
        }
        var texture = renderer.RenderTexture(node, null);
        return texture.SaveToPng(path) ? (true, $"{texture.GetWidth()}x{texture.GetHeight()}") : (false, "could not save");
    }

    /// --snapshot, in every build: once the first scan is shown, activate
    /// again (the window comes forward, no second one opens), draw the window
    /// to `png` and quit. The release workflow runs it on each package.
    public static void SelfCheck(Gtk.Application application, App app, string png, Action<bool> done)
    {
        var started = false;
        app.State.TreeChanged += () =>
        {
            if (started)
            {
                return;
            }
            started = true;
            // A second for the window to lay the tiles out and draw them.
            GLib.Functions.TimeoutAdd(GLib.Constants.PRIORITY_DEFAULT, 1000, () =>
            {
                application.Activate();
                var oneWindow = application.GetActiveWindow() == app.Window;
                var (saved, what) = Snap(app.Window, png);
                Console.Error.WriteLine($"snapshot {png}: {what}{(oneWindow ? "" : "; activating again opened a second window")}");
                done(saved && oneWindow);
                application.Quit();
                return false;
            });
        };
    }

    static double[]? Points(string variable) => Parse(Environment.GetEnvironmentVariable(variable));

    static double[]? Parse(string? text)
    {
        var parts = text?.Split(',');
        if (parts is null)
        {
            return null;
        }
        var values = new double[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!double.TryParse(parts[i], out values[i]))
            {
                return null;
            }
        }
        return values;
    }

    static string Where(App app) =>
        $"folder [{string.Join(',', app.State.Crumbs)}], selected [{string.Join(',', app.State.Selected ?? [])}]{(app.State.Selected is null ? " (none)" : "")}";

    // Frame stats: the treemap's own draws, and every frame GTK presents with
    // the CPU time it spent between before-paint and after-paint, split in
    // two: layout (measure and allocate widgets) and paint (snapshot and render).
    static void InstallFps(App app)
    {
        var log = TestHooks.Env("DISKTILES_FPS") is not null;
        var draws = new Queue<(long At, double Ms)>();
        var frames = new Queue<(long At, double Ms)>();
        var layouts = new Queue<(long At, double Ms)>();
        var paints = new Queue<(long At, double Ms)>();

        var hud = Gtk.Label.New("");
        hud.AddCssClass("hud");
        hud.Halign = Gtk.Align.End;
        hud.Valign = Gtk.Align.Start;
        hud.CanTarget = false;
        hud.Visible = log;
        app.Treemap.AddOverlay(hud);
        app.Keys.Bind("f", "Debug", "frame-rate overlay", () => { hud.Visible = !hud.Visible; }, on: null);
        app.Treemap.Rendered += ms => draws.Enqueue((Stopwatch.GetTimestamp(), ms));

        if (app.Window.GetFrameClock() is { } clock)
        {
            // GTK's own handlers run first, so ours mark when its layout and
            // its paint have finished.
            long paintStarted = 0, laidOutAt = 0, paintedAt = 0;
            clock.OnBeforePaint += (_, _) => paintStarted = laidOutAt = paintedAt = Stopwatch.GetTimestamp();
            clock.OnLayout += (_, _) => laidOutAt = Stopwatch.GetTimestamp();
            clock.OnPaint += (_, _) => paintedAt = Stopwatch.GetTimestamp();
            clock.OnAfterPaint += (_, _) =>
            {
                if (paintStarted != 0)
                {
                    var end = Stopwatch.GetTimestamp();
                    frames.Enqueue((end, Stopwatch.GetElapsedTime(paintStarted).TotalMilliseconds));
                    layouts.Enqueue((end, Stopwatch.GetElapsedTime(paintStarted, laidOutAt).TotalMilliseconds));
                    paints.Enqueue((end, Stopwatch.GetElapsedTime(laidOutAt, paintedAt).TotalMilliseconds));
                }
            };
        }
        GLib.Functions.TimeoutAdd(GLib.Constants.PRIORITY_DEFAULT, 250, () =>
        {
            if (hud.Visible)
            {
                var (count, average, max) = Stats(frames);
                hud.SetText($"{count} fps · frame {average:0.0} ms · max {max:0.0} ms");
            }
            return true;
        });
        if (log)
        {
            GLib.Functions.TimeoutAdd(GLib.Constants.PRIORITY_DEFAULT, 1000, () =>
            {
                var (count, average, max) = Stats(frames);
                var (redraws, drawAverage, _) = Stats(draws);
                var (_, layoutAverage, layoutMax) = Stats(layouts);
                var (_, paintAverage, paintMax) = Stats(paints);
                Console.Error.WriteLine($"fps: {count} frames/s, frame cpu avg {average:0.00} ms, max {max:0.00} ms | layout avg {layoutAverage:0.00} max {layoutMax:0.00} | paint avg {paintAverage:0.00} max {paintMax:0.00} | treemap redraws {redraws}/s ({drawAverage:0.00} ms)");
                return true;
            });
        }
    }

    // Entries in the last second, and their average and longest time.
    static (int Count, double Average, double Max) Stats(Queue<(long At, double Ms)> times)
    {
        var cutoff = Stopwatch.GetTimestamp() - Stopwatch.Frequency;
        while (times.Count > 0 && times.Peek().At < cutoff)
        {
            times.Dequeue();
        }
        return times.Count == 0 ? (0, 0, 0) : (times.Count, times.Average(d => d.Ms), times.Max(d => d.Ms));
    }
}
