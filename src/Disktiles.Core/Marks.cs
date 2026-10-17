// The set of paths marked for removal.
// Keyed by absolute path, not tree position: the tree is scanned again after
// a removal, and a mark must survive that, or be reported as gone, rather
// than silently point at another node.
namespace Disktiles.Core;

public sealed class Marks
{
    readonly List<Target> _items = [];
    readonly HashSet<string> _index = [];

    /// In the order they were marked.
    public IReadOnlyList<Target> Items => _items;
    public int Count => _items.Count;
    public bool Contains(string path) => _index.Contains(path);

    /// Mark `target`, or unmark it when it is marked: whether it ended up marked.
    public bool Toggle(Target target)
    {
        if (_index.Remove(target.Path))
        {
            _items.RemoveAll(item => item.Path == target.Path);
            return false;
        }
        _index.Add(target.Path);
        _items.Add(target);
        return true;
    }

    public void Remove(string path)
    {
        if (_index.Remove(path))
        {
            _items.RemoveAll(item => item.Path == path);
        }
    }

    public void Clear()
    {
        _items.Clear();
        _index.Clear();
    }

    /// Sizes again from a fresh tree; a path that is gone weighs nothing,
    /// so the tally never claims space that is already free.
    public void Refresh(string rootPath, NodeRef root)
    {
        for (var i = 0; i < _items.Count; i++)
        {
            var item = _items[i];
            _items[i] = Find(rootPath, root, item.Path) is { } node
                ? item with { Bytes = node.Bytes, IsDir = node.IsDir, Hidden = IsHidden(item.Path) }
                : item with { Bytes = 0 };
        }
    }

    /// The node at an absolute path, by whole components, so a name holding
    /// a separator cannot confuse it.
    public static NodeRef? Find(string rootPath, NodeRef root, string path)
    {
        if (Space.StripPrefix(path, rootPath) is not { } relative)
        {
            return null;
        }
        var node = root;
        foreach (var name in Space.Components(relative))
        {
            var index = node.Children.IndexOf(name);
            if (index < 0)
            {
                return null;
            }
            node = node.Children[index];
        }
        return node;
    }

    /// Child indices from the root to the node at `path`, if it is still there.
    public static int[]? CrumbsFor(string rootPath, NodeRef root, string path)
    {
        if (Space.StripPrefix(path, rootPath) is not { } relative)
        {
            return null;
        }
        var crumbs = new List<int>();
        var node = root;
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

    public static bool IsHidden(string path) => Removal.FileName(path)?.StartsWith('.') ?? false;
}
