// The app without its window: everything the GTK version did, as state and
// view models, with QML as a thin renderer over them (Backend.cs). Each
// feature lives in its own file as a part of this class and wires itself in
// from its Init method, called below in the GTK app's install order.
//
// Nothing here knows about Qt: the tests build it with a hand-cranked
// dispatcher, and QML talks to it only through Backend.
using Disktiles.Core;

namespace Disktiles;

/// Which page the window shows.
enum Screen { Explore, Review, Running, Done }

/// What a tile's colour says.
enum ColorMode { Kind, Age }

/// What changed, so Backend tells QML only about those view models.
[Flags]
enum Part
{
    None = 0,
    Tiles = 1 << 0,
    View = 1 << 1,
    Hover = 1 << 2,
    Selection = 1 << 3,
    Panel = 1 << 4,
    Header = 1 << 5,
    Footer = 1 << 6,
    Scan = 1 << 7,
    Screen = 1 << 8,
    Review = 1 << 9,
    Overlay = 1 << 10,
    Palette = 1 << 11,
    /// The pointer moved over the same tile: only the card's position.
    Pointer = 1 << 12,
    All = ~0,
}

/// The UI thread, as the app needs it: post work back to it, and tick on it.
interface IDispatcher
{
    /// Run `action` on the UI thread, later. Safe from any thread.
    void Post(Action action);

    /// Call `tick` on the UI thread every `ms` until it returns false.
    void Every(int ms, Func<bool> tick);
}

/// Something only the window can do, asked of QML: copy, quit, a file dialog.
sealed record Effect(string Kind, string Text = "", string Extra = "");

sealed partial class App
{
    public readonly AppState State;
    public readonly Keymap Keys;
    public readonly Cli Cli;
    public readonly IDispatcher Ui;

    /// Some view models changed: Backend tells QML.
    public event Action<Part>? Changed;

    public App(Cli cli, IDispatcher ui)
    {
        Cli = cli;
        Ui = ui;
        State = new AppState(cli.Root);
        Keys = new Keymap(State);
        Removal.PrimeMountPoints();
        LoadTheme(systemIsDark: true);
        // The bridge watches the Omarchy theme and says when the user switches it.
        Omasharp.Theme.Changed += () => LoadTheme(_systemIsDark);

        // The GTK app's order: the panel's sections before the treemap, so on
        // a folder change the folder shows first, then the tile under the pointer.
        InitSelection();
        InitTreemap();
        InitNavigation();
        InitZoom();
        InitScanning();
        InitTrail();
        InitMarking();
        InitHooks();
        InitDrive();
        Start(cli.Root);
    }

    public void Touch(Part parts) => Changed?.Invoke(parts);

    /// The app asks the window for something only it can do (Backend raises it to QML).
    public event Action<Effect>? Effected;

    /// Ask the window to do something only it can.
    public void Emit(Effect effect) => Effected?.Invoke(effect);

    /// One line at the window's bottom right: what the scan or an action said.
    public string Status { get; private set; } = "";

    public void SetStatus(string text)
    {
        Status = text;
        Touch(Part.Footer);
    }

    /// `disktiles · ~/projects`.
    public string Title => $"disktiles · {State.Tilde(State.Root)}";

    /// A key press from QML (QtKeys.Describe), or from --test-keys.
    public bool Press(Key key) => Keys.Press(key);

    /// DISKTILES_LOG=1 (or --log): one line per scan, mode change and listing.
    public static readonly bool LogEnabled = Environment.GetEnvironmentVariable("DISKTILES_LOG") is not null;

    public void Log(string what)
    {
        if (LogEnabled || Cli.Hooks.ContainsKey("log"))
        {
            Console.Error.WriteLine($"log: {what}");
        }
    }

    // ── theme ───────────────────────────────────────────────────────────

    /// Read the Omarchy theme again (QML says when the host's theme moved).
    public void LoadTheme(bool systemIsDark)
    {
        _systemIsDark = systemIsDark;
        var next = OmarchyTheme.Current(Environment.GetEnvironmentVariable("HOME"), systemIsDark);
        if (next == Look.Theme && Palette is not null)
        {
            return;
        }
        Look.Theme = next;
        Palette = MakePalette(next);
        Log($"theme {next.Name} ({(next.Dark ? "dark" : "light")}) background {next.Background.Qml} accent {next.Accent.Qml}");
        // Every colour the tiles, legend and panel carry comes from it.
        Touch(Part.All);
    }

    bool _systemIsDark = true;

    /// The theme's roles for QML, derived as the GTK app derived them.
    public Dictionary<string, string> Palette { get; private set; } = null!;

    static Dictionary<string, string> MakePalette(OmarchyTheme t) => new()
    {
        ["background"] = t.Background.Qml, ["surface"] = t.Surface.Qml, ["inset"] = t.Inset.Qml,
        ["foreground"] = t.Foreground.Qml, ["secondary"] = t.Secondary.Qml, ["bright"] = t.Bright.Qml,
        ["accent"] = t.Accent.Qml, ["onAccent"] = t.OnAccent.Qml, ["selection"] = t.Selection.Qml, ["border"] = t.Border.Qml,
        ["danger"] = t.Danger.Qml, ["warning"] = t.Warning.Qml, ["success"] = t.Success.Qml,
        ["highlight"] = Look.Highlight.Qml, ["onHighlight"] = Look.OnHighlight.Qml,
        ["outline"] = t.Bright.Opacity(0.55).Qml, ["hatch"] = Look.HatchColour.Qml, ["labelDim"] = Look.LabelDim.Qml,
        ["dark"] = t.Dark ? "true" : "false", ["name"] = t.Name,
    };
}
