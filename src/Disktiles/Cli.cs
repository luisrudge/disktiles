// The command line and the Power Efficiency presets. Read once, before the
// window opens.
using System.Reflection;
using Disktiles.Core;

namespace Disktiles;

/// What the command line asked for. `Power` is null for a custom worker count;
/// `Snapshot` is the PNG --snapshot draws the window to.
sealed record Cli(string Root, ScanOptions Options, uint Depth, Power? Power, string? Snapshot = null)
{
    /// Set by Program before the window opens; Scanning starts from it.
    public static Cli Current { get; set; } = null!;

    /// The release's version (the tag, at publish), without the commit the
    /// SDK appends after a `+`.
    public static string Version { get; } =
        typeof(Cli).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? "unknown";

    public const string Usage = """
        disktiles — a treemap of what is using your disk

        usage: disktiles [OPTIONS] [PATH]

        arguments:
          PATH              directory to scan (default: the home directory)

        The window opens on a treemap of the root, largest first. Space marks the
        selected tile, Enter opens it, c reviews the marked list, ? lists every key.

        options:
          -a, --apparent-size   measure apparent length instead of allocated blocks
          -l, --follow-links    follow symlinks
          -H, --no-hidden       skip dotfiles and dot-directories
          -D, --disk            scan the whole disk the home directory is on
          -X, --cross-filesystems
                                also measure other disks, network shares and pseudo
                                filesystems mounted below PATH (off by default)
          -d, --depth N         how many levels to draw at once (1-6, default 3)
              --power-efficiency PRESET
                                miser, balanced (default), aggressive, drain-my-battery
              --scan-threads N  fixed scan workers, capped by available CPU count
              --metric files    rank by file count instead of bytes
              --snapshot FILE   draw the window to a PNG once the scan is shown,
                                then quit: a check that GTK loads and draws
          -V, --version         print the version
          -h, --help            show this help

        """;

    /// Throws ArgumentException with the message to print; `--help` is the
    /// caller's to handle (it exits before GTK starts).
    public static Cli Parse(string[] args, Power saved)
    {
        string? root = null;
        var options = new ScanOptions { Threads = saved.Threads() };
        Power? power = saved;
        uint depth = 3;
        var disk = false;
        string? snapshot = null;
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            string Next(string need) => ++i < args.Length ? args[i] : throw new ArgumentException(need);
            switch (arg)
            {
                case "-a" or "--apparent-size": options = options with { ApparentSize = true }; break;
                case "-l" or "--follow-links": options = options with { FollowLinks = true }; break;
                case "-H" or "--no-hidden": options = options with { IncludeHidden = false }; break;
                // Staying on one volume is the default; kept so old invocations still work.
                case "-x" or "--one-filesystem": options = options with { OneFilesystem = true }; break;
                case "-X" or "--cross-filesystems": options = options with { OneFilesystem = false }; break;
                case "-D" or "--disk": disk = true; break;
                case "-d" or "--depth":
                    depth = uint.TryParse(Next("--depth needs a number"), out var d) ? d : throw new ArgumentException("--depth needs a number");
                    if (depth is < 1 or > 6)
                    {
                        throw new ArgumentException("--depth must be 1 to 6");
                    }
                    break;
                case "--power-efficiency":
                    power = PowerPresets.Parse(Next("--power-efficiency needs a preset")) ?? throw new ArgumentException("unknown Power Efficiency preset");
                    options = options with { Threads = power.Value.Threads() };
                    break;
                case "--scan-threads":
                    power = null;
                    options = options with
                    {
                        Threads = !int.TryParse(Next("--scan-threads needs a number"), out var t)
                            ? throw new ArgumentException("--scan-threads needs a positive integer")
                            : t > 0 ? Math.Min(t, Environment.ProcessorCount)
                            : throw new ArgumentException("--scan-threads must be positive"),
                    };
                    break;
                case "--snapshot": snapshot = Next("--snapshot needs a file"); break;
                case "--metric":
                    options = options with
                    {
                        Metric = Next("--metric needs a value") switch
                        {
                            "files" => Metric.Files,
                            "bytes" or "size" => Metric.Bytes,
                            var other => throw new ArgumentException($"unknown metric {other}; try bytes or files"),
                        },
                    };
                    break;
                // Some launchers still add a process serial number on macOS.
                case var other when other.StartsWith("-psn_", StringComparison.Ordinal): break;
                case var other when other.StartsWith('-'):
                    throw new ArgumentException($"unknown option {other}\n\n{Usage}");
                default:
                    root = root is null ? arg : throw new ArgumentException("only one path can be scanned");
                    break;
            }
        }
        if (disk && root is not null)
        {
            throw new ArgumentException("--disk and a PATH cannot be combined");
        }
        var home = Removal.HomeDir();
        root = disk
            ? (home is null ? null : Space.VolumeRootFor(home)) ?? "/"
            : root ?? home ?? throw new ArgumentException("no path given and no home directory");
        // Canonical: absolute, with symlinks resolved.
        root = Space.Absolute(root);
        root = Space.RealPath(root) ?? root;
        if (!Directory.Exists(Space.Literal(root)))
        {
            throw new ArgumentException(File.Exists(Space.Literal(root)) ? $"{root} is not a directory" : $"cannot read {root}");
        }
        return new Cli(root, options, depth, power, snapshot);
    }
}

/// The Power Efficiency presets: fixed worker counts, saved between runs.
enum Power { Miser, Balanced, Aggressive, DrainMyBattery }

static class PowerPresets
{
    public static string Key(this Power power) => power switch
    {
        Power.Miser => "miser",
        Power.Balanced => "balanced",
        Power.Aggressive => "aggressive",
        _ => "drain-my-battery",
    };

    public static Power? Parse(string value) =>
        Enum.GetValues<Power>().Select(p => (Power?)p).FirstOrDefault(p => p!.Value.Key() == value);

    public static int Threads(this Power power)
    {
        var cpus = Math.Max(1, Environment.ProcessorCount);
        return power switch
        {
            Power.Miser => Math.Min(2, cpus),
            Power.Balanced => Math.Min(4, cpus),
            Power.Aggressive => Math.Min(8, cpus),
            _ => cpus,
        };
    }

    /// Where the preset is saved: disktiles/power-efficiency in each desktop's
    /// own config location, the XDG config dir on Linux.
    public static string? SettingsPath()
    {
        var home = Environment.GetEnvironmentVariable("HOME");
        string? config = OperatingSystem.IsMacOS() ? home is null ? null : Path.Join(home, "Library/Application Support")
            : OperatingSystem.IsWindows() ? Environment.GetEnvironmentVariable("APPDATA")
            : Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { } xdg && Path.IsPathRooted(xdg) ? xdg
            : home is null ? null : Path.Join(home, ".config");
        return config is null ? null : Path.Join(config, "disktiles/power-efficiency");
    }

    /// The saved preset; Balanced when there is none. A bad file is reported
    /// and ignored.
    public static Power Load()
    {
        try
        {
            if (SettingsPath() is { } path && File.Exists(path))
            {
                return Parse(File.ReadAllText(path).Trim()) ?? throw new InvalidDataException("unknown power preset");
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Console.Error.WriteLine($"Cannot load Power Efficiency: {error.Message}; using Balanced");
        }
        return Power.Balanced;
    }
}
