// Navigation and keys (feature 1): the keyboard selection and the pointer,
// going in and out, back and forward, the panel, the file manager, interface
// zoom, the "?" overlay, quitting.
//
// Space, X, Enter and the arrows act on the tile under the mouse if the
// mouse moved last, and on the keyboard selection after an arrow or Tab.
using System.Diagnostics;
using Disktiles.Core;

namespace Disktiles;

/// One row of the "?" overlay.
sealed record HelpRow(string Keys, string Does);

sealed partial class App
{
    enum Direction { Left, Right, Up, Down }

    /// `p` shows or hides the panel (QML also hides it in a narrow window).
    public bool PanelShown { get; private set; } = true;
    /// The "?" overlay is open.
    public bool HelpShown { get; private set; }

    void InitNavigation()
    {
        var state = State;
        Keys.Bind("Return", "Navigate", "Open that directory, at any depth", () =>
        {
            state.AdoptPointer();
            Descend();
        });
        Keys.Bind("BackSpace u", "Navigate", "Go up one directory", state.Up);
        // The selection goes first, then a level.
        Keys.Bind("Escape", "Navigate", "Go up one directory", () =>
        {
            if (state.Selected is null)
            {
                return state.Up();
            }
            state.Select(null);
            return true;
        });
        foreach (var (keys, direction) in new[]
        {
            ("Left h", Direction.Left), ("Right l", Direction.Right), ("Up k", Direction.Up), ("Down j", Direction.Down),
        })
        {
            Keys.Bind(keys, "Navigate", "Move between tiles at this level", () =>
            {
                state.AdoptPointer();
                Move(direction);
            });
        }
        Keys.Bind("Tab", "Navigate", "Next largest sibling", () => { state.AdoptPointer(); Cycle(1); });
        Keys.Bind("shift+Tab", "Navigate", "Next largest sibling", () => { state.AdoptPointer(); Cycle(-1); });
        Keys.Bind("q ctrl+q ctrl+w", "App", "Quit", () => Emit(new Effect("quit")));

        // A scan lands with its largest entry selected (`select_largest`);
        // a re-sort (Files mode) keeps the selection it found again.
        state.TreeChanged += () =>
        {
            if (state.Selected is null && state.Tree is { Children.Length: > 0 })
            {
                state.Select([0]);
            }
        };

        Keys.Bind("p", "View", "Show or hide the selection line", () =>
        {
            PanelShown = !PanelShown;
            Touch(Part.Panel);
        });
        Keys.Bind("o ctrl+R", "Navigate", "Show it in the file manager", () => Reveal(state.ActionTarget ?? state.Crumbs));
        InitHistory();
        InitInterfaceZoom();
        Keys.Bind("question", "App", "Every key", () =>
        {
            HelpShown = true;
            Keys.Grab(HelpKeys);
            Touch(Part.Overlay);
        }, on: null);
    }

    // While open, the overlay takes the keyboard; ?, /, q and Escape close it.
    bool HelpKeys(Key key)
    {
        if (key.Name is "Escape" or "question" or "slash" or "q")
        {
            CloseHelp();
        }
        return true;
    }

    public void CloseHelp()
    {
        HelpShown = false;
        Keys.Release(HelpKeys);
        Touch(Part.Overlay);
    }

    /// A press on the treemap: 1 left, 2 middle, 3 right, 8/9 back/forward;
    /// `presses` 2 for a double click; `control` held.
    public void Click(double x, double y, int button, int presses, bool control)
    {
        if (State.Tree is null || State.Screen != Screen.Explore)
        {
            return;
        }
        var tile = Hit(x, y);
        // Ctrl-click and the middle button mark without going in or moving the selection.
        if (button == 2 || (button == 1 && control))
        {
            if (tile is { Kind: TileKind.Node })
            {
                ToggleMark(tile.Crumbs);
            }
            return;
        }
        var crumbs = tile is { Kind: TileKind.Node } ? tile.Crumbs : null;
        switch (button)
        {
            // A double click opens what it hits; for a file, the folder holding it.
            case 1 when crumbs is not null && presses >= 2:
                State.Select(crumbs);
                Descend();
                break;
            // Select; a click on the selection opens it, like a file manager.
            case 1 when crumbs is not null && State.Selected is { } selected
                    && selected.AsSpan().SequenceEqual(crumbs) && State.Resolve(crumbs) is { IsDir: true }:
                Descend();
                break;
            case 1:
                State.Select(crumbs);
                break;
            // What `o` does, for the tile under the pointer.
            case 3 when crumbs is not null:
                State.Select(crumbs);
                Reveal(crumbs);
                break;
            case 8 or 9:
                Step(back: button == 8);
                break;
        }
    }

