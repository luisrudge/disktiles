// DISKTILES_PERF=<file>: timestamped JSON lines of what the app does, for
// measuring. The same file is in the GTK original's scratch clone, so both
// apps log the same events the same way. `t` is ms since the process started
// (from /proc/self/stat and /proc/uptime: about 10 ms of error in the anchor,
// none between events). Off (one field check) when the variable is unset.
using System.Diagnostics;
using System.Globalization;
using System.Text;

static class Perf
{
    static readonly StreamWriter? s_out = Open();
    static readonly long s_t0 = Stopwatch.GetTimestamp();
    static readonly double s_ageAtInit = ProcessAgeMs();
    static readonly Lock s_lock = new();
    static readonly List<(double At, double Work)> s_frames = [];
    static double s_driveFrom = double.NaN, s_cpuFrom;
    static string s_drive = "";

    public static bool On => s_out is not null;

    /// Also set DISKTILES_PERF_DETAIL=1: extra probes that cost time themselves (the port's encode).
    public static readonly bool Detail = Environment.GetEnvironmentVariable("DISKTILES_PERF_DETAIL") is "1";

    static StreamWriter? Open()
    {
        if (Environment.GetEnvironmentVariable("DISKTILES_PERF") is not { Length: > 0 } path)
        {
            return null;
        }
        var writer = new StreamWriter(path, append: true) { AutoFlush = true };
        // CLOCK_MONOTONIC (Stopwatch's clock, and Qt's %{time boot}) at t, to line other logs up.
        writer.WriteLine($"{{\"t\":0,\"ev\":\"anchor\",\"mono_ms\":{(Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency).ToString("0.000", CultureInfo.InvariantCulture)},\"age_ms\":{ProcessAgeMs().ToString("0.00", CultureInfo.InvariantCulture)}}}");
        return writer;
    }

    /// ms since the process started.
    public static double Now => s_ageAtInit + Stopwatch.GetElapsedTime(s_t0).TotalMilliseconds;

    static double ProcessAgeMs()
    {
        try
        {
            var stat = File.ReadAllText("/proc/self/stat");
            var fields = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');
            var startTicks = double.Parse(fields[19], CultureInfo.InvariantCulture); // field 22, starttime
            var uptime = double.Parse(File.ReadAllText("/proc/uptime").Split(' ')[0], CultureInfo.InvariantCulture);
            return uptime * 1000 - startTicks * 10;
        }
        catch (Exception error) when (error is IOException or FormatException)
        {
            return 0;
        }
    }

    /// CPU time this process used so far (user + system), ms.
    public static double CpuMs()
    {
        var stat = File.ReadAllText("/proc/self/stat");
        var fields = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');
        return (double.Parse(fields[11], CultureInfo.InvariantCulture) + double.Parse(fields[12], CultureInfo.InvariantCulture)) * 10;
    }

    public static void Mark(string ev, string extra = "")
    {
        if (s_out is null)
        {
            return;
        }
        var line = $"{{\"t\":{Now.ToString("0.00", CultureInfo.InvariantCulture)},\"ev\":\"{ev}\"{(extra.Length > 0 ? "," + extra : "")}}}";
        lock (s_lock)
        {
            s_out.WriteLine(line);
        }
    }

    /// A JSON field for Mark: "name":value.
    public static string F(string name, double value) => $"\"{name}\":{value.ToString("0.###", CultureInfo.InvariantCulture)}";
    public static string F(string name, string value) => $"\"{name}\":\"{value.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";

    /// ms since `from` (a Stopwatch timestamp).
    public static double Ms(long from) => Stopwatch.GetElapsedTime(from).TotalMilliseconds;

    /// A frame was presented; `work` is the toolkit's own time on it, if known (else -1).
    public static void Frame(double work = -1)
    {
        if (s_out is null)
        {
            return;
        }
        var now = Now;
        lock (s_lock)
        {
            if (!double.IsNaN(s_driveFrom))
            {
                s_frames.Add((now, work));
            }
        }
    }

    /// A workload driver starts: frames are kept from here.
    public static void DriveStart(string name)
    {
        lock (s_lock)
        {
            (s_drive, s_driveFrom, s_cpuFrom) = (name, Now, CpuMs());
            s_threadsFrom = Threads();
            s_frames.Clear();
        }
        Mark("drive_start", F("drive", name));
    }

