// The window and its parts, put together once; every feature then wires
// itself in from its own file (see Install below and ARCHITECTURE.md).
namespace Disktiles;

sealed class App
{
    public readonly Gtk.ApplicationWindow Window;
    public readonly AppState State;
    public readonly Keymap Keys;
    public readonly Header Header;
    public readonly Footer Footer;
    public readonly Sidebar Sidebar;
    public readonly SelectionSection Selection;
    public readonly WorthSection Worth;
    public readonly DiskSection Disk;
    public readonly TreemapView Treemap;
    readonly Gtk.Stack _screens;

    public App(Gtk.Application application, string root)
    {
        Window = Gtk.ApplicationWindow.New(application);
        Window.Title = "disktiles";
        Window.SetDefaultSize(1440, 900);
        AddCss(Sidebar.Css() + HoverCard.Css());

        State = new AppState(root);
        Keys = new Keymap(State);
        Header = new Header();
        Footer = new Footer();
        Sidebar = new Sidebar();
        // The panel's sections before the treemap: their handlers run first.
        Selection = new SelectionSection(State);
        Worth = new WorthSection(State);
        Disk = new DiskSection(State);
        Sidebar.Add(Slot.Selection, Selection.Widget);
        Sidebar.Add(Slot.Worth, Worth.Widget);
        Sidebar.Add(Slot.Disk, Disk.Widget);
        Treemap = new TreemapView(State, Window);

        // The treemap and the panel, with a divider to drag.
        var split = Gtk.Paned.New(Gtk.Orientation.Horizontal);
        split.StartChild = Treemap.Widget;
        split.EndChild = Sidebar.Widget;
        split.ResizeEndChild = false;
        split.ShrinkEndChild = false;
        split.Vexpand = true;

        var explore = Gtk.Box.New(Gtk.Orientation.Vertical, 0);
        explore.Append(Header.Widget);
        explore.Append(split);
        explore.Append(Footer.Widget);
        _screens = Gtk.Stack.New();
        AddScreen(Screen.Explore, explore);
        State.ScreenChanged += () => _screens.SetVisibleChildName(State.Screen.ToString());
        Window.Child = _screens;

        // Capture phase: the keymap sees a key before the focused widget
        // does, so a list row with focus never eats an arrow or Escape.
        var keys = Gtk.EventControllerKey.New();
        keys.PropagationPhase = Gtk.PropagationPhase.Capture;
        keys.OnKeyPressed += (_, e) => Keys.Press(e.Keyval, e.State);
        Window.AddController(keys);

        Window.Present();

        // One line per feature, each in its own file.
        Navigation.Install(this);
        Zoom.Install(this);
        Scanning.Install(this);
        Trail.Install(this);
        Marking.Install(this);
        Omarchy.Install(this);
        TestHooks.Install(this);
    }

    /// A whole-window page shown while State.Screen is `screen`.
    public void AddScreen(Screen screen, Gtk.Widget page) => _screens.AddNamed(page, screen.ToString());

    public void AddCss(string css)
    {
        var provider = Gtk.CssProvider.New();
        provider.LoadFromString(css);
        Gtk.StyleContext.AddProviderForDisplay(Window.GetDisplay(), provider, Gtk.Constants.STYLE_PROVIDER_PRIORITY_APPLICATION);
    }
}
