// What the app is looking at: the scan, the folder shown, what the keys act
// on, how it is measured. Views listen to its events; features change it
// through its methods, never through a view.
using Disktiles.Core;

namespace Disktiles;

sealed class AppState(string root)
{
    public readonly string Home = Removal.HomeDir() ?? "/";
    public string Root { get; private set; } = root;
    /// When the tree was scanned: ages ("3 days ago") count from here.
    public long Now { get; private set; } = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    public ScanOptions Options { get; set; } = new();
    public NodeRef? Tree { get; private set; }
    /// The folder shown, as child indices from the root.
    public int[] Crumbs { get; private set; } = [];
    /// What the keys act on; separate from the hover, which follows the pointer.
    public int[]? Selected { get; private set; }
    /// The tile under the pointer, when it is a node.
    public int[]? Hovered { get; private set; }
    /// The pointer moved more recently than the keyboard navigated: keys
    /// act on the tile under it, not on the keyboard selection.
    public bool PointerActive { get; set; }
    /// What Space, X, Enter and `o` act on.
    public int[]? ActionTarget => PointerActive ? Hovered ?? Selected : Selected;
    public Metric Metric { get; private set; } = Metric.Bytes;
    public ColorMode ColorMode { get; private set; }
    /// The header bands at 100% interface zoom: 1.375 and 1 rem of 16 px.
    public static readonly LayoutOptions DefaultLayout = new() { Header = 22, HeaderInner = 16 };
    public LayoutOptions Layout { get; private set; } = DefaultLayout;
    public Screen Screen { get; private set; }
    /// What the filter text matches in the folder it was typed in. While
    /// typing it only dims what does not match.
    public Matches? Matches { get; private set; }
    /// Enter was pressed: the treemap lays out only the matches.
    public bool FilterApplied { get; private set; }

    /// A new scan: everything else starts over. FolderChanged follows.
    public event Action? TreeChanged;
    public event Action? FolderChanged;
    public event Action? SelectionChanged;
    public event Action? HoverChanged;
    /// Metric or layout options changed: the treemap lays out again.
    public event Action? LayoutChanged;
    public event Action? ScreenChanged;
    /// Matches or FilterApplied changed: the treemap paints again.
    public event Action? FilterChanged;

    public NodeRef? Current => Resolve(Crumbs);

    public void SetTree(string root, NodeRef tree)
    {
        Root = root;
        Tree = tree;
        Now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Crumbs = [];
        Selected = Hovered = null;
        Matches = null;
        FilterApplied = false;
        TreeChanged?.Invoke();
        FolderChanged?.Invoke();
    }

    /// The folder becomes the selection, and the hover is stale until the
    /// pointer moves again.
    public void GoTo(int[] crumbs)
    {
        Crumbs = crumbs;
        Selected = crumbs;
        Hovered = null;
        PointerActive = false;
        // A filter is about the folder it was typed in: above it, it would
        // hide everything beside that folder, so it lapses. Quietly: the
        // treemap paints once, for the folder.
        if (Matches is { } matches && !crumbs.AsSpan().StartsWith(matches.Base))
        {
            Matches = null;
            FilterApplied = false;
        }
        FolderChanged?.Invoke();
    }

    /// One folder up; false at the root.
    public bool Up()
    {
        if (Crumbs.Length == 0)
        {
            return false;
        }
        GoTo(Crumbs[..^1]);
        return true;
    }

    public void Select(int[]? at)
    {
        Selected = at;
        SelectionChanged?.Invoke();
    }

    /// Before a key acts on "the current tile": the pointed-at tile becomes
    /// the selection if the pointer moved last (`adopt_pointer_target`).
    public void AdoptPointer()
    {
        if (PointerActive && Hovered is { } at)
        {
            Selected = at;
        }
    }

    public void Hover(int[]? at)
    {
        Hovered = at;
        HoverChanged?.Invoke();
    }

    public void SetMetric(Metric metric)
    {
        Metric = metric;
        LayoutChanged?.Invoke();
    }

    /// The colours are painted into the treemap: it lays out again.
    public void SetColorMode(ColorMode mode)
    {
        ColorMode = mode;
        LayoutChanged?.Invoke();
    }

    /// The same tree, re-sorted for `metric`: every crumb moved, so the
    /// folder and the selection come with their new crumbs. TreeChanged
    /// (the panel recounts), then FolderChanged.
    public void Resort(NodeRef tree, Metric metric, int[] crumbs, int[]? selected)
    {
        Tree = tree;
        Metric = metric;
        Crumbs = crumbs;
        Selected = selected;
        Hovered = null;
        // The filter's crumbs are indices into the old order.
        Matches = null;
        FilterApplied = false;
        TreeChanged?.Invoke();
        FolderChanged?.Invoke();
    }

    public void SetLayout(LayoutOptions layout)
    {
        Layout = layout;
        LayoutChanged?.Invoke();
    }

    public void SetFilter(Matches? matches, bool applied)
    {
        Matches = matches;
        FilterApplied = applied && matches is not null;
        FilterChanged?.Invoke();
    }

    public void SetScreen(Screen screen)
    {
        Screen = screen;
        ScreenChanged?.Invoke();
    }

    public NodeRef? Resolve(int[] at)
    {
        if (Tree is not { } node)
        {
            return null;
        }
        foreach (var index in at)
        {
            if (index >= node.Children.Length)
            {
                return null;
            }
            node = node.Children[index];
        }
        return node;
    }

    public string PathOf(int[] at)
    {
        var parts = new List<string> { Root };
        var node = Tree!.Value;
        foreach (var index in at)
        {
            node = node.Children[index];
            parts.Add(node.Name);
        }
        return Path.Join([.. parts]);
    }

    /// PathOf backwards: null when the scan
    /// does not hold it. Paths outlive crumbs, which a new tree reorders.
    public int[]? CrumbsOf(string path)
    {
        // By components: `..foo` is a name, not a step up.
        if (Space.StripPrefix(path, Root) is not { } relative)
        {
            return null;
        }
        if (relative.Length == 0)
        {
            return Tree is null ? null : [];
        }
        if (Tree is not { } node)
        {
            return null;
        }
        var crumbs = new List<int>();
        foreach (var name in Space.Components(relative))
        {
            var index = node.Children.IndexOf(name);
            if (index < 0)
            {
                return null;
            }
            crumbs.Add(index);
            node = node.Children[index];
        }
        return [.. crumbs];
    }

    public string Tilde(string full) =>
        full == Home ? "~" : full.StartsWith(Home + Path.DirectorySeparatorChar, StringComparison.Ordinal) ? "~" + full[Home.Length..] : full;
}