    /// What is selected below this folder, however deep (a file opens the
    /// directory holding it); else the largest entry.
    void Descend()
    {
        var state = State;
        var target = state.Selected is { } selected && selected.Length > state.Crumbs.Length
            ? state.Resolve(selected) is { IsDir: true } ? selected : selected[..^1]
            : state.Current is { Children.Length: > 0 } ? [.. state.Crumbs, 0] : null;
        if (target is not null && target.Length > state.Crumbs.Length
            && target.AsSpan().StartsWith(state.Crumbs) && state.Resolve(target) is { IsDir: true, Children.Length: > 0 })
        {
            state.GoTo(target);
        }
    }

    /// The nearest tile at the same depth that way, falling back to the
    /// parent at the left or top edge.
    void Move(Direction direction)
    {
        var state = State;
        state.PointerActive = false;
        var tiles = Tiles.Where(t => t.Kind == TileKind.Node).ToList();
        var current = state.Selected is { } s ? tiles.Find(t => t.Crumbs.AsSpan().SequenceEqual(s)) : null;
        if (current is null)
        {
            // The selection is not drawn (the folder itself, after Enter):
            // start from the largest tile.
            if (tiles.Count > 0)
            {
                state.Select(tiles[0].Crumbs);
            }
            return;
        }
        // Same-depth neighbours only: stepping into a child by arrow key
        // would make the depth of the selection impossible to predict.
        var from = current.Rect;
        Tile? best = null;
        var bestScore = float.MaxValue;
        foreach (var tile in tiles)
        {
            if (tile.Crumbs.Length != current.Crumbs.Length || tile == current || Gap(direction, from, tile.Rect) is not { } gap)
            {
                continue;
            }
            var score = Offset(direction, from, tile.Rect) * 2.5f + gap;
            if (score < bestScore)
            {
                (best, bestScore) = (tile, score);
            }
        }
        if (best is not null)
        {
            state.Select(best.Crumbs);
        }
        else if (direction is Direction.Left or Direction.Up && state.Crumbs.Length > 0)
        {
            state.Select(state.Crumbs[..^1]);
        }
    }

    // Distance from `from` to `rect` along the axis, if `rect` lies that way.
    static float? Gap(Direction direction, Rect from, Rect rect)
    {
        var gap = direction switch
        {
            Direction.Right => rect.X - from.Right,
            Direction.Left => from.X - rect.Right,
            Direction.Down => rect.Y - from.Bottom,
            _ => from.Y - rect.Bottom,
        };
        return gap >= -0.5f ? Math.Max(gap, 0) : null;
    }

    // How far off the axis `rect` sits, so the nearest tile that way wins
    // rather than any tile in that half-plane.
    static float Offset(Direction direction, Rect from, Rect rect)
    {
        static float Overlap(float a0, float a1, float b0, float b1) => Math.Max(Math.Min(a1, b1) - Math.Max(a0, b0), 0);
        return direction is Direction.Left or Direction.Right
            ? Math.Max(Math.Min(from.H, rect.H) - Overlap(from.Y, from.Bottom, rect.Y, rect.Bottom), 0)
            : Math.Max(Math.Min(from.W, rect.W) - Overlap(from.X, from.Right, rect.X, rect.Right), 0);
    }

    /// The next entry by rank, which is the next largest by the metric.
    /// Scanning a directory for space is this walk.
    void Cycle(int step)
    {
        var state = State;
        state.PointerActive = false;
        var parent = state.Selected is { } s && s.Length > state.Crumbs.Length ? s[..^1] : state.Crumbs;
        if (state.Resolve(parent) is not { Children.Length: > 0 and var count })
        {
            return;
        }
        var at = state.Selected is { } selected && selected.Length == parent.Length + 1
            && selected.AsSpan().StartsWith(parent) ? selected[^1] : -1;
        var next = at < 0 ? (step >= 0 ? 0 : count - 1) : ((at + step) % count + count) % count;
        state.Select([.. parent, next]);
    }

    // ── the file manager ────────────────────────────────────────────────

