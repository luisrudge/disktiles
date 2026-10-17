# disktiles: how it is put together

Each feature lives in its own file and wires itself in from `Install(App app)`.
Shared files change only at the registration points below.

`src/Disktiles.Core` holds the scan, the tree, layout, the filter and removal,
with no UI. `src/Disktiles` is the GTK app (assembly `disktiles`, app id
`io.github.luisrudge.disktiles`); the files below are there unless a path says
otherwise.

## Files

| File | What |
|---|---|
| `Program.cs` | Entry point: the root folder and the Gtk.Application. The application id is the Wayland app id (Hyprland's window class). |
| `App.cs` | Builds the window: header, treemap and sidebar split, footer, screens stack, key controller. Then one `X.Install(this)` line per feature. |
| `AppState.cs` | `Screen` enum and `AppState`: Root, Tree, Crumbs (folder shown), Selected (keyboard selection), Hovered, Metric, Layout (LayoutOptions), Options (ScanOptions), Screen, plus change events. Also Resolve, PathOf, Tilde. |
| `Keymap.cs` | The one key dispatcher: bindings with keys, group and description; modal grabs. |
| `TreemapView.cs` | Texture render, hover outline and card, hit test, click and scroll entry points, paint hook. |
| `HoverCard.cs` | The card's widgets and CSS (including `.outline` and `.hud`). |
| `Header.cs` | Top row: `Start` (trail) and `End` (view controls), then the legend (`Categories`; Age mode hides it for its ramp). |
| `Cli.cs` | The command line (`disktiles --help`) and the power presets; `Cli.Current` is read by Scanning before the first scan. |
| `Footer.cs` | The key-hint bar: `Hints` (filled by Omarchy.cs from the Keymap), `End` ("? all keys") and the status line. |
| `Sidebar.cs` | Slots for sections, shared label helpers, panel CSS. |
| `SelectionSection.cs`, `WorthSection.cs`, `DiskSection.cs` | The panel's sections. |
| `Look.cs` | The palette entry point: colours from the current theme (Fill, Accent, AgeFill, AgeAccent, Highlight, Danger, Label, HatchColour), labels, HumanBytes, HumanCount, Ago, Hatch. |
| `Theme.cs` | `Rgba` and `OmarchyTheme`: the Omarchy theme, read from `colors.toml` and followed live. See OMARCHY.md. |
| `TestHooks.cs` | The DISKTILES_* switches, including the fps overlay. |
| `Navigation.cs` (1), with `History.cs`, `FileManager.cs`, `InterfaceZoom.cs`, `KeysHelp.cs` | Keyboard selection and pointer, Enter/Backspace/Escape, clicks, `p` panel, `q`; back/forward and the `<` `>` buttons; `o`/right-click reveal (GTK FileLauncher: the portal's OpenDirectory, Nautilus on Omarchy); ctrl = - 0; the `?` overlay (wraps the window's child in an Overlay). |
| `Zoom.cs` (2) `Scanning.cs` (3) `Trail.cs` (4) `Marking.cs` (5) `Omarchy.cs` (6) | One file per feature area. Put new files for an area behind its own `Install`. Scanning (3) also installs `Modes.cs` (Size/Files/Age, Hidden, Apparent) and `Targets.cs` (volume picker, ctrl+o, g). |
| `FilterBar.cs`, `GitStatus.cs` | Feature 4's other parts, installed from `Trail.Install`: the `/` filter, and git in the selection. |
| `MarkedPanel.cs`, `Review.cs` (5) | Marking's panel parts (mark button, Marked section, notice, free-after-marks projection) and the Review, Running and Done screens. The guards, trash, export and marks themselves are core: `src/Disktiles.Core/Removal.cs`, `Export.cs`, `Marks.cs`, tested by `tests/RemovalTests` (`dotnet run --project tests/RemovalTests`) against fixtures under /tmp. |

## State and events

Change state only through AppState's methods. Each method fires its event:

- `SetTree(root, tree)` fires TreeChanged, then FolderChanged.
- `GoTo(crumbs)` and `Up()` fire FolderChanged. `Up()` returns false at the root. GoTo also makes the folder the selection and forgets the hover (`PointerActive = false`).
- Keys that act on "the current tile" call `AdoptPointer()` first, then use `Selected`; or read `ActionTarget` (the hover if the pointer moved last, else the selection). `CrumbsOf(path)` is PathOf backwards.
- `Select(at)` fires SelectionChanged.
- `Hover(at)` fires HoverChanged.
- `SetMetric`, `SetColorMode` and `SetLayout` fire LayoutChanged.
- `Resort(tree, metric, crumbs, selected)` (a re-sorted copy for Files or Size, `Tree.Resorted`) fires TreeChanged, then FolderChanged: every crumb moved.
- `State.Tree` is the root's `NodeRef`, a handle (tree + slot) to an unmanaged node in the core's arena; `Resolve(crumbs)` gives any other. A tree never changes once shown (a re-sort is a copy, a scan a new tree), so a handle never goes stale, and the old arena is freed by the GC once nothing holds it. Read `Name` for display only (it decodes UTF-8 each call); find a child by name with `Children.IndexOf(name)`.
- A new scan: `Scanning.Start(root)` (State.Options says how); `Scanning.Root` is what `r` scans again, `Scanning.Running` the scan in flight.
- `SetScreen` fires ScreenChanged.
- `SetFilter(matches, applied)` fires FilterChanged. `SetTree` drops the filter, and `GoTo` above the folder it was typed in lets it lapse, both quietly (no FilterChanged; the treemap paints once, for the folder).

The treemap re-renders on FolderChanged, LayoutChanged, FilterChanged and resize. It dims what `State.Matches` leaves out and, while `FilterApplied`, lays out only the matches. The selection section shows whichever of the folder, the selection or the hover changed last.

Handlers run in subscription order. The sidebar sections are constructed before the treemap view on purpose.

## Extension points

```csharp
// A key. Names are GDK keyval names with modifiers in front: "Escape", "BackSpace",
// "Return", "question", "bracketleft", "ctrl+o", "alt+Left", "shift+Tab".
// Shift appears only on keys that type nothing (shift+a is "A").
// Space-separated alternatives. on: Screen.Explore by default, null for every screen.
app.Keys.Bind("bracketright", "View", "deeper", () => app.State.SetLayout(app.State.Layout with { MaxDepth = app.State.Layout.MaxDepth + 1 }));
// Func<bool>: return false to let the next binding for the key try.
// A higher priority goes first: Escape clears a filter before it goes up.
app.Keys.Bind("Escape", "Filter", "clear filter", () => filter.Clear(), priority: 10);
app.Keys.Bind("Escape", "Review", "back", () => app.State.SetScreen(Screen.Explore), on: Screen.Review);
// A modal (help overlay, find field, dialog) sees every key first, until released.
// key.Text is the typed character.
Func<Key, bool> field = key => { /* ... */ return true; };
app.Keys.Grab(field); /* later */ app.Keys.Release(field);
// app.Keys.Bindings lists every binding (Keys, Group, Description, On) for a "?" overlay.

// Header controls and the trail.
app.Header.End.Append(depthControl);   // install order sets the order
app.Header.Start.Append(trailWidget);
app.Header.Under.Append(widget);       // under the trail, before the legend: scan totals, filter

// Footer. Key hints come from the Keymap by themselves (Omarchy.cs): a
// binding for space, Return, c, h, slash, bracketleft, t, 0, v, r or question
// shows its hint.
app.Footer.SetStatus("scan 257.6k entries · 1.4 s");

// A panel section in a slot: Selection, Worth, Marked, Notice, Disk (Disk is pinned at the bottom).
app.Sidebar.Add(Slot.Marked, new MarkedSection(app.State).Widget);
// Lines under the selection's facts (git status, mark button), updated as it changes.
app.Selection.Extra.Append(gitLine);
app.Selection.Shown += (crumbs, node) => gitLine.SetText(/* ... */);

// A screen: a whole-window page shown while State.Screen matches.
app.AddScreen(Screen.Review, reviewPage);
app.State.SetScreen(Screen.Review);

// The treemap.
app.Treemap.Clicked += (tile, button) => { /* 1 left, 3 right, 8 back, 9 forward */ };
app.Treemap.Scrolled += (x, y, dx, dy, modifiers) => { /* zoom; dx, dy in wheel notches, down positive */ };
app.Treemap.Painted += cr => { /* draw into the texture: marks, dimming; base-space coordinates, the view's transform is applied */ };
app.Treemap.AddOverlay(widget);        // CanTarget = false
app.Treemap.Render();                  // only when the picture really changes (marks, filter)
app.Treemap.Tiles; app.Treemap.Hit(x, y); app.Treemap.Hovered;
// Zoom (View.cs): screen = (base - origin) * scale. Tiles stay in base space;
// Hit takes screen coordinates. A new folder resets the view to identity.
app.Treemap.View; app.Treemap.SetView(view); app.Treemap.ViewChanged += ...;
app.Treemap.View.Project(tile.Rect);    // where a tile is on screen
app.Treemap.Body(crumbs);               // a drawn directory's rect below its name band

// CSS. Use the theme's named colours, never literals, so a theme switch
// restyles it: @dt_background @dt_surface @dt_inset @dt_foreground
// @dt_secondary @dt_bright @dt_accent @dt_border @dt_danger @dt_warning
// @dt_success @dt_highlight @dt_on_highlight, e.g. alpha(@dt_foreground, 0.12).
app.AddCss(".marked { color: @dt_danger; }");
// Cairo: cr.SetSource(Look.Danger); Look.Fill(category, depth). Read Look.*
// at paint time, not once: the theme changes under you (Render() follows).
```

## Test hooks (TestHooks.cs)

Only `DISKTILES_LOG` and `DISKTILES_TRACE` exist in every build: they only print.
The rest act for the user, so they exist only in debug builds and in
`dotnet publish -p:TestHooks=true`.

| Switch | What |
|---|---|
| `DISKTILES_FPS=1` | fps overlay (also toggled by `f`), and one line a second on stderr |
| `DISKTILES_TRACE=1` | Pointer event trace |
| `DISKTILES_HOVER=x,y` | Simulated pointer at one point |
| `DISKTILES_SWEEP=1` (or `=nopanel`) | Pointer that sweeps the treemap every frame |
| `DISKTILES_CLICK=x,y[,button[,presses]]` | A click; `;` between several. A left click selects; a click on the selection, or a double click (presses 2), opens it (a file: its folder) |
| `DISKTILES_POINT=x,y` | A pointer that moved there: keys act on the tile under it |
| `DISKTILES_REVEAL=dry` | `o` and right-click log the path instead of opening the file manager (never open Nautilus on the user's screen) |
| `DISKTILES_SCROLL=x,y,dy[,shift][,N][;...]` | A scroll step of dy notches, N times; `;` chains more (logs the view after each) |
| `DISKTILES_ZOOMSWEEP=1` (`=magnify`) | A wheel spun in and out, 15 notches/s, near the centre (`magnify`: never going in or up) |
| `DISKTILES_KEYS="Down Down Return ctrl+o"` | Key presses through the same Keymap.Press as real keys |
| `DISKTILES_LOG=1` | One stderr line per scan start, progress tick, landing, cancel, mode change and volume listing |
| `DISKTILES_PICK=/path` | ctrl+o takes this folder instead of opening the portal's chooser, which would open on the real, focused screen |
| `DISKTILES_CRUMB=n[,menu]` | Press the n-th trail crumb (from 0, as shown), or open its ▾ sibling menu |
| `DISKTILES_SNAP=file.png[,s]` | The window rendered to a PNG by GTK's own renderer, s seconds (default 2) after the steps. It works while the screen is locked: the compositor never takes part |
| `DISKTILES_SAVE_TO=file`, `DISKTILES_CLIPBOARD_TO=file` | The review's `s` and `a` write here instead of the portal's save dialog and the clipboard |

With `DISKTILES_TRACE=1` the trail, the filter, the sibling menu and git also log what they show.

Coordinates are logical pixels in the treemap. After the first scan, once there are tiles, the click plays first, then the scroll, then the crumb, then the keys, 150 ms apart. Each step logs `test: ... folder [crumbs], selected [crumbs]` to stderr. A new hook is another `steps.Enqueue(("what", () => ...))` in `AfterScan`.

## Performance rules (measured on an old dual-core laptop; keep these)

- The treemap is painted with Cairo into an ImageSurface at device pixels and shown as a MemoryTexture by a Gtk.Picture. Render only when the layout or the painted content changes. Never on hover.
- Zoom and pan never repaint during the motion: two pictures in a Gtk.Fixed (the base, the whole layout at scale 1, and the detail, the view as last settled) get a child transform each, and the view is painted sharp 150 ms after the last change. Going in or out of a directory eases the new texture from where that region was (a transform too). Check with `DISKTILES_ZOOMSWEEP=magnify DISKTILES_FPS=1`: about 60 frames/s, redraws only when the wheel stops.
- The hover outline is four strip widgets. The hover card is measured once per content change, with its margins subtracted. An identical motion over the same tile must do nothing: GTK re-sends the last position after every relayout.
- Panel updates are throttled to 10/s (SelectionSection). Anything that follows the hover must be throttled the same way.
- Ellipsized labels use MaxWidthChars = 1 and Hexpand, so the panel width never follows the text.
- Check with `DISKTILES_SWEEP=1 DISKTILES_FPS=1` in a 2560x1600 window. You should see about 60 frames/s and "treemap redraws 0/s".
