// The Omarchy theme, read from
// `~/.local/state/omarchy/current/theme/colors.toml` and
// `current/theme.name`, falling back to `~/.config/omarchy/current` on older
// installs, and to Tokyo Night when there is no theme or it does not parse.
// Plain files, so nothing here is OS-specific: off Omarchy there are none.
using Disktiles.Core;

namespace Disktiles;

/// A colour, 0..1 per channel.
readonly record struct Rgba(double R, double G, double B, double A = 1)
{
    public static Rgba Hex(int rgb) => new(((rgb >> 16) & 0xff) / 255.0, ((rgb >> 8) & 0xff) / 255.0, (rgb & 0xff) / 255.0);

    /// Hue, saturation and lightness, 0..1 each.
    public static Rgba Hsl(double h, double s, double l, double a = 1)
    {
        if (s <= 0)
        {
            return new(l, l, l, a);
        }
        var q = l < 0.5 ? l * (1 + s) : l + s - l * s;
        var p = 2 * l - q;
        return new(Channel(h + 1 / 3.0), Channel(h), Channel(h - 1 / 3.0), a);

        double Channel(double t)
        {
            t = t < 0 ? t + 1 : t > 1 ? t - 1 : t;
            return t < 1 / 6.0 ? p + (q - p) * 6 * t
                : t < 0.5 ? q
                : t < 2 / 3.0 ? p + (q - p) * (2 / 3.0 - t) * 6
                : p;
        }
    }

    public double Hue
    {
        get
        {
            var max = Math.Max(R, Math.Max(G, B));
            var min = Math.Min(R, Math.Min(G, B));
            var d = max - min;
            if (d == 0)
            {
                return 0;
            }
            var h = max == R ? (G - B) / d + (G < B ? 6 : 0) : max == G ? (B - R) / d + 2 : (R - G) / d + 4;
            return h / 6;
        }
    }

    public Rgba Opacity(double a) => this with { A = A * a };

    /// In RGB: interpolating hue would drag a colour around the wheel on its
    /// way to a grey.
    public Rgba Mix(Rgba to, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return new(R + (to.R - R) * t, G + (to.G - G) * t, B + (to.B - B) * t, A + (to.A - A) * t);
    }

    public double Luminance
    {
        get
        {
            static double Linear(double v) => v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
            return 0.2126 * Linear(R) + 0.7152 * Linear(G) + 0.0722 * Linear(B);
        }
    }

    public double Contrast(Rgba other)
    {
        var (a, b) = (Luminance, other.Luminance);
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    public string Css => FormattableString.Invariant($"rgba({Math.Round(R * 255)},{Math.Round(G * 255)},{Math.Round(B * 255)},{A:0.###})");
}

/// The semantic roles every surface is drawn from.
sealed record OmarchyTheme(
    string Name, bool Dark,
    Rgba Background, Rgba Surface, Rgba Inset,
    Rgba Foreground, Rgba Secondary, Rgba Bright,
    Rgba Accent, Rgba OnAccent, Rgba Selection, Rgba Border,
    Rgba Danger, Rgba Warning, Rgba Success)
{
    public Rgba Divider => Foreground.Opacity(0.12);
    public Rgba HoverFill => Foreground.Opacity(0.08);
    public Rgba SelectedFill => Foreground.Opacity(0.18);
    public Rgba ControlBorder => Foreground.Opacity(0.4);

    public static readonly OmarchyTheme TokyoNight = new("Tokyo Night", true,
        Rgba.Hex(0x1a1b26), Rgba.Hex(0x24283b), Rgba.Hex(0x13141c),
        Rgba.Hex(0xa9b1d6), Rgba.Hex(0xa9b1d6), Rgba.Hex(0xc0caf5),
        Rgba.Hex(0x7aa2f7), Rgba.Hex(0x13141c), Rgba.Hex(0x292e42), Rgba.Hex(0x414868),
        Rgba.Hex(0xf7768e), Rgba.Hex(0xe0af68), Rgba.Hex(0x9ece6a));

    public static readonly OmarchyTheme FlexokiLight = new("Flexoki Light", false,
        Rgba.Hex(0xfffcf0), Rgba.Hex(0xf2f0e5), Rgba.Hex(0xe6e4d9),
        Rgba.Hex(0x100f0f), Rgba.Hex(0x575653), Rgba.Hex(0x100f0f),
        Rgba.Hex(0x205ea6), Rgba.Hex(0xfffcf0), Rgba.Hex(0xdad8ce), Rgba.Hex(0xb7b5ac),
        Rgba.Hex(0xaf3029), Rgba.Hex(0x855b00), Rgba.Hex(0x526600));

    /// The `current` directory the theme is read from: the state directory if
    /// it exists at all (a broken one must not revive a stale legacy theme),
    /// else the legacy one; null when neither exists.
    public static string? CurrentDir(string home)
    {
        var state = Path.Combine(home, ".local/state/omarchy/current");
        var legacy = Path.Combine(home, ".config/omarchy/current");
        return Path.Exists(state) || Space.LinkTarget(state) is not null ? state
            : Path.Exists(legacy) ? legacy
            : null;
    }

    /// What the app draws with: the Omarchy theme where there is one;
    /// elsewhere the system's light or dark or, on a desktop that states no
    /// preference, Tokyo Night.
    public static OmarchyTheme Current(string? home, Func<bool> systemIsDark)
    {
        if (!string.IsNullOrEmpty(home) && CurrentDir(home) is { } dir)
        {
            return FromCurrentDir(dir) ?? TokyoNight;
        }
        return FollowsSystem() ? (systemIsDark() ? TokyoNight : FlexokiLight) : TokyoNight;
    }

    // macOS and Windows always state a preference; on Linux only the desktops
    // whose settings do (a bare window manager reads as light through the portal).
    static bool FollowsSystem() =>
        OperatingSystem.IsMacOS() || OperatingSystem.IsWindows()
        || (Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? "").Split(':')
            .Any(d => d.ToLowerInvariant() is "gnome" or "kde" or "ubuntu" or "unity" or "pantheon" or "cinnamon");

    public static OmarchyTheme? FromCurrentDir(string dir)
    {
        try
        {
            var name = File.Exists(Path.Combine(dir, "theme.name")) ? File.ReadAllText(Path.Combine(dir, "theme.name")).Trim() : "";
            return FromColorsToml(name.Length > 0 ? name : "Omarchy", File.ReadAllText(Path.Combine(dir, "theme/colors.toml")));
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// Semantic Omarchy palettes and ANSI `color0..15` ones; null when a
    /// required role is missing or a value is not `#RRGGBB` (no partial themes).
    public static OmarchyTheme? FromColorsToml(string name, string toml)
    {
        var values = ParseToml(toml);
        if (values is null)
        {
            return null;
        }
        var bad = false;
        Rgba? Color(params string[] keys)
        {
            foreach (var key in keys)
            {
                if (!values.TryGetValue(key, out var text))
                {
                    continue;
                }
                if (text is ['#', .. var hex] && hex.Length == 6 && int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var rgb))
                {
                    return Rgba.Hex(rgb);
                }
                bad = true;
                return null;
            }
            return null;
        }
        if (Color("background") is not { } background || Color("foreground") is not { } foreground || Color("accent") is not { } accent
            || Color("red", "color1") is not { } danger || Color("yellow", "color3") is not { } warning || Color("green", "color2") is not { } success)
        {
            return null;
        }
        bool dark;
        if (values.TryGetValue("mode", out var mode))
        {
            if (mode is not ("dark" or "light"))
            {
                return null;
            }
            dark = mode == "dark";
        }
        else
        {
            dark = background.Luminance <= foreground.Luminance;
        }
        // Neutral layers derive from the user's own colours, never another theme's.
        var surface = Color("lighter_background") ?? background.Mix(foreground, 0.05);
        var inset = Color("dark_background") ?? background.Mix(foreground, 0.08);
        var bright = Color("bright_foreground", "selection_foreground", "cursor") ?? foreground;
        var selection = Color("selection", "selection_background") ?? background.Mix(accent, 0.2);
        var border = Color("muted", "color8") ?? background.Mix(foreground, 0.25);
        Rgba black = new(0, 0, 0), white = new(1, 1, 1);
        var onAccent = accent.Contrast(black) > accent.Contrast(white) ? black : white;
        return bad ? null : new(name, dark, background, surface, inset, foreground, background.Mix(foreground, 0.75), bright,
            accent, onAccent, selection, border, danger, warning, success);
    }

    // colors.toml is flat `key = "value"` lines. Non-string values are kept
    // as null so a colour role holding one rejects the theme. Null when a
    // line is not TOML at all.
    // ponytail: flat key/value subset of TOML, tables and arrays ignored; a real parser if themes start nesting.
    static Dictionary<string, string?>? ParseToml(string toml)
    {
        var values = new Dictionary<string, string?>();
        foreach (var raw in toml.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] is '#' or '[')
            {
                continue;
            }
            var eq = line.IndexOf('=');
            if (eq <= 0)
            {
                return null;
            }
            var key = line[..eq].Trim().Trim('"', '\'');
            var value = line[(eq + 1)..].Trim();
            if (value is [var quote and ('"' or '\''), .. var rest] && rest.IndexOf(quote) is var end and >= 0)
            {
                values[key] = rest[..end];
            }
            else
            {
                values[key] = null;
            }
        }
        return values;
    }
}
