// Switches for testing without a mouse, from the command line (a headless
// compositor passes no environment to the app, so the GTK app's DISKTILES_*
// variables became options):
//
//   --test-hover x,y        a pointer parked at one point
//   --test-point x,y        a pointer that moved there: keys act on the tile under it
//   --test-click x,y[,b[,n]]  a press, button b (1 default, 2 middle, 3 right, 8/9
//                           back/forward), n its press count; ";" between several
//   --test-ctrlclick x,y    a ctrl-click: marks the tile there; ";" between several
//   --test-scroll x,y,dy[,shift][,N]  a scroll step of dy notches, N times; ";" for more
//   --test-crumb n[,menu]   press the n-th crumb of the trail (from 0), or its ▾
//   --test-keys "k k …"     key presses through the keymap: "Down Down Return ctrl+o"
//   --test-pick /path       ctrl+o takes this folder instead of opening a chooser
//   --test-reveal dry       log what `o` would show instead of opening the file manager
//   --test-save-to file     the review's `s` writes here instead of asking where
//   --test-clipboard-to file  the review's `a` writes here, never to the clipboard
//   --test-trash-dir dir    the trash is an XDG trash in dir, never the user's
//   --test-log 1            what scans, modes, marks and screens do, to stderr
//
// Lists take ";" or ":" between items (":" survives the unquoted shell
// line `dotnet build -t:Shot` runs the app with). Coordinates are logical
// pixels in the treemap. After the first scan, once
// the treemap has tiles, the clicks, ctrl-clicks, point, scrolls, crumb and
// keys play 150 ms apart, each logged to stderr. Only debug builds (and
// `dotnet publish -p:TestHooks=true`) act on them; --snapshot works in every build.
namespace Disktiles;

sealed partial class App
{
    const int StepMs = 150;

    /// A test switch's value; null in a release build, whatever was given.
    public string? Hook(string name) =>
#if TEST_HOOKS
        Cli.Hooks.GetValueOrDefault(name);
#else
        name == "log" ? Cli.Hooks.GetValueOrDefault(name) : null;
#endif

    /// `f` (debug builds): the frame-rate overlay.
    public bool FpsShown { get; private set; }

    void InitHooks()
    {
#if TEST_HOOKS
        Keys.Bind("f", "Debug", "frame-rate overlay", () =>
        {
            FpsShown = !FpsShown;
            Touch(Part.Overlay);
        }, on: null);
#endif
        var started = false;
        Landed += scanned =>
        {
            if (started)
            {
                return;
            }
            started = true;
            if (Cli.Snapshot is { } png)
            {
                SelfCheck(png, scanned);
            }
#if TEST_HOOKS
            AfterScan();
#endif
        };
    }

    /// --snapshot: once the first scan is shown, QML draws the window to
    /// `png` and quits (1 if that failed); a failed scan quits at once.
    void SelfCheck(string png, bool scanned)
    {
        if (!scanned)
        {
            Console.Error.WriteLine($"snapshot {png}: the scan failed");
            Emit(new Effect("quit", "1"));
            return;
        }
        // A second for the window to lay the tiles out and draw them.
        var waited = false;
        Ui.Every(1000, () =>
        {
            if (waited)
            {
                return false;
            }
            waited = true;
            Emit(new Effect("snapshot", png));
            return false;
        });
    }

    void AfterScan()
    {
        var steps = new Queue<(string What, Action Act)>();
        if (Points(Hook("hover")) is [var hx, var hy])
        {
            steps.Enqueue(($"hover {hx},{hy}", () => Hover(hx, hy)));
        }
        foreach (var click in Hook("click")?.Split(';', ':') ?? [])
        {
            if (Points(click) is [var cx, var cy, .. var rest])
            {
                var button = rest is [var b, ..] ? (int)b : 1;
                var presses = rest is [_, var p] ? (int)p : 1;
                steps.Enqueue(($"click {cx},{cy} button {button} x{presses}", () => Click(cx, cy, button, presses, control: false)));
            }
        }
        foreach (var mark in Hook("ctrlclick")?.Split(';', ':') ?? [])
        {
            if (Points(mark) is [var mx, var my])
            {
                steps.Enqueue(($"ctrl-click {mx},{my}", () => Click(mx, my, 1, 1, control: true)));
            }
        }
        if (Points(Hook("point")) is [var px, var py])
        {
            steps.Enqueue(($"point {px},{py}", () => PointerMoved(px, py)));
        }
        foreach (var spec in Hook("scroll")?.Split(';', ':') ?? [])
        {
            if (spec.Split(',') is not [var sx, var sy, var sdy, .. var flag]
                || !double.TryParse(sx, out var x) || !double.TryParse(sy, out var y) || !double.TryParse(sdy, out var dy))
            {
                continue;
            }
            var shift = flag.Contains("shift");
            var times = flag.Select(f => int.TryParse(f, out var n) ? n : 0).DefaultIfEmpty(0).Max();
            for (var i = 0; i < Math.Max(times, 1); i++)
            {
                steps.Enqueue(($"scroll {x},{y} dy {dy}{(shift ? " shift" : "")}", () =>
                {
                    Scroll(x, y, 0, dy, shift);
                    Console.Error.WriteLine($"test: view {View.Scale:0.00}x at {View.OriginX:0.0},{View.OriginY:0.0}");
                }));
            }
        }
        if (Hook("crumb")?.Split(',') is [var crumb, .. var menu] && int.TryParse(crumb, out var at))
        {
            steps.Enqueue(($"crumb {at}{(menu is ["menu"] ? " menu" : "")}", () =>
                Console.Error.WriteLine($"test: crumb -> {(menu is ["menu"] ? CrumbMenu(at) : Crumb(at))}")));
        }
        foreach (var chord in Hook("keys")?.Split([' ', ':'], StringSplitOptions.RemoveEmptyEntries) ?? [])
        {
            steps.Enqueue(($"key {chord}", () =>
            {
                var handled = Press(QtKeys.Parse(chord));
                Console.Error.WriteLine($"test: key {chord} -> {(handled ? "handled" : "unhandled")}, {Where()}, view {View.Scale:0.00}x, depth {State.Layout.MaxDepth}");
            }));
        }
        if (steps.Count == 0)
        {
            return;
        }
        // Once the treemap has tiles to aim at and its size held still for a
        // tick (the window settles after the first scan), one step per tick.
        var settled = (Width: -1.0, Height: -1.0);
        var playing = false;
        Ui.Every(StepMs, () =>
        {
            if (!playing)
            {
                var size = (Width, Height);
                playing = Tiles.Count > 0 && size == settled;
                settled = size;
                if (!playing)
                {
                    return true;
                }
            }
            var (what, act) = steps.Dequeue();
            act();
            if (!what.StartsWith("key ", StringComparison.Ordinal))
            {
                Console.Error.WriteLine($"test: {what}, {Where()}");
            }
            return steps.Count > 0;
        });
    }

    static double[]? Points(string? text)
    {
        var parts = text?.Split(',');
        if (parts is null)
        {
            return null;
        }
        var values = new double[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!double.TryParse(parts[i], System.Globalization.CultureInfo.InvariantCulture, out values[i]))
            {
                return null;
            }
        }
        return values;
    }

    string Where() =>
        $"folder [{string.Join(',', State.Crumbs)}], selected [{string.Join(',', State.Selected ?? [])}]{(State.Selected is null ? " (none)" : "")}, screen {State.Screen}";
}