    /// `o`, right-click and ctrl+shift+R: show the tile in the file manager.
    /// The FileManager1 interface Nautilus (and most file managers) answers
    /// on the session bus selects the file in its folder; without one, the
    /// folder is opened with xdg-open. --test-reveal dry logs it instead.
    public void Reveal(int[] crumbs)
    {
        if (State.Tree is null)
        {
            return;
        }
        var path = State.PathOf(crumbs);
        if (!Path.Exists(path) && Space.LinkTarget(path) is null)
        {
            SetStatus($"{State.Tilde(path)} is no longer on disk");
            return;
        }
        if (Hook("reveal") == "dry")
        {
            Console.Error.WriteLine($"reveal: {path}");
            return;
        }
        var uri = "file://" + Removal.PercentEncode(path);
        _ = Task.Run(() =>
        {
            var shown = Run("gdbus", "call", "--session", "--dest", "org.freedesktop.FileManager1",
                "--object-path", "/org/freedesktop/FileManager1", "--method", "org.freedesktop.FileManager1.ShowItems",
                $"['{uri}']", "\"\"")
                || Run("xdg-open", Removal.Parent(path) ?? path);
            if (!shown)
            {
                Ui.Post(() => SetStatus($"Could not show {State.Tilde(path)}: no file manager answered"));
            }
        });

        static bool Run(string program, params string[] args)
        {
            try
            {
                var start = new ProcessStartInfo(program) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var arg in args)
                {
                    start.ArgumentList.Add(arg);
                }
                using var process = Process.Start(start)!;
                process.StandardOutput.ReadToEnd();
                process.WaitForExit();
                return process.ExitCode == 0;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return false;
            }
        }
    }

    // ── interface zoom ──────────────────────────────────────────────────

    static readonly double[] ZoomSteps = [0.75, 0.875, 1.0, 1.125, 1.25, 1.5, 1.75];
    const int NormalZoom = 2;
    int _zoomStep = NormalZoom;

    /// ctrl = / ctrl - / ctrl 0: text, the treemap's bands and labels, and
    /// the panel scale by this.
    public double UiZoom => ZoomSteps[_zoomStep];

    /// The header bands at 100% interface zoom.
    public static readonly LayoutOptions DefaultLayout = new() { Header = 22, HeaderInner = 16 };

    void InitInterfaceZoom()
    {
        void Set(int step)
        {
            if (step == _zoomStep)
            {
                return;
            }
            _zoomStep = step;
            State.SetLayout(State.Layout with
            {
                Header = DefaultLayout.Header * (float)UiZoom,
                HeaderInner = DefaultLayout.HeaderInner * (float)UiZoom,
            });
            Touch(Part.All);
        }
        Keys.Bind("ctrl+equal ctrl+plus ctrl+KP_Add", "View", "Interface zoom", () => Set(Math.Min(_zoomStep + 1, ZoomSteps.Length - 1)), on: null);
        Keys.Bind("ctrl+minus ctrl+KP_Subtract", "View", "Interface zoom", () => Set(Math.Max(_zoomStep - 1, 0)), on: null);
        Keys.Bind("ctrl+0 ctrl+KP_0", "View", "Interface zoom", () => Set(NormalZoom), on: null);
    }

    // ── the "?" overlay ─────────────────────────────────────────────────

    public static readonly HelpRow[] HelpRows =
    [
        new("space / x", "Mark or unmark the tile you point at"),
        new("ctrl-click", "Mark without moving the selection"),
        new("enter", "Open that directory, at any depth"),
        new("⌫ / esc", "Go up one directory"),
        new("alt ← / →", "Back or forward, as do the mouse's side buttons"),
        new("← ↑ ↓ → / hjkl", "Move between tiles at this level"),
        new("tab", "Next largest sibling"),
        new("scroll", "Zoom toward a directory, then go into it"),
        new("shift-scroll", "Pan the magnified view"),
        new("[ / ]", "Draw fewer or more levels at once"),
        new("- / = / 0", "Magnify, shrink, or reset the view"),
        new("ctrl = / - / 0", "Interface zoom"),
        new("/", "Filter by name: only matches keep their colour; enter shows only them"),
        new("c", "Review the marked list"),
        new("t", "Size, files or age: what areas and colours say"),
        new("r", "Scan again from the same root"),
        new("esc", "Stop a scan in progress"),
        new("v", "Scan another volume"),
        new("ctrl-o", "Choose another directory to scan"),
        new("g", "The whole disk; click any directory above to widen"),
        new("d", "Disk usage or apparent size"),
        new("i", "Include or skip hidden entries"),
        new("p", "Show or hide the selection line"),
        new("o", "Show it in the file manager"),
        new("q", "Quit"),
        new("Review screen", "m trash · p permanent · ! unmark all · s save list · a copy as prompt"),
        new("", "enter commits · esc goes back"),
        new("", "A permanent deletion always asks first"),
    ];

    public string HelpFoot => $"? or esc closes · {State.Root} · {(State.Metric == Metric.Files ? "files" : "size")}";
}
