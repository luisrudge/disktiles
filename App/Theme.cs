// The Omarchy theme: the colours from the bridge's `Theme` (which reads
// `~/.local/state/omarchy/current/theme/colors.toml` and follows switches),
// the name from `current/theme.name`, and Tokyo Night when there is no theme
// or it does not parse. The roles every surface uses are derived here.

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

    /// `#rrggbb`, or `#aarrggbb` when not opaque: the spellings QML reads.
    public string Qml
    {
        get
        {
            static int Byte(double v) => (int)Math.Round(Math.Clamp(v, 0, 1) * 255);
            var rgb = $"{Byte(R):x2}{Byte(G):x2}{Byte(B):x2}";
            return A >= 1 ? $"#{rgb}" : $"#{Byte(A):x2}{rgb}";
        }
    }
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

    /// What the app draws with: the Omarchy theme (the bridge's `Theme`, which
    /// follows switches live) where there is one; elsewhere the system's light
    /// or dark (Qt's colour scheme, from QML) or, on a desktop that states no
    /// preference, Tokyo Night.
    public static OmarchyTheme Current(string? home, bool systemIsDark)
    {
        var current = string.IsNullOrEmpty(home) ? null : Path.Combine(home, ".local/state/omarchy/current");
        if (current is not null && File.Exists(Path.Combine(current, "theme/colors.toml")))
        {
            var name = ReadName(Path.Combine(current, "theme.name"));
            // The bridge camelCases the keys (dark_background -> darkBackground).
            return FromValues(name, key => Omasharp.Theme.Colors.TryGetValue(Camel(key), out var v) ? v : null) ?? TokyoNight;
        }
        return FollowsSystem() ? (systemIsDark ? TokyoNight : FlexokiLight) : TokyoNight;
    }

    // The bridge's Theme has the colours but not the theme's name.
    static string ReadName(string path)
    {
        try
        {
            var name = File.Exists(path) ? File.ReadAllText(path).Trim() : "";
            return name.Length > 0 ? name : "Omarchy";
        }
        catch (IOException)
        {
            return "Omarchy";
        }
        catch (UnauthorizedAccessException)
        {
            return "Omarchy";
        }
    }

    static string Camel(string key)
    {
        var parts = key.Split('_');
        return parts[0] + string.Concat(parts.Skip(1).Where(p => p.Length > 0).Select(p => char.ToUpperInvariant(p[0]) + p[1..]));
    }

    // Only the desktops whose settings state a preference (a bare window
    // manager reads as light).
    static bool FollowsSystem() =>
        (Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? "").Split(':')
            .Any(d => d.ToLowerInvariant() is "gnome" or "kde" or "ubuntu" or "unity" or "pantheon" or "cinnamon");

    /// Semantic Omarchy palettes and ANSI `color0..15` ones; null when a
    /// required role is missing or a value is not `#RRGGBB` (no partial themes).
    public static OmarchyTheme? FromColorsToml(string name, string toml) =>
        ParseToml(toml) is { } values ? FromValues(name, key => values.TryGetValue(key, out var v) ? v ?? "" : null) : null;

    /// The roles from a theme's values, looked up by their colors.toml names;
    /// `get` returns null for a missing key.
    static OmarchyTheme? FromValues(string name, Func<string, string?> get)
    {
        var bad = false;
        Rgba? Color(params string[] keys)
        {
            foreach (var key in keys)
            {
                if (get(key) is not { } text)
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
        if (get("mode") is { } mode)
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