    /// The driver is done: the summary, to the perf file and stderr.
    public static string DriveEnd()
    {
        double wall, cpu;
        List<(double At, double Work)> frames;
        lock (s_lock)
        {
            (wall, cpu) = (Now - s_driveFrom, CpuMs() - s_cpuFrom);
            frames = [.. s_frames];
            s_driveFrom = double.NaN;
        }
        var intervals = new List<double>();
        for (var i = 1; i < frames.Count; i++)
        {
            intervals.Add(frames[i].At - frames[i - 1].At);
        }
        intervals.Sort();
        double P(double q) => intervals.Count == 0 ? 0 : intervals[Math.Min(intervals.Count - 1, (int)Math.Floor(q * intervals.Count))];
        var works = frames.Where(f => f.Work >= 0).Select(f => f.Work).Order().ToList();
        double W(double q) => works.Count == 0 ? -1 : works[Math.Min(works.Count - 1, (int)Math.Floor(q * works.Count))];
        var summary = new StringBuilder();
        summary.Append(F("drive", s_drive)).Append(',').Append(F("wall_ms", wall)).Append(',').Append(F("cpu_ms", cpu))
            .Append(',').Append(F("frames", frames.Count)).Append(',').Append(F("interval_median", P(0.5)))
            .Append(',').Append(F("interval_p95", P(0.95))).Append(',').Append(F("interval_max", intervals.Count == 0 ? 0 : intervals[^1]))
            .Append(',').Append(F("over_16_7", intervals.Count(i => i > 16.7)))
            .Append(',').Append(F("work_median", W(0.5))).Append(',').Append(F("work_p95", W(0.95)))
            .Append(',').Append(F("work_max", works.Count == 0 ? -1 : works[^1]));
        Mark("drive_end", summary.ToString());
        var threads = Threads();
        Mark("drive_threads", ThreadsJson(threads.ToDictionary(kv => kv.Key, kv => kv.Value - s_threadsFrom.GetValueOrDefault(kv.Key))));
        // Every frame, for analysis: when (ms since the process started) and the toolkit's own work on it.
        Mark("frames", "\"at\":[" + string.Join(",", frames.Select(f => f.At.ToString("0.00", CultureInfo.InvariantCulture)))
            + "],\"work\":[" + string.Join(",", frames.Select(f => f.Work.ToString("0.###", CultureInfo.InvariantCulture))) + "]");
        Flush();
        Console.Error.WriteLine($"perf summary: {{{summary}}}");
        return summary.ToString();
    }

    /// CPU ms per thread name (threads of one name summed), from /proc/self/task.
    public static Dictionary<string, double> Threads()
    {
        var byName = new Dictionary<string, double>();
        foreach (var dir in Directory.EnumerateDirectories("/proc/self/task"))
        {
            try
            {
                var stat = File.ReadAllText(Path.Join(dir, "stat"));
                var name = stat[(stat.IndexOf('(') + 1)..stat.LastIndexOf(')')];
                // Numbered pool threads count as one kind.
                name = name.TrimEnd("0123456789#: ".ToCharArray());
                var fields = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');
                var ms = (double.Parse(fields[11], CultureInfo.InvariantCulture) + double.Parse(fields[12], CultureInfo.InvariantCulture)) * 10;
                byName[name] = byName.GetValueOrDefault(name) + ms;
            }
            catch (Exception error) when (error is IOException or FormatException)
            {
            }
        }
        return byName;
    }

    static string ThreadsJson(Dictionary<string, double> threads) =>
        "\"threads\":{" + string.Join(",", threads.Where(kv => kv.Value > 0).OrderByDescending(kv => kv.Value)
            .Select(kv => $"\"{kv.Key.Replace("\"", "")}\":{kv.Value.ToString("0", CultureInfo.InvariantCulture)}")) + "}";

    /// Per-thread CPU so far, as a mark.
    public static void MarkThreads(string ev) => Mark(ev, ThreadsJson(Threads()));

    static Dictionary<string, double> s_threadsFrom = [];

    public static void Flush()
    {
        lock (s_lock)
        {
            s_out?.Flush();
        }
    }
}
