// The current Omarchy theme for C# code (window apps and panels): every key in
// ~/.local/state/omarchy/current/theme/colors.toml, camelCased like QML's `theme.*`
// (dark_background -> "darkBackground"), plus Dark and Name (current/theme.name).
// Changed fires on the UI thread when the user switches themes. Use it for colors you compute in C# (a Drawing,
// a palette), not for QML: QML has `theme.*` (windows) and `Commons.Color.*` (panels).
// bridge/host/theme.cpp reads the same file for window QML: keep defaults and parsing in step.
namespace Omasharp;

public static class Theme
{
    static Dictionary<string, string> s_colors = Defaults();
    static string s_name = "";
    static FileSystemWatcher? s_watcher;
    static Timer? s_debounce;
    static Action? s_changed;
    static readonly object s_lock = new();
    static bool s_started;

    static string StateDir => Path.Combine(Environment.GetEnvironmentVariable("HOME") ?? "", ".local/state/omarchy/current");

    /// <summary>Every theme key (camelCased) to its value, e.g. "accent" -> "#7aa2f7".</summary>
    public static IReadOnlyDictionary<string, string> Colors { get { Start(); return s_colors; } }

    public static bool Dark => Get("mode") != "light";

    /// <summary>The theme's name as Omarchy stores it, e.g. "tokyo-night"; "" without an Omarchy theme.</summary>
    public static string Name { get { Start(); return s_name; } }

    public static string Get(string key, string fallback = "") => Colors.TryGetValue(key, out var v) ? v : fallback;

    /// <summary>A theme color for a Drawing, e.g. Theme.Color("accent").</summary>
    public static Color Color(string key, string fallback = "#ff00ff") => Omasharp.Color.Parse(Get(key, fallback));

    /// <summary>Fires on the UI thread after the user switches themes.</summary>
    public static event Action Changed
    {
        add { Start(); lock (s_lock) s_changed += value; }
        remove { lock (s_lock) s_changed -= value; }
    }

    static void Start()
    {
        lock (s_lock)
        {
            if (s_started) return;
            s_started = true;
            s_colors = Load();
            s_name = LoadName();
            if (!Directory.Exists(StateDir)) return;
            // Omarchy swaps current/theme for a new directory: watch below current/.
            s_watcher = new FileSystemWatcher(StateDir) { IncludeSubdirectories = true, EnableRaisingEvents = true };
            FileSystemEventHandler on = (_, _) => s_debounce?.Change(80, Timeout.Infinite);
            s_watcher.Changed += on;
            s_watcher.Created += on;
            s_watcher.Renamed += (_, _) => s_debounce?.Change(80, Timeout.Infinite);
            s_debounce = new Timer(_ =>
            {
                var next = Load();
                var name = LoadName();
                if (name == s_name && next.Count == s_colors.Count && next.All(kv => s_colors.TryGetValue(kv.Key, out var v) && v == kv.Value)) return;
                s_colors = next;
                s_name = name;
                Ui.Post(() => s_changed?.Invoke());
            });
        }
    }

    static Dictionary<string, string> Defaults() => new()
    {
        ["mode"] = "dark",
        ["background"] = "#1a1b26",
        ["foreground"] = "#a9b1d6",
        ["accent"] = "#7aa2f7",
        ["selection"] = "#292e42",
        ["muted"] = "#414868",
        ["lighterBackground"] = "#24283b",
    };

    static Dictionary<string, string> Load()
    {
        var colors = Defaults();
        try
        {
            foreach (var raw in File.ReadLines(Path.Combine(StateDir, "theme/colors.toml")))
            {
                var line = raw.Trim();
                var eq = line.IndexOf('=');
                if (line.Length == 0 || line[0] == '#' || eq < 0) continue;
                var value = line[(eq + 1)..].Trim();
                if (value.Length >= 2 && value[0] is '"' or '\'')
                {
                    var end = value.IndexOf(value[0], 1);
                    value = end > 0 ? value[1..end] : value[1..];
                }
                colors[Camel(line[..eq].Trim())] = value;
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return colors;
    }

    static string LoadName()
    {
        try { return File.ReadAllText(Path.Combine(StateDir, "theme.name")).Trim(); }
        catch (IOException) { return ""; }
        catch (UnauthorizedAccessException) { return ""; }
    }

    static string Camel(string key)
    {
        var parts = key.Split('_', '-');
        return parts[0] + string.Concat(parts.Skip(1).Where(p => p.Length > 0).Select(p => char.ToUpperInvariant(p[0]) + p[1..]));
    }
}
