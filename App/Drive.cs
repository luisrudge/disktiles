// DISKTILES_WORKLOAD=<workload>: a fixed workload for measuring, the same as in
// the GTK original's scratch clone, then quit with a summary (Perf.DriveEnd).
// It starts once the first scan has landed and the treemap has held its size
// for a tick. DISKTILES_WORKLOAD_SECONDS sets its length (default 8).
//
//   SWEEP      the pointer moves every frame on a Lissajous path over the treemap (hover, card, panel)
//   ZOOMSWEEP  every 4th frame a wheel notch near the centre, 1.5 s in then 1.5 s out (zooms, goes in and up)
//   NAV        Return, BackSpace, ... every 400 ms: into the largest directory and out, with the transition
//   MODES      t every 400 ms: Size, Files, Age
//   DEPTH      ] ] ] [ [ [ every 400 ms: depth 3 to 6 and back
//   FILTER     / l i c e n s e Return Escape, one key every 200 ms
//   IDLE       nothing: startup only
namespace Disktiles;

sealed partial class App
{
    /// The per-frame drivers want QML's frame ticks (FrameAnimation).
    public bool DriveTicking { get; private set; }
    string _drive = "";
    long _driveStarted;
    int _driveFrames;
#if TEST_HOOKS

    void InitDrive()
    {
        if (Environment.GetEnvironmentVariable("DISKTILES_WORKLOAD") is not { Length: > 0 } drive)
        {
            return;
        }
        _drive = drive.ToUpperInvariant();
        var seconds = double.TryParse(Environment.GetEnvironmentVariable("DISKTILES_WORKLOAD_SECONDS"), System.Globalization.CultureInfo.InvariantCulture, out var s) ? s : 8;
        var started = false;
        Landed += _ =>
        {
            if (started)
            {
                return;
            }
            started = true;
            var settled = (Width: -1.0, Height: -1.0);
            Ui.Every(150, () =>
            {
                var size = (Width, Height);
                var ready = Tiles.Count > 0 && size == settled;
                settled = size;
                if (!ready)
                {
                    return true;
                }
                StartDrive(seconds);
                return false;
            });
        };
    }
#endif

    static readonly Dictionary<string, (int Ms, string[] Keys)> KeyDrives = new()
    {
        ["NAV"] = (400, ["Return", "BackSpace"]),
        ["MODES"] = (400, ["t"]),
        ["DEPTH"] = (400, ["bracketright", "bracketright", "bracketright", "bracketleft", "bracketleft", "bracketleft"]),
        ["FILTER"] = (200, ["slash", "l", "i", "c", "e", "n", "s", "e", "Return", "Escape"]),
    };

    void StartDrive(double seconds)
    {
        Perf.DriveStart(_drive);
        _driveStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        var stopAt = seconds * 1000;
        if (KeyDrives.TryGetValue(_drive, out var keys))
        {
            var step = 0;
            Ui.Every(keys.Ms, () =>
            {
                if (Perf.Ms(_driveStarted) >= stopAt)
                {
                    EndDrive();
                    return false;
                }
                var chord = keys.Keys[step++ % keys.Keys.Length];
                Perf.Mark("key", Perf.F("key", chord));
                Press(QtKeys.Parse(chord));
                return true;
            });
            return;
        }
        if (_drive is "SWEEP" or "ZOOMSWEEP")
        {
            DriveTicking = true;
            Touch(Part.Overlay);
        }
        // SWEEP and ZOOMSWEEP tick from QML's frames (DriveTick); IDLE just waits.
        Ui.Every(50, () =>
        {
            if (Perf.Ms(_driveStarted) < stopAt)
            {
                return true;
            }
            EndDrive();
            return false;
        });
    }

    /// One frame of SWEEP or ZOOMSWEEP, from QML's FrameAnimation.
    public void DriveTick()
    {
        if (!DriveTicking)
        {
            return;
        }
        var t = Perf.Ms(_driveStarted) / 1000;
        var (w, h) = (Width, Height);
        if (_drive == "SWEEP")
        {
            PointerMoved((0.5 + 0.45 * Math.Sin(t * 1.3)) * w, (0.5 + 0.45 * Math.Sin(t * 1.9 + 1)) * h);
            return;
        }
        if (++_driveFrames % 4 != 0 || Tiles.Count == 0)
        {
            return;
        }
        var (x, y) = ((0.5 + 0.2 * Math.Sin(t * 0.7)) * w, (0.5 + 0.2 * Math.Sin(t * 0.9)) * h);
        Scroll(x, y, 0, t % 3 < 1.5 ? -1 : 1, shift: false);
    }

    void EndDrive()
    {
        DriveTicking = false;
        Touch(Part.Overlay);
        Perf.DriveEnd();
        Emit(new Effect("quit"));
    }
}
